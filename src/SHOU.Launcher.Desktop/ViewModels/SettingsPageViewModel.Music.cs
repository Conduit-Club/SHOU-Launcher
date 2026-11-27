using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Downloads;
using SHOU.Launcher.Desktop.Platform;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed record MusicTrack(string Name, string Path);
public sealed partial class SettingsPageViewModel
{
    private readonly BackgroundAudio backgroundAudio = new();
    private MusicTrack? selectedMusic, playingTrack;
    private string musicFeedback = "";
    public string MusicFeedback { get => musicFeedback; private set { if (SetProperty(ref musicFeedback, value)) OnPropertyChanged(nameof(HasMusicFeedback)); } }
    public bool HasMusicFeedback => !string.IsNullOrWhiteSpace(MusicFeedback);
    private bool musicWanted, musicAutoWasEnabled, musicStoppedManually, previousMusicGameRunning;
    private decimal appliedVolume = -1;
    private readonly Avalonia.Threading.DispatcherTimer musicTimer = new() { Interval = TimeSpan.FromSeconds(.5) };
    public decimal MusicVolume { get => appearanceOptions.MusicVolume; set => Appearance(value, p => p.MusicVolume, (p, v) => p with { MusicVolume = v }); }
    public bool MusicAutoPlay { get => appearanceOptions.MusicAutoPlay; set => Appearance(value, p => p.MusicAutoPlay, (p, v) => p with { MusicAutoPlay = v }); }
    public bool MusicShuffle { get => appearanceOptions.MusicShuffle; set => Appearance(value, p => p.MusicShuffle, (p, v) => p with { MusicShuffle = v }); }
    public string MusicGamePolicy { get => appearanceOptions.MusicGamePolicy; set => Appearance(value, p => p.MusicGamePolicy, (p, v) => p with { MusicGamePolicy = v }); }
    public IReadOnlyList<string> MusicGamePolicies { get; } = ["keep", "stop", "only"];
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
                SelectedMusic = MusicTracks.FirstOrDefault(); MusicFeedback = MusicTracks.Count == 0 ? "音乐目录中没有 WAV、MP3 或 M4A 文件。" : "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MusicFeedback = UiPrivacy.Redact(ex.Message); }
        });
        PlayMusicCommand = Command(() => { musicWanted = true; musicStoppedManually = false; playingTrack = null; backgroundAudio.Stop(); UpdateMusicPlayback(); }, () => SelectedMusic is not null);
        StopMusicCommand = Command(() => { musicWanted = false; musicStoppedManually = true; backgroundAudio.Stop(); playingTrack = null; });
        musicTimer.Tick += (_, _) => UpdateMusicPlayback(); musicTimer.Start();
        RefreshMusicCommand.Execute(null); ApplyMusicOptions();
    }
    private void ApplyMusicOptions()
    {
        if (_savedPresentation.MusicAutoPlay != musicAutoWasEnabled)
        { musicWanted = _savedPresentation.MusicAutoPlay; musicAutoWasEnabled = _savedPresentation.MusicAutoPlay; if (!musicWanted) backgroundAudio.Stop(); }
        UpdateMusicPlayback();
    }
    private void UpdateMusicPlayback()
    {
        if (settingsLifetime.IsCancellationRequested || !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;
        var p = _savedPresentation; var running = Owner.Snapshot.GameSessions.Count > 0 || Owner.Snapshot.GameProcessId is not null;
        if (running && !previousMusicGameRunning && p.MusicGamePolicy == "only") musicStoppedManually = false;
        previousMusicGameRunning = running;
        if (musicStoppedManually) return;
        if (p.MusicGamePolicy == "stop" && running || p.MusicGamePolicy == "only" && !running)
        { backgroundAudio.Stop(); return; }
        if ((!musicWanted && !(p.MusicGamePolicy == "only" && running)) || MusicTracks.Count == 0) return;
        try
        {
            var volumeChanged = appliedVolume != p.MusicVolume;
            if (backgroundAudio.IsPlaying && (!volumeChanged || OperatingSystem.IsWindows()))
            { if (volumeChanged) backgroundAudio.SetVolume(p.MusicVolume); appliedVolume = p.MusicVolume; return; }
            var track = volumeChanged && playingTrack is not null ? playingTrack : playingTrack is null ? SelectedMusic ?? MusicTracks[0] :
                p.MusicShuffle ? MusicTracks[Random.Shared.Next(MusicTracks.Count)] : MusicTracks[(Math.Max(0, MusicTracks.IndexOf(playingTrack)) + 1) % MusicTracks.Count];
            backgroundAudio.Play(track.Path, p.MusicVolume); playingTrack = track; appliedVolume = p.MusicVolume;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        { musicWanted = false; musicStoppedManually = true; backgroundAudio.Stop(); MusicFeedback = UiPrivacy.Redact(ex.Message); }
    }

    internal void DisposeLauncherSettings() { autoSaveTimer.Stop(); musicTimer.Stop(); DisposeAbout(); settingsLifetime.Cancel(); settingsWork?.Cancel(); backgroundAudio.Dispose(); }
}
