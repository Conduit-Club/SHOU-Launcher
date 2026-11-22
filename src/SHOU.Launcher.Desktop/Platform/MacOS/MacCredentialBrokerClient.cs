using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.Platform.MacOS;

/// <summary>Private anonymous pipes; verify the live child before sending any secret.</summary>
internal sealed class MacCredentialBrokerClient(string executable, string certificateSha1)
{
    internal const string Identifier = "club.conduit.shou.launcher.credentials.v1";
    private const uint Magic = 0x53484f55;
    private const int MaximumValue = 1048576;
    private readonly SemaphoreSlim gate = new(1, 1);

    internal static MacCredentialBrokerClient? ForCurrentApp()
    {
        var contents = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        if (!File.Exists(Path.Combine(contents, "Info.plist"))) return null; // dotnet run / unit test hosts
        var identity = MacCodeIdentity.ReadSelf();
        // PR packages are explicitly ad-hoc test builds. Their legacy storage
        // remains available, but they cannot invoke the fixed signed broker.
        if (identity.Identifier != "club.conduit.shou.launcher" || identity.CertificateSha1 is null) return null;
        var executable = Path.Combine(contents, "Helpers", "SHOU.CredentialBroker");
        if (!File.Exists(executable)) throw new CredentialStoreException(CredentialFailure.Unavailable, "候选包缺少凭据组件，请重新安装完整应用。");
        return new(executable, identity.CertificateSha1);
    }

    internal async Task<byte[]?> RequestAsync(string service, string key, uint operation,
        ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        ValidateName(service); ValidateName(key);
        if (value.Length > MaximumValue) throw new ArgumentOutOfRangeException(nameof(value));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(operation == 4 ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        Process? child = null; byte[]? response = null; Task? drain = null;
        var bytes = value.ToArray();
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            child = Process.Start(start) ?? throw new IOException("Credential component did not start.");
            drain = DrainAsync(child.StandardError, ct);
            var output = child.StandardOutput.BaseStream;
            var greeting = new byte[8];
            await output.ReadExactlyAsync(greeting, ct).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(greeting) != Magic) throw new InvalidDataException("Credential protocol mismatch.");
            KeychainCredentialStore.Check(BinaryPrimitives.ReadInt32LittleEndian(greeting.AsSpan(4)));
            await Task.Run(() => MacCodeIdentity.VerifyChild(child.Id, Identifier, certificateSha1), ct).ConfigureAwait(false);
            if (child.HasExited) throw new IOException("Credential component exited before validation.");
            // Nothing private is written to the pipe until the OS verifies this live child.
            var serviceBytes = Encoding.UTF8.GetBytes(service); var keyBytes = Encoding.UTF8.GetBytes(key);
            var header = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(header, operation);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)serviceBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)keyBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)bytes.Length);
            var input = child.StandardInput.BaseStream;
            await input.WriteAsync(header, ct).ConfigureAwait(false);
            await input.WriteAsync(serviceBytes, ct).ConfigureAwait(false);
            await input.WriteAsync(keyBytes, ct).ConfigureAwait(false);
            await input.WriteAsync(bytes, ct).ConfigureAwait(false);
            await input.FlushAsync(ct).ConfigureAwait(false);
            child.StandardInput.Close();
            await output.ReadExactlyAsync(greeting, ct).ConfigureAwait(false);
            var status = BinaryPrimitives.ReadInt32LittleEndian(greeting);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(greeting.AsSpan(4));
            if (length > MaximumValue || (status != 0 && length != 0)) throw new InvalidDataException("Credential response size is invalid.");
            response = new byte[(int)length];
            await output.ReadExactlyAsync(response, ct).ConfigureAwait(false);
            await child.WaitForExitAsync(ct).ConfigureAwait(false);
            await drain.ConfigureAwait(false);
            if (child.ExitCode != 0) throw new IOException("Credential component did not finish.");
            if (status == -25300) return null;
            KeychainCredentialStore.Check(status);
            var result = response; response = null; return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new CredentialStoreException(CredentialFailure.Unavailable, "钥匙串操作超时，原登录记录保留。"); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.ComponentModel.Win32Exception)
        { throw new CredentialStoreException(CredentialFailure.Unavailable, "凭据组件未完成操作，原登录记录保留。", ex); }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (response is not null) CryptographicOperations.ZeroMemory(response);
            if (child is not null)
            {
                try { if (!child.HasExited) child.Kill(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                child.Dispose();
            }
            deadline.Cancel();
            if (drain is not null)
            {
                try { await drain.ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            }
            gate.Release();
        }
    }

    private static void ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Contains('\0'))
            throw new ArgumentException("A nonempty bounded credential identifier is required.");
    }
    private static async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[512];
        while (await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false) != 0) { }
    }
}
