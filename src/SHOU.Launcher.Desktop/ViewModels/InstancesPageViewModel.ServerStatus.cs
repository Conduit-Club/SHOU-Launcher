using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class InstanceServerDraftViewModel(Guid draftId, int? existingIndex, string name, string address) : ObservableObject, IDisposable
{
    private ClubServer? result; private Bitmap? icon; private bool querying;
    public Guid DraftId => draftId;
    public int? ExistingIndex => existingIndex;
    public string Name => name;
    public string Address => address;
    public Bitmap? StatusIcon => icon;
    public bool HasStatusIcon => icon is not null;
    public string Motd => result?.Motd ?? "";
    public bool HasMotd => Motd.Length > 0;
    public string StatusSummary => querying ? Localization.UiLocalization.Instance.Text("正在查询…") : result is null ? Localization.UiLocalization.Instance.Text("尚未查询") :
        string.Join(" · ", new[] { Localization.UiLocalization.Instance.Text(result.Status), result.OnlinePlayers is { } online ? online + (result.MaximumPlayers is { } maximum ? " / " + maximum : "") : "", result.GameVersion ?? "", result.PingMilliseconds is { } ping ? $"Ping {ping} ms" : result.ResponseMilliseconds is { } elapsed ? Localization.UiLocalization.Instance.Format("响应 {0} ms", elapsed) : "" }.Where(x => x.Length > 0));
    internal void SetQuerying(bool value) { querying = value; OnPropertyChanged(nameof(StatusSummary)); }
    internal void SetStatus(ClubServer value)
    {
        result = value; querying = false; icon?.Dispose(); icon = null;
        if (value.IconPng is { Length: > 0 } bytes) try { using var stream = new MemoryStream(bytes, false); icon = Bitmap.DecodeToWidth(stream, 40); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { }
        foreach (var property in new[] { nameof(StatusSummary), nameof(Motd), nameof(HasMotd), nameof(StatusIcon), nameof(HasStatusIcon) }) OnPropertyChanged(property);
    }
    public void Dispose() { icon?.Dispose(); icon = null; }
}

public sealed partial class InstancesPageViewModel
{
    private CancellationTokenSource? serverStatusCancellation;
    private int serverStatusGeneration;
    private bool serverStatusBusy;
    public bool IsServerStatusBusy => serverStatusBusy;
    public AsyncRelayCommand RefreshInstanceServerStatusCommand { get; private set; } = null!;
    public AsyncRelayCommand<InstanceServerDraftViewModel> RefreshSingleServerStatusCommand { get; private set; } = null!;
    public bool IsCurrentServerRow(InstanceServerDraftViewModel? row) => !_workspaceDisposed && IsInstanceServersPage && ReferenceEquals(Owner.SelectedPage, this) && row is not null && InstanceServerRows.Contains(row);
    private void InitializeServerStatus()
    {
        RefreshInstanceServerStatusCommand = AsyncCommand(() => RefreshInstanceServerStatusAsync(), () => IsInstanceServersPage && !serverStatusBusy && Service is IToolboxService && Owner.CanReadRemote);
        RefreshSingleServerStatusCommand = new(row => IsCurrentServerRow(row) ? RefreshInstanceServerStatusAsync(row) : Task.CompletedTask, row => IsCurrentServerRow(row) && !serverStatusBusy && Service is IToolboxService && Owner.CanReadRemote);
    }
    private void CancelServerStatus()
    {
        ++serverStatusGeneration; serverStatusCancellation?.Cancel(); serverStatusCancellation?.Dispose(); serverStatusCancellation = null; serverStatusBusy = false;
        foreach (var draft in _worldDrafts.Values) foreach (var row in draft.Servers) row.SetQuerying(false);
        OnPropertyChanged(nameof(IsServerStatusBusy)); RefreshInstanceServerStatusCommand?.NotifyCanExecuteChanged(); RefreshSingleServerStatusCommand?.NotifyCanExecuteChanged();
    }
    private void ServerStatusNavigationChanged()
    { if (!IsInstanceServersPage || !ReferenceEquals(Owner.SelectedPage, this)) CancelServerStatus(); }
    private async Task RefreshInstanceServerStatusAsync(InstanceServerDraftViewModel? single = null)
    {
        if (_workspaceDisposed || !IsInstanceServersPage || !ReferenceEquals(Owner.SelectedPage, this) || SelectedInstance is not { } instance || Service is not IToolboxService service) return;
        CancelServerStatus(); var generation = serverStatusGeneration; var source = new CancellationTokenSource(); serverStatusCancellation = source; var token = source.Token; var draft = _worldDraft;
        var rows = single is null ? draft.Servers.ToArray() : draft.Servers.Contains(single) ? new[] { single } : [];
        bool Current() => !_workspaceDisposed && generation == serverStatusGeneration && ReferenceEquals(draft, _worldDraft) && IsInstanceServersPage && ReferenceEquals(Owner.SelectedPage, this) && SelectedInstance?.Id == instance.Id && SelectedInstance.GameDirectory == instance.GameDirectory;
        serverStatusBusy = true; OnPropertyChanged(nameof(IsServerStatusBusy)); RefreshInstanceServerStatusCommand?.NotifyCanExecuteChanged(); RefreshSingleServerStatusCommand?.NotifyCanExecuteChanged();
        using var concurrency = new SemaphoreSlim(4);
        try
        {
            await Task.WhenAll(rows.Select(async row =>
            {
                await concurrency.WaitAsync(token);
                try
                {
                    if (!Current() || !draft.Servers.Contains(row)) return; row.SetQuerying(true);
                    ClubServer status;
                    try { status = await service.QueryToolServerAsync(row.Address, token); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or HttpRequestException or NotSupportedException) { status = new("instance-query", row.Name, row.Address, "状态暂不可用"); }
                    if (Current() && draft.Servers.Contains(row)) row.SetStatus(status);
                }
                finally { concurrency.Release(); }
            }));
        }
        catch (OperationCanceledException) { }
        finally { if (Current()) CancelServerStatus(); if (ReferenceEquals(serverStatusCancellation, source)) serverStatusCancellation = null; source.Dispose(); }
    }
}
