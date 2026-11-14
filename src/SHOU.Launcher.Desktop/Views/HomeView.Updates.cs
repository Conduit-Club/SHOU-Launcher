using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class HomeView
{
    private HomePageViewModel? updatesOwner;
    private ClubUpdatesOverlay? updatesOverlay;
    private OverlayLayer? updatesLayer;
    private void InitializeUpdatesOverlay()
    {
        AttachedToVisualTree += (_, _) => SubscribeUpdates();
        DataContextChanged += (_, _) => { RemoveUpdatesOverlay(); SubscribeUpdates(); };
        DetachedFromVisualTree += (_, _) =>
        {
            if (updatesOwner is { } owner) { owner.PropertyChanged -= UpdatesOwnerChanged; owner.CloseUpdatesCommand.Execute(null); }
            updatesOwner = null; RemoveUpdatesOverlay();
        };
    }
    private void SubscribeUpdates()
    {
        if (updatesOwner is { } old) old.PropertyChanged -= UpdatesOwnerChanged;
        updatesOwner = DataContext as HomePageViewModel;
        if (updatesOwner is { } owner) { owner.PropertyChanged += UpdatesOwnerChanged; UpdateUpdatesOverlay(); }
    }
    private void UpdatesOwnerChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(HomePageViewModel.IsUpdatesOpen)) UpdateUpdatesOverlay(); }
    private void UpdateUpdatesOverlay()
    {
        if (updatesOwner?.IsUpdatesOpen != true) { RemoveUpdatesOverlay(); return; }
        if (updatesOverlay is not null) return;
        var layer = OverlayLayer.GetOverlayLayer(this); if (layer is null) return;
        updatesLayer = layer; updatesOverlay = new() { DataContext = updatesOwner };
        SizeUpdatesOverlay(); layer.SizeChanged += UpdatesLayerSizeChanged; layer.Children.Add(updatesOverlay);
        Dispatcher.UIThread.Post(() => updatesOverlay?.FindControl<Button>("CloseClubUpdatesButton")?.Focus());
    }
    private void UpdatesLayerSizeChanged(object? sender, SizeChangedEventArgs e) => SizeUpdatesOverlay();
    private void SizeUpdatesOverlay()
    {
        if (updatesOverlay is null || updatesLayer is null) return;
        var size = updatesLayer.Bounds.Size; if (size.Width <= 0 || size.Height <= 0) size = TopLevel.GetTopLevel(this)!.ClientSize;
        updatesOverlay.Width = size.Width; updatesOverlay.Height = size.Height;
    }
    private void RemoveUpdatesOverlay()
    {
        var overlay = updatesOverlay; updatesOverlay = null;
        if (updatesLayer is { } layer) { layer.SizeChanged -= UpdatesLayerSizeChanged; if (overlay is not null) layer.Children.Remove(overlay); }
        updatesLayer = null;
        if (overlay is not null) this.FindControl<Button>("OpenClubUpdatesButton")?.Focus();
    }
}
