using System.Globalization;
using System.Runtime.InteropServices;

namespace SHOU.Launcher.Desktop.Platform.MacOS;

internal enum DisplayLinkStage { Create, Register, Ready }
internal sealed record DisplayLinkProbe(DisplayLinkStage Stage, int NativeStatus);
internal sealed record DisplayLinkStartupReport(bool Ready, int Attempts, DisplayLinkStage Stage, int NativeStatus);
internal sealed class DisplayLinkStartupException(DisplayLinkStartupReport report)
    : Exception($"macOS 显示时钟暂不可用（{report.Stage}: {report.NativeStatus}）。请在桌面唤醒后重试；原账户和实例保留。")
{
    internal DisplayLinkStartupReport Report { get; } = report;
}

/// <summary>Prepare the same CoreVideo registration used by Avalonia before its one-shot platform setup.</summary>
internal static class MacDisplayLinkStartup
{
    internal const string NativeFailurePrefix = "Avalonia.Native was not able to start the RenderTimer. Native error code is: ";
    internal static DisplayLinkStartupReport? Prepare(bool isMacOS,
        Func<DisplayLinkProbe>? probe = null, Action<TimeSpan>? delay = null)
    {
        if (!isMacOS) return null;
        probe ??= ProbeNative;
        delay ??= Thread.Sleep;
        for (var attempt = 1; ; attempt++)
        {
            var result = probe();
            var report = new DisplayLinkStartupReport(result.NativeStatus == 0, attempt, result.Stage, result.NativeStatus);
            if (report.Ready) return report;
            // Display reconfiguration/wake can briefly reject registration. Never
            // retry a half-initialized Avalonia AppBuilder or wait indefinitely.
            if (attempt == 8) throw new DisplayLinkStartupException(report);
            delay(TimeSpan.FromMilliseconds(250));
        }
    }

    internal static int? NativeFailureStatus(Exception error)
    {
        if (error is not InvalidOperationException || !error.Message.StartsWith(NativeFailurePrefix, StringComparison.Ordinal)) return null;
        return int.TryParse(error.Message.AsSpan(NativeFailurePrefix.Length), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var status) ? status : null;
    }

    private static DisplayLinkProbe ProbeNative()
    {
        nint link = 0;
        try
        {
            var status = Native.CVDisplayLinkCreateWithActiveCGDisplays(out link);
            if (status != 0) return new(DisplayLinkStage.Create, status);
            status = Native.CVDisplayLinkSetOutputCallback(link, Native.Callback, 0);
            return new(status == 0 ? DisplayLinkStage.Ready : DisplayLinkStage.Register, status);
        }
        finally { if (link != 0) Native.CVDisplayLinkRelease(link); }
    }

    private static class Native
    {
        private const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int OutputCallback(nint link, nint now, nint output, ulong flags, ref ulong outputFlags, nint context);
        // The probe does not start the display link. Keep the callback rooted until release.
        internal static readonly OutputCallback Callback = NoTick;
        private static int NoTick(nint link, nint now, nint output, ulong flags, ref ulong outputFlags, nint context) => 0;
        [DllImport(CoreVideo)] internal static extern int CVDisplayLinkCreateWithActiveCGDisplays(out nint link);
        [DllImport(CoreVideo)] internal static extern int CVDisplayLinkSetOutputCallback(nint link, OutputCallback callback, nint context);
        [DllImport(CoreVideo)] internal static extern void CVDisplayLinkRelease(nint link);
    }
}
