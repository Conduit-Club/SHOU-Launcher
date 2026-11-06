using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCL.Core.Conduit;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed class ClubUpdateItemViewModel : ObservableObject
{
    public ClubUpdateNotice Notice { get; }
    public string Header => Notice.DateText + " · " + Notice.Title;
    private readonly HomePageViewModel owner;
    private string body = "展开后读取正文。";
    private bool expanded, loading;
    public ClubUpdateItemViewModel(HomePageViewModel owner, ClubUpdateNotice notice)
    { this.owner = owner; Notice = notice; RetryCommand = new(() => owner.ReadArticleAsync(this, true)); }
    public string Body { get => body; internal set => SetProperty(ref body, value); }
    public bool IsLoading { get => loading; internal set => SetProperty(ref loading, value); }
    internal bool Loaded { get; set; }
    public bool IsExpanded { get => expanded; set { if (SetProperty(ref expanded, value) && value && !Loaded && !loading) _ = owner.ReadArticleAsync(this); } }
    internal void ExpandWithoutLoading() => SetProperty(ref expanded, true, nameof(IsExpanded));
    public AsyncRelayCommand RetryCommand { get; }
}

public sealed partial class HomePageViewModel : IDisposable
{
    private readonly CancellationTokenSource communityLifetime = new();
    private CancellationTokenSource? updatesRead;
    private bool communityStarted, serverRefreshing, updatesOpen, updatesLoading;
    private long updatesGeneration;
    private string updatesStatus = "", updatesCache = "", serverFeedback = "尚未查询";
    private Task? statusRequest;
    public ObservableCollection<ClubUpdateItemViewModel> UpdateItems { get; } = [];
    public bool IsServerRefreshing { get => serverRefreshing; private set { SetProperty(ref serverRefreshing, value); RefreshServersCommand.NotifyCanExecuteChanged(); } }
    public string ServerStatusSummary => IsServerRefreshing ? "正在查询服务器状态…" : serverFeedback + "\n" + string.Join("\n", Owner.Snapshot.Servers.Where(s => s.Id != "none").Select(s => s.Name + "：" + (s.OnlinePlayers is { } n ? n + " 人在线" : s.Status)));
    public bool IsUpdatesOpen { get => updatesOpen; private set => SetProperty(ref updatesOpen, value); }
    public bool IsUpdatesLoading { get => updatesLoading; private set { SetProperty(ref updatesLoading, value); RefreshUpdatesCommand.NotifyCanExecuteChanged(); } }
    public bool HasUpdates => UpdateItems.Count > 0;
    public string UpdatesStatus { get => updatesStatus; private set => SetProperty(ref updatesStatus, value); }
    public string UpdatesCacheSummary { get => updatesCache; private set => SetProperty(ref updatesCache, value); }
    public RelayCommand CloseUpdatesCommand { get; private set; } = null!;
    public AsyncRelayCommand RefreshUpdatesCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenUpdatesWebsiteCommand { get; private set; } = null!;
    private void InitializeCommunityCommands()
    {
        CloseUpdatesCommand = Command(CloseUpdates);
        RefreshUpdatesCommand = AsyncCommand(() => ReadUpdatesAsync(true), () => !IsUpdatesLoading);
        OpenUpdatesWebsiteCommand = Open(ClubUpdateNoticeParser.IndexUrl);
    }
    public void StartCommunityRefresh()
    {
        if (communityStarted || Service is not IClubContentService) return;
        communityStarted = true; _ = RefreshServerStatusAsync(); _ = PollServersAsync();
    }
    private async Task PollServersAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try { while (await timer.WaitForNextTickAsync(communityLifetime.Token)) await RefreshServerStatusAsync(); }
        catch (OperationCanceledException) when (communityLifetime.IsCancellationRequested) { }
    }
    private Task RefreshServerStatusAsync() => statusRequest is { IsCompleted: false } ? statusRequest : statusRequest = RefreshServerStatusCoreAsync();
    private async Task RefreshServerStatusCoreAsync()
    {
        IsServerRefreshing = true; OnPropertyChanged(nameof(ServerStatusSummary));
        try { await Service.RefreshServersAsync(communityLifetime.Token); serverFeedback = "已刷新 " + DateTime.Now.ToString("HH:mm"); }
        catch (OperationCanceledException) when (communityLifetime.IsCancellationRequested) { }
        catch (Exception) { serverFeedback = "暂时无法查询，可点击刷新重试。"; }
        finally { IsServerRefreshing = false; OnPropertyChanged(nameof(ServerStatusSummary)); }
    }
    private async Task OpenUpdatesAsync()
    {
        if (IsUpdatesOpen) return;
        updatesRead?.Dispose(); updatesRead = CancellationTokenSource.CreateLinkedTokenSource(communityLifetime.Token);
        ++updatesGeneration; IsUpdatesOpen = true; await ReadUpdatesAsync(false);
    }
    private async Task ReadUpdatesAsync(bool refresh)
    {
        if (IsUpdatesLoading || !IsUpdatesOpen) return;
        if (Service is not IClubContentService service) { UpdatesStatus = "当前服务不支持内嵌日志，可在社团网站阅读。"; return; }
        var ct = updatesRead!.Token; var generation = updatesGeneration; IsUpdatesLoading = true; UpdatesStatus = "正在读取社团更新日志…";
        try
        {
            var result = await service.GetClubUpdatesAsync(refresh, ct); ct.ThrowIfCancellationRequested();
            if (generation != updatesGeneration) return;
            var previous = UpdateItems.ToDictionary(i => i.Notice.Id);
            UpdateItems.Clear(); foreach (var notice in result.Notices) UpdateItems.Add(previous.GetValueOrDefault(notice.Id) ?? new(this, notice));
            OnPropertyChanged(nameof(HasUpdates));
            UpdatesStatus = result.Warning ?? (result.FromCache ? "已读取缓存" : "已刷新 " + result.FetchedAt.LocalDateTime.ToString("HH:mm"));
            UpdatesCacheSummary = "断网时显示本地缓存。";
            if (UpdateItems.FirstOrDefault() is { } first)
            { first.ExpandWithoutLoading(); if (refresh || !first.Loaded) await ReadArticleAsync(first, refresh); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { if (generation == updatesGeneration) { UpdatesStatus = UiPrivacy.Redact(ex.Message); UpdatesCacheSummary = HasUpdates ? "保留已有内容，可重试。" : "可重试或在社团网站阅读。"; } }
        finally { if (generation == updatesGeneration) IsUpdatesLoading = false; }
    }
    internal async Task ReadArticleAsync(ClubUpdateItemViewModel row, bool refresh = false)
    {
        if (Service is not IClubContentService service || updatesRead is null || !IsUpdatesOpen || row.IsLoading || !UpdateItems.Contains(row)) return;
        var ct = updatesRead.Token; var generation = updatesGeneration; row.IsLoading = true;
        try { var result = await service.GetClubUpdateArticleAsync(row.Notice, refresh, ct); ct.ThrowIfCancellationRequested(); if (generation == updatesGeneration && UpdateItems.Contains(row)) { row.Body = result.Body; row.Loaded = true; } }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { if (generation == updatesGeneration) row.Body = UiPrivacy.Redact(ex.Message) + "\n可点击重新读取正文。"; }
        finally { row.IsLoading = false; }
    }
    private void CloseUpdates() { updatesRead?.Cancel(); ++updatesGeneration; IsUpdatesOpen = false; IsUpdatesLoading = false; }
    public void Dispose() { CloseUpdates(); communityLifetime.Cancel(); }
}
