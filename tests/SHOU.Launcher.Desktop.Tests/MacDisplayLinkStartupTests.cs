using SHOU.Launcher.Desktop.Platform.MacOS;
using Xunit;

namespace SHOU.Launcher.Desktop.Tests;

public sealed class MacDisplayLinkStartupTests
{
    [Fact]
    public void ExplicitNativeProbeUsesTheRealMacFrameworkWithoutOpeningAWindow()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("SHOU_TEST_MAC_NATIVE_DISPLAYLINK") != "1")
            Assert.Skip("Opt-in CoreVideo interop check; does not open or control a window.");
        var report = MacDisplayLinkStartup.Prepare(true);
        Assert.True(report!.Ready); Assert.Equal(0, report.NativeStatus);
    }
    [Fact]
    public void WindowsDoesNotProbeLoadCoreVideoOrDelay()
    {
        Assert.Null(MacDisplayLinkStartup.Prepare(false,
            () => throw new InvalidOperationException("Non-Mac probe was called."),
            _ => throw new InvalidOperationException("Non-Mac startup was delayed.")));
    }
    [Fact]
    public void ReadyDisplayDoesNotDelayNormalStartup()
    {
        var report = MacDisplayLinkStartup.Prepare(true, () => new(DisplayLinkStage.Ready, 0),
            _ => throw new InvalidOperationException("Healthy display was delayed."));
        Assert.True(report!.Ready); Assert.Equal(1, report.Attempts); Assert.Equal(0, report.NativeStatus);
    }
    [Fact]
    public void TransientWakeFailureRecoversBeforePlatformInitialization()
    {
        var calls = 0; var waits = 0;
        var report = MacDisplayLinkStartup.Prepare(true,
            () => ++calls < 3 ? new(DisplayLinkStage.Create, -6660) : new(DisplayLinkStage.Ready, 0),
            duration => { Assert.True(duration > TimeSpan.Zero); waits++; });
        Assert.True(report!.Ready); Assert.Equal(3, calls); Assert.Equal(2, waits);
    }
    [Fact]
    public void PersistentFailureIsBoundedAndRetainsTheNativeStageAndStatus()
    {
        var probes = 0;
        var error = Assert.Throws<DisplayLinkStartupException>(() => MacDisplayLinkStartup.Prepare(true,
            () => { probes++; return new(DisplayLinkStage.Register, -6670); }, _ => { }));
        Assert.False(error.Report.Ready); Assert.InRange(probes, 2, 16);
        Assert.Equal(probes, error.Report.Attempts); Assert.Equal(DisplayLinkStage.Register, error.Report.Stage);
        Assert.Equal(-6670, error.Report.NativeStatus);
    }
    [Fact]
    public void MissingNativeDependencyIsNotHiddenByRetries()
    {
        Assert.Throws<EntryPointNotFoundException>(() => MacDisplayLinkStartup.Prepare(true,
            () => throw new EntryPointNotFoundException(), _ => throw new InvalidOperationException("Unexpected retry.")));
    }
    [Theory]
    [InlineData("-6660", -6660)]
    [InlineData("0", 0)]
    [InlineData("-6660 secret-text", null)]
    [InlineData("2147483648", null)]
    public void FailureReportExtractsOnlyTheAllowlistedInteger(string suffix, int? expected)
    {
        Assert.Equal(expected, MacDisplayLinkStartup.NativeFailureStatus(new InvalidOperationException(MacDisplayLinkStartup.NativeFailurePrefix + suffix)));
        Assert.Null(MacDisplayLinkStartup.NativeFailureStatus(new IOException(MacDisplayLinkStartup.NativeFailurePrefix + suffix)));
        Assert.Null(MacDisplayLinkStartup.NativeFailureStatus(new InvalidOperationException("unrelated " + suffix)));
    }
}
