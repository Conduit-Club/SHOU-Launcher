using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
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
            if ((e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0 && e.Key is >= Key.D1 and <= Key.D4)
            {
                var command = e.Key switch
                {
                    Key.D1 => vm.ShowHomeCommand,
                    Key.D2 => vm.ShowDownloadsCommand,
                    Key.D3 => vm.ShowSettingsCommand,
                    _ => vm.ShowToolsCommand
                };
                command.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && vm.CancelOperationCommand.CanExecute(null))
            {
                vm.CancelOperationCommand.Execute(null);
                e.Handled = true;
            }
            else if ((e.Key == Key.Escape || (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt)) && vm.BackCommand.CanExecute(null))
            {
                vm.BackCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        for (var current = e.Source as Visual; current is not null; current = current.GetVisualParent())
            if (current is Button) return;
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            BeginMoveDrag(e);
    }

    private void MinimizeWindow_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseWindow_Click(object? sender, RoutedEventArgs e) => Close();
}
