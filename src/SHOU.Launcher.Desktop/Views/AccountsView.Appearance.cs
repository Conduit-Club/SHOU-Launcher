using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SHOU.Launcher.Core.Abstractions;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class AccountsView
{
    private AccountAppearanceOverlay? appearanceOverlay;
    private AccountsPageViewModel? appearanceOwner;
    private OverlayLayer? appearanceLayer;
    private void PreviewAccountSkin_Click(object? sender, RoutedEventArgs e) => ShowAppearance(AccountAppearanceDialogPage.Preview);
    private void ViewAccountCapes_Click(object? sender, RoutedEventArgs e) => ShowAppearance(AccountAppearanceDialogPage.Capes);
    private void ChangeAccountSkin_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountsPageViewModel vm) return;
        if (vm.SelectedChannelAccount?.Kind == AccountKind.Offline) ShowAppearance(AccountAppearanceDialogPage.LocalSkin);
        else if (vm.OpenAppearanceWebsiteCommand.CanExecute(null)) vm.OpenAppearanceWebsiteCommand.Execute(null);
    }
    private async void SaveAccountSkin_Click(object? sender, RoutedEventArgs e)
    { if (DataContext is AccountsPageViewModel vm) await AccountAppearanceOverlay.SaveSkinPngAsync(this, vm); }
    private void ShowAppearance(AccountAppearanceDialogPage page)
    {
        if (DataContext is not AccountsPageViewModel vm) return;
        var layer = OverlayLayer.GetOverlayLayer(this);
        if (layer is null) { vm.Root.ShowError("当前窗口没有可用的预览浮层。"); return; }
        RemoveAppearanceOverlay();
        if (!vm.OpenAccountAppearance(page)) return;
        appearanceOwner = vm; appearanceOverlay = new AccountAppearanceOverlay { DataContext = vm };
        vm.PropertyChanged += AppearanceOwnerChanged; DetachedFromVisualTree += AppearanceHostDetached;
        appearanceLayer = layer; layer.SizeChanged += AppearanceLayerSizeChanged;
        var size = layer.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) size = TopLevel.GetTopLevel(this)!.ClientSize;
        appearanceOverlay.Width = size.Width; appearanceOverlay.Height = size.Height;
        layer.Children.Add(appearanceOverlay); appearanceOverlay.FocusCloseButton();
    }
    private void AppearanceLayerSizeChanged(object? sender, SizeChangedEventArgs args)
    { if (appearanceOverlay is { } overlay && args.NewSize.Width > 0 && args.NewSize.Height > 0) { overlay.Width = args.NewSize.Width; overlay.Height = args.NewSize.Height; } }
    private void AppearanceOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountsPageViewModel.IsAccountAppearanceDialogOpen) && appearanceOwner is { IsAccountAppearanceDialogOpen: false })
        {
            var closing = appearanceOverlay;
            Dispatcher.UIThread.Post(() => { if (ReferenceEquals(appearanceOverlay, closing)) RemoveAppearanceOverlay(); });
        }
    }
    private void AppearanceHostDetached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e) => RemoveAppearanceOverlay();
    private void RemoveAppearanceOverlay()
    {
        if (appearanceOwner is { } vm) { vm.PropertyChanged -= AppearanceOwnerChanged; vm.CloseAccountAppearance(); }
        if (appearanceLayer is { } layer) layer.SizeChanged -= AppearanceLayerSizeChanged;
        if (appearanceOverlay is { Parent: Panel parent } overlay) { overlay.DataContext = null; parent.Children.Remove(overlay); }
        appearanceOverlay = null; appearanceOwner = null; appearanceLayer = null; DetachedFromVisualTree -= AppearanceHostDetached;
        this.FindControl<Button>("AccountSkinMenuButton")?.Focus();
    }
}
