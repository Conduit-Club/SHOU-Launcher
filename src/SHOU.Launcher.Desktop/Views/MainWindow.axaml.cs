using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
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
        ConfigurePlatformChrome();
        InitializePackDrop();
        Opened += async (_, _) => { if (DataContext is MainWindowViewModel vm) await vm.InitializeAsync(); };
        Closed += (_, _) => { if (DataContext is MainWindowViewModel vm) vm.Dispose(); };
        KeyDown += (_, e) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;
            if (OperatingSystem.IsMacOS() && e.KeyModifiers == KeyModifiers.Meta && e.Key == Key.W)
            {
                Close();
                e.Handled = true;
            }
            else if (OperatingSystem.IsMacOS() && e.KeyModifiers == KeyModifiers.Meta && e.Key == Key.OemComma)
            {
                vm.ShowSettingsCommand.Execute(null);
                e.Handled = true;
            }
            else if ((e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0 && e.Key is >= Key.D1 and <= Key.D4)
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

    private void ConfigurePlatformChrome()
    {
        if (!OperatingSystem.IsMacOS()) return;
        WindowDecorations = WindowDecorations.Full;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 48;
        this.FindControl<Grid>("TitleBarLayout")!.ColumnDefinitions = new ColumnDefinitions("*,Auto,*");
        var brand = this.FindControl<StackPanel>("WindowBrand")!;
        Grid.SetColumn(brand, 2);
        brand.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        brand.Margin = new Thickness(0, 0, 16, 0);
        var navigation = this.FindControl<StackPanel>("PrimaryNavigation")!;
        navigation.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        navigation.Margin = new Thickness(0);
        var context = this.FindControl<Grid>("ContextHeader")!;
        Grid.SetColumnSpan(context, 3);
        context.Margin = new Thickness(80, 0, 16, 0);
        this.FindControl<StackPanel>("CustomWindowControls")!.IsVisible = false;
        WindowDecorationProperties.SetElementRole(this.FindControl<Border>("LauncherTitleBar")!, WindowDecorationsElementRole.TitleBar);
        foreach (var button in navigation.Children.OfType<Button>())
            WindowDecorationProperties.SetElementRole(button, WindowDecorationsElementRole.User);
        WindowDecorationProperties.SetElementRole(this.FindControl<Button>("BackNavigationButton")!, WindowDecorationsElementRole.User);
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (OperatingSystem.IsMacOS()) return;
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
