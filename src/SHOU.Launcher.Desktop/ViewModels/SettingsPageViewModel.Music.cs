using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record MusicTrack(string Name, string Path);
public sealed partial class SettingsPageViewModel
{
    private readonly BackgroundAudio backgroundAudio = new();
    private MusicTrack? selectedMusic;
    public ObservableCollection<MusicTrack> MusicTracks { get; } = [];
    public MusicTrack? SelectedMusic { get => selectedMusic; set { if (SetProperty(ref selectedMusic, value)) PlayMusicCommand.NotifyCanExecuteChanged(); } }
    public RelayCommand RefreshMusicCommand { get; private set; } = null!;
    public RelayCommand PlayMusicCommand { get; private set; } = null!;
    public RelayCommand StopMusicCommand { get; private set; } = null!;
    private void InitializeMusic()
    {
        RefreshMusicCommand = Command(() =>
        {
            MusicTracks.Clear(); var root = Path.Combine(DataDirectory, "appearance", "music");
            try
            {
                SHOU.Launcher.Core.IO.SafePath.RejectLinks(root);
                if (Directory.Exists(root)) foreach (var file in new DirectoryInfo(root).EnumerateFiles().Where(f => f.LinkTarget is null && f.Extension.ToLowerInvariant() is ".mp3" or ".wav" or ".m4a").OrderBy(f => f.Name).Take(200)) MusicTracks.Add(new(file.Name, file.FullName));
                SelectedMusic = MusicTracks.FirstOrDefault(); LauncherFeedback = MusicTracks.Count == 0 ? "音乐目录中没有 WAV、MP3 或 M4A 文件。" : "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LauncherFeedback = UiPrivacy.Redact(ex.Message); }
        });
        PlayMusicCommand = Command(() => { try { backgroundAudio.Play(SelectedMusic!.Path); LauncherFeedback = ""; } catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException) { LauncherFeedback = UiPrivacy.Redact(ex.Message); } }, () => SelectedMusic is not null);
        StopMusicCommand = Command(backgroundAudio.Stop);
    }
    internal void DisposeLauncherSettings() { settingsLifetime.Cancel(); settingsWork?.Cancel(); backgroundAudio.Dispose(); }
}
