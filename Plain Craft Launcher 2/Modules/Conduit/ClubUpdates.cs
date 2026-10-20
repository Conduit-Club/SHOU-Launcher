using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Windows;
using PCL.Core.App;
using PCL.Core.Conduit;
using PCL.Core.Minecraft.Profile;

namespace PCL;

public static class ClubUpdates
{
    private const int MaxNoticeResponseBytes = 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();
    private static readonly HttpClient NoticeClient = CreateNoticeClient();
    private static int _startupNoticeCheckStarted;
    public static ClubRelease Latest { get; private set; }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-CE-SHOU/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static HttpClient CreateNoticeClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-CE-SHOU/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        return client;
    }

    public static async Task<ClubRelease> CheckAsync()
    {
        var json = await Client.GetStringAsync("https://api.github.com/repos/Conduit-Club/PCL-CE-SHOU/releases?per_page=30");
        return Latest = ClubRelease.ParseLatest(json);
    }

    /// <summary>
    /// 启动后后台检查一次社团更新日志。网络、解析和展示条件不满足时均静默结束，留待下次启动重试。
    /// </summary>
    public static void CheckStartupNotice()
    {
        if (Interlocked.Exchange(ref _startupNoticeCheckStarted, 1) != 0 ||
            !Config.Preference.ClubUpdateNoticeEnabled)
            return;

        try
        {
            var html = DownloadNoticePageAsync().GetAwaiter().GetResult();
            if (!Config.Preference.ClubUpdateNoticeEnabled)
                return;

            var latest = ClubUpdateNoticeParser.ParseLatest(html, DateOnly.FromDateTime(DateTime.Now));
            if (latest is null)
            {
                ModBase.Log("[Conduit] 更新日志页没有有效日期记录", ModBase.LogLevel.Debug);
                return;
            }

            var lastDate = ParseAcknowledgedDate(States.System.ClubUpdateNoticeLastId);
            if (!ClubUpdateNoticeParser.ShouldNotify(latest, lastDate))
                return;

            // Fetch runs off the UI thread. The final check and dialog enqueue below run on the UI
            // thread so a login, launch or another modal prompt can never be interrupted by this notice.
            ModBase.RunInUi(() => PresentNoticeIfStillAvailable(latest));
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Conduit] 更新日志检查失败（下次启动重试）", ModBase.LogLevel.Debug);
        }
    }

    private static async Task<string> DownloadNoticePageAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var response = await NoticeClient.GetAsync(ClubUpdateNoticeParser.IndexUrl,
            HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxNoticeResponseBytes)
            throw new InvalidDataException("社团更新日志页面超过 1 MiB，已跳过解析");

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxNoticeResponseBytes)
                throw new InvalidDataException("社团更新日志页面超过 1 MiB，已跳过解析");
            await content.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(content.ToArray());
    }

    private static DateOnly? ParseAcknowledgedDate(string? value)
        => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;

    private static bool IsSensitiveOperationActive()
    {
        var main = ModMain.frmMain;
        var visibleMessage = main is not null && main.PanMsg.Children.Count > 0 &&
                             main.PanMsgBackground.Visibility != Visibility.Collapsed;
        return ModBase.isProgramEnded || ModLaunch.isLaunching || ProfileService.IsCreatingProfile ||
               ModMain.frmLoginMs?.IsAuthenticating == true || ModMain.frmLoginAuth?.IsAuthenticating == true ||
               ModMain.WaitingMyMsgBox.Count > 0 || visibleMessage;
    }

    private static void PresentNoticeIfStillAvailable(ClubUpdateNotice notice)
    {
        try
        {
            if (!Config.Preference.ClubUpdateNoticeEnabled || IsSensitiveOperationActive())
            {
                ModBase.Log("[Conduit] 更新日志已发现，但展示前检测到登录、启动或其他弹窗，留待下次启动", ModBase.LogLevel.Debug);
                return;
            }

            // The checks above establish that the dialog can be presented. Persist before entering
            // its modal wait, so a user who leaves the notice open or exits after seeing it will not
            // receive the same date on a later startup. Restore the old value if enqueueing fails.
            var previousId = States.System.ClubUpdateNoticeLastId;
            States.System.ClubUpdateNoticeLastId = notice.Id;
            try
            {
                ModMain.MyMsgBox(
                    $"日期：{notice.DateText}\r\n\r\n{notice.Title}",
                    "社团更新日志",
                    "打开原文",
                    "关闭",
                    button1Action: () => ModBase.OpenWebsite(notice.Url.ToString()));
            }
            catch
            {
                States.System.ClubUpdateNoticeLastId = previousId;
                throw;
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Conduit] 展示更新日志提示失败（下次启动重试）", ModBase.LogLevel.Debug);
        }
    }
}
