using System.Runtime.InteropServices;
using System.Text.Json;
using SHOU.Launcher.Core;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Core.Configuration;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;
using Avalonia;

namespace SHOU.Launcher.Desktop;

public static partial class Bootstrap
{
    private static PlatformServices? platformServices;
    private static IDataDirectoryLease? dataLease;
    private static HttpClient? httpClient;
    private static NetworkOptions? networkOptions;
    private static IVerifiedDownloader? verifiedDownloader;
    public static int Run(string[] args)
    {
        Arguments = args;
        var exitCode = 1;
        try
        {
            ValidateArguments();
            if (args.Contains("--smoke-test", StringComparer.Ordinal) && ArgumentValue("--exit-after") is null)
                Arguments = [.. args, "--exit-after", "20"];
            InitializeHostAsync().GetAwaiter().GetResult();
            var command = RunCommandAsync().GetAwaiter().GetResult();
            // Keep AppKit startup on the original STA/main thread, after async services have completed.
            exitCode = command ?? Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(Arguments);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Launcher startup/command failed: " + ex.GetType().Name);
            TryWriteFailureReport(ex, "startup_or_command");
            exitCode = 1;
        }
        finally
        {
            try { ShutdownHostAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.Error.WriteLine("Launcher shutdown failed: " + ex.GetType().Name); TryWriteFailureReport(ex, "shutdown"); exitCode = 1; }
        }
        return exitCode;
    }
    private static async Task InitializeHostAsync()
    {
        var data = ArgumentValue("--data-dir");
        if (data is null && OperatingSystem.IsWindows())
            data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SHOU-Launcher-Preview");
        networkOptions = new();
        httpClient = new(new ConfigurableNetworkHandler(networkOptions)) { Timeout = TimeSpan.FromSeconds(40), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SHOU-Launcher/1.5.0");
        verifiedDownloader = new DownloadCoordinator(httpClient, new VerifiedDownloader(httpClient), networkOptions,
            warning => Console.Error.WriteLine(warning));
        platformServices = PlatformServicesFactory.Create(new(data, ArgumentValue("--java-path")), verifiedDownloader);
        // Constructors above do not create data or read credentials. The lease is the first write.
        dataLease = platformServices.DataLock.Acquire(platformServices.Paths);
        ExternalLauncher = platformServices.ExternalLauncher;
        productJavaBootstrap = DescribeJavaBootstrap(platformServices.Paths);
        Launcher = new LauncherService(platformServices, httpClient, verifiedDownloader, networkOptions, javaBootstrap: productJavaBootstrap);
        await Launcher.InitializeAsync().ConfigureAwait(false);
        if (productJavaBootstrap is not null) await InstallJavaBootstrapAsync(productJavaBootstrap).ConfigureAwait(false);
    }
    private static async Task ShutdownHostAsync()
    {
        try { if (Launcher is not null) await Launcher.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { if (platformServices?.Processes is IAsyncDisposable processes) await processes.DisposeAsync().ConfigureAwait(false); }
            finally { httpClient?.Dispose(); dataLease?.Dispose(); dataLease = null; }
        }
    }
    private static void ValidateArguments()
    {
        var switches = new HashSet<string>(["--diagnostics","--smoke-test","--prepare-minecraft","--launch-minecraft","--official-downloads","--inspect-java-trust","--derive-java-trust"], StringComparer.Ordinal);
        var valued = new HashSet<string>(["--data-dir","--result-file","--run-id","--exit-after","--java-path","--version","--loader","--loader-version","--offline-profile","--dry-run-launch","--credential-smoke"], StringComparer.Ordinal);
        for (var i = 0; i < Arguments.Length; i++)
        {
            var option = Arguments[i];
            if (switches.Contains(option) || option.StartsWith("-psn_", StringComparison.Ordinal)) continue;
            if (!valued.Contains(option) || ++i >= Arguments.Length || Arguments[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unknown or incomplete launcher option.");
        }
        foreach (var flag in new[] { "--data-dir", "--result-file", "--java-path" })
            if (ArgumentValue(flag) is { } path && !Path.IsPathFullyQualified(path)) throw new ArgumentException(flag + " requires an absolute path.");
        var testMode = Arguments.Any(a => a is "--smoke-test" or "--prepare-minecraft" or "--launch-minecraft" or "--credential-smoke" or "--inspect-java-trust" or "--derive-java-trust");
        if (testMode && ArgumentValue("--data-dir") is null) throw new ArgumentException("Smoke/game/credential validation requires a fully isolated --data-dir.");
        if (ArgumentValue("--exit-after") is { } value && (!int.TryParse(value, out var seconds) || seconds is <= 0 or > 3600)) throw new ArgumentException("exit-after must be 1–3600 seconds.");
    }
    private static void WriteCommandReport(object result)
    {
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        if (ArgumentValue("--result-file") is { } file)
        {
            if (!Path.IsPathFullyQualified(file)) throw new ArgumentException("Result file must be absolute.");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, json); File.Move(temporary, file, true);
        }
        Console.WriteLine(json);
    }
    private static void TryWriteFailureReport(Exception error, string stage)
    {
        try { WriteCommandReport(new { status = "FAIL", stage, errorType = error.GetType().Name,
            message = Launcher?.Snapshot.Error ?? "命令或清理未完成；未作成功声明。",
            // Method identities diagnose native startup without copying exception text, argv,
            // response bodies, file contents, credentials or source-file paths into a report.
            stackMethods = new System.Diagnostics.StackTrace(error, false).GetFrames()
                .Select(frame => frame.GetMethod()).Where(method => method is not null)
                .Select(method => method!.DeclaringType?.FullName + "." + method.Name).Take(32).ToArray(),
            innerTypes = InnerExceptionTypes(error), reportedAt = DateTimeOffset.UtcNow }); }
        catch (Exception reportError) { Console.Error.WriteLine("Failure report could not be written: " + reportError.GetType().Name); }
    }
    private static string[] InnerExceptionTypes(Exception error)
    {
        var types = new List<string>();
        for (var inner = error.InnerException; inner is not null && types.Count < 8; inner = inner.InnerException)
            types.Add(inner.GetType().FullName ?? inner.GetType().Name);
        return types.ToArray();
    }
}
