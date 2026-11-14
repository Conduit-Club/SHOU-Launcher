using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class ClubUpdatesOverlay : UserControl
{
    public ClubUpdatesOverlay() => AvaloniaXamlLoader.Load(this);
    private void Updates_KeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Escape && DataContext is HomePageViewModel vm) { vm.CloseUpdatesCommand.Execute(null); e.Handled = true; } }
}
