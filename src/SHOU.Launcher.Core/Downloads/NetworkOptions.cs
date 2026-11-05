using System.Net;
using System.Text;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Downloads;

public sealed class NetworkOptions
{
    private NetworkConfiguration current = new(new(), new(), 0);
    public ProxySettings Proxy => Capture().Proxy;
    public Aria2Settings Aria2 => Capture().Aria2;
    public ICredentialStore? Credentials { get; set; }
    public void Update(LauncherSettings settings) => Volatile.Write(ref current, new(settings.Proxy ?? new(), settings.Aria2 ?? new(), Interlocked.Increment(ref revision)));
    private long revision;
    public NetworkConfiguration Capture() => Volatile.Read(ref current);
}
public sealed record NetworkConfiguration(ProxySettings Proxy, Aria2Settings Aria2, long Revision);

/// <summary>System/Direct/Custom are handled without any legacy Windows registry initialization.</summary>
public sealed class ConfigurableNetworkHandler(NetworkOptions options, bool allowAutoRedirect = true) : HttpMessageHandler
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<long, HttpMessageInvoker> clients = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var configuration = options.Capture();
        var revision = configuration.Revision;
        HttpMessageInvoker invoker;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!clients.TryGetValue(revision, out invoker!))
            {
                var proxySettings = configuration.Proxy;
                IWebProxy? proxy = null;
                if (proxySettings.Mode == ProxyMode.Custom)
                {
                    proxy = new WebProxy(proxySettings.Address ?? throw new InvalidOperationException("Custom proxy address is missing."));
                    if (proxySettings.CredentialKey is { Length: > 0 } key)
                    {
                        var raw = await (options.Credentials ?? throw new CredentialStoreException(CredentialFailure.Unavailable, "Credential store unavailable.")).ReadAsync(key, cancellationToken).ConfigureAwait(false);
                        if (raw is null) throw new CredentialStoreException(CredentialFailure.Unavailable, "Proxy credential is missing.");
                        var text = Encoding.UTF8.GetString(raw); var split = text.IndexOf(':');
                        if (split < 0) throw new CredentialStoreException(CredentialFailure.Corrupt, "Proxy credential must use username:password format.");
                        proxy.Credentials = new NetworkCredential(text[..split], text[(split + 1)..]);
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
                    }
                }
                var handler = new SocketsHttpHandler { UseProxy = proxySettings.Mode != ProxyMode.Direct, Proxy = proxy, AllowAutoRedirect = allowAutoRedirect,
                    ConnectTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    AutomaticDecompression = DecompressionMethods.All };
                invoker = new HttpMessageInvoker(handler, true); clients.Add(revision, invoker);
            }
        }
        finally { gate.Release(); }
        return await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { foreach (var client in clients.Values) client.Dispose(); gate.Dispose(); }
        base.Dispose(disposing);
    }
}
