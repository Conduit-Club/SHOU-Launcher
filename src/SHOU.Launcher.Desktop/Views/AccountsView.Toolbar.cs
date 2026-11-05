using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SHOU.Launcher.Desktop.ViewModels;

namespace SHOU.Launcher.Desktop.Views;

public partial class AccountsView
{
    private StackPanel? toolbarAccountPanel;
    private Border? accountToolbarHost;
    private TextBlock? accountAppearanceFeedback;
    private FlyoutBase? accountToolbarFlyout;
    private TopLevel? accountToolbarTopLevel;
    private AccountsPageViewModel? accountToolbarOwner;
    private string? accountToolbarAccountId;
    private bool accountToolbarAttached, accountToolbarTouchRevealed, accountToolbarUpdateQueued;
    private long accountToolbarEpoch;

    private void AccountToolbarAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (accountToolbarAttached) return;
        accountToolbarAttached = true; ++accountToolbarEpoch;
        toolbarAccountPanel = this.FindControl<StackPanel>("SelectedAccountPanel")!;
        accountToolbarHost = this.FindControl<Border>("AccountToolbarHost")!;
        accountAppearanceFeedback = this.FindControl<TextBlock>("AccountAppearanceFeedback")!;
        accountToolbarFlyout = this.FindControl<Button>("AccountSkinMenuButton")!.Flyout;
        accountToolbarTopLevel = TopLevel.GetTopLevel(this);
        toolbarAccountPanel.PointerEntered += AccountToolbarPointerChanged;
        toolbarAccountPanel.PointerExited += AccountToolbarPointerChanged;
        toolbarAccountPanel.AddHandler(InputElement.GotFocusEvent, AccountToolbarFocusChanged, RoutingStrategies.Bubble, true);
        toolbarAccountPanel.AddHandler(InputElement.LostFocusEvent, AccountToolbarFocusChanged, RoutingStrategies.Bubble, true);
        accountToolbarTopLevel?.AddHandler(InputElement.PointerPressedEvent, AccountToolbarPointerPressed, RoutingStrategies.Tunnel, true);
        if (accountToolbarFlyout is { } flyout) { flyout.Opened += AccountToolbarFlyoutChanged; flyout.Closed += AccountToolbarFlyoutChanged; }
        SubscribeAccountToolbarOwner(); RefreshAccountToolbar();
    }

    private void AccountToolbarDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        accountToolbarAttached = false; ++accountToolbarEpoch;
        if (toolbarAccountPanel is { } panel)
        {
            panel.PointerEntered -= AccountToolbarPointerChanged; panel.PointerExited -= AccountToolbarPointerChanged;
            panel.RemoveHandler(InputElement.GotFocusEvent, AccountToolbarFocusChanged);
            panel.RemoveHandler(InputElement.LostFocusEvent, AccountToolbarFocusChanged);
        }
        accountToolbarTopLevel?.RemoveHandler(InputElement.PointerPressedEvent, AccountToolbarPointerPressed);
        if (accountToolbarFlyout is { } flyout) { flyout.Opened -= AccountToolbarFlyoutChanged; flyout.Closed -= AccountToolbarFlyoutChanged; flyout.Hide(); }
        if (accountToolbarOwner is { } owner) owner.PropertyChanged -= AccountToolbarOwnerChanged;
        if (accountToolbarHost is { } host) { host.Opacity = 0; host.IsHitTestVisible = false; }
        toolbarAccountPanel = null; accountToolbarHost = null; accountAppearanceFeedback = null;
        accountToolbarFlyout = null; accountToolbarTopLevel = null; accountToolbarOwner = null; accountToolbarAccountId = null;
        accountToolbarTouchRevealed = false; accountToolbarUpdateQueued = false;
    }

    private void AccountToolbarDataContextChanged(object? sender, EventArgs e)
    {
        if (!accountToolbarAttached) return;
        ++accountToolbarEpoch; accountToolbarUpdateQueued = false; accountToolbarTouchRevealed = false;
        accountToolbarFlyout?.Hide(); SubscribeAccountToolbarOwner(); RefreshAccountToolbar();
    }

    private void SubscribeAccountToolbarOwner()
    {
        if (accountToolbarOwner is { } previous) previous.PropertyChanged -= AccountToolbarOwnerChanged;
        accountToolbarOwner = DataContext as AccountsPageViewModel;
        if (accountToolbarOwner is { } owner) owner.PropertyChanged += AccountToolbarOwnerChanged;
    }

    private void AccountToolbarOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountsPageViewModel.SelectedChannelAccount) or nameof(AccountsPageViewModel.ShowSelectedAccount))
            accountToolbarTouchRevealed = false;
        QueueAccountToolbarUpdate();
    }

    private void AccountToolbarPointerChanged(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch) accountToolbarTouchRevealed = false;
        RefreshAccountToolbar();
    }

    private void AccountToolbarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (toolbarAccountPanel is not { } panel) return;
        var inside = panel.IsEffectivelyVisible && new Rect(panel.Bounds.Size).Contains(e.GetPosition(panel));
        accountToolbarTouchRevealed = inside && e.Pointer.Type == PointerType.Touch;
        RefreshAccountToolbar();
    }

    private void AccountToolbarFocusChanged(object? sender, RoutedEventArgs e)
    { RefreshAccountToolbar(); QueueAccountToolbarUpdate(); }
    private void AccountToolbarFlyoutChanged(object? sender, EventArgs e) => RefreshAccountToolbar();

    private void QueueAccountToolbarUpdate()
    {
        if (!accountToolbarAttached || accountToolbarUpdateQueued) return;
        accountToolbarUpdateQueued = true; var epoch = accountToolbarEpoch;
        Dispatcher.UIThread.Post(() =>
        {
            if (!accountToolbarAttached || epoch != accountToolbarEpoch) return;
            accountToolbarUpdateQueued = false; RefreshAccountToolbar();
        });
    }

    private void RefreshAccountToolbar()
    {
        if (!accountToolbarAttached || toolbarAccountPanel is not { } panel || accountToolbarHost is not { } host) return;
        var accountId = accountToolbarOwner?.SelectedChannelAccount?.Id;
        if (accountId != accountToolbarAccountId) { accountToolbarAccountId = accountId; accountToolbarTouchRevealed = false; accountToolbarFlyout?.Hide(); }
        var selected = accountToolbarOwner?.ShowSelectedAccount == true;
        if (!selected) { accountToolbarTouchRevealed = false; accountToolbarFlyout?.Hide(); }
        var reveal = selected && (panel.IsPointerOver || panel.IsKeyboardFocusWithin || accountToolbarFlyout?.IsOpen == true || accountToolbarTouchRevealed);
        // Keep layout and tab navigation intact; the parent card reveals the hidden hit area.
        host.Opacity = reveal ? 1 : 0; host.IsHitTestVisible = reveal;
        if (accountAppearanceFeedback is { } feedback)
        {
            // These two are persistent empty-state hints, already present in the avatar tooltip.
            var status = accountToolbarOwner?.AccountAppearanceStatus ?? "";
            feedback.IsVisible = selected && (accountToolbarOwner?.IsAccountAppearanceBusy == true || status.Length > 0 && status is not "尚未选择本地皮肤。" and not "点击“刷新头像”读取在线外观。");
        }
    }
}
