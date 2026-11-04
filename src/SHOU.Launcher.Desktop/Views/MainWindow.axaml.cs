using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Opened += async (_, _) => { if (DataContext is MainWindowViewModel vm) await vm.InitializeAsync(); };
        Closed += (_, _) => { if (DataContext is MainWindowViewModel vm) vm.Dispose(); };
        KeyDown += (_, e) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;
            if ((e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0 && e.Key is >= Key.D1 and <= Key.D6)
            {
                vm.SelectedPage = vm.Pages[(int)e.Key - (int)Key.D1]; e.Handled = true;
            }
            else if (e.Key == Key.Escape && vm.CancelOperationCommand.CanExecute(null))
            {
                vm.CancelOperationCommand.Execute(null); e.Handled = true;
            }
        };
    }
}
