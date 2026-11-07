using System.ComponentModel;
using Avalonia.Controls;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class MainWindow
{
    private MainWindowViewModel? visibilityOwner;
    private readonly HashSet<int> visibilityHandled = [];
    private bool hiddenForGame;
    private void InitializeGameVisibility()
    {
        DataContextChanged += (_, _) =>
        {
            if (visibilityOwner is { } previous) previous.PropertyChanged -= GameVisibilityChanged;
            visibilityOwner = DataContext as MainWindowViewModel;
            if (visibilityOwner is { } next) next.PropertyChanged += GameVisibilityChanged;
        };
        Closed += (_, _) => { if (visibilityOwner is { } owner) owner.PropertyChanged -= GameVisibilityChanged; };
    }
    private void GameVisibilityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.Snapshot) || sender is not MainWindowViewModel vm) return;
        var state = vm.Snapshot;
        if (state.GameSessions.Count == 0 && hiddenForGame) { hiddenForGame = false; Show(); WindowState = WindowState.Normal; }
        if (state.State is "Launching" or "Installing") return;
        foreach (var session in state.GameSessions)
        {
            if (!visibilityHandled.Add(session.ProcessId)) continue;
            var instance = state.Instances.FirstOrDefault(i => i.Id == session.InstanceId);
            var behavior = SHOU.Launcher.Core.Minecraft.GameSettingsPolicy.Options(instance, state.Settings).Visibility;
            switch (behavior)
            {
                case LauncherVisibility.Minimize: WindowState = WindowState.Minimized; break;
                case LauncherVisibility.Hide: hiddenForGame = true; Hide(); break;
                case LauncherVisibility.Close: Close(); return;
            }
        }
    }
}
