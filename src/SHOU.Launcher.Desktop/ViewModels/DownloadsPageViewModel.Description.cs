using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class DownloadsPageViewModel
{
    private string translatedDescription = "";
    public string TranslatedDescription { get => translatedDescription; private set { SetProperty(ref translatedDescription, value); OnPropertyChanged(nameof(HasTranslatedDescription)); } }
    public bool HasTranslatedDescription => TranslatedDescription.Length > 0;
    public bool CanTranslateDescription => Localization.UiLocalization.Instance.Locale == "zh-CN" && Service is IContentDescriptionService;
    public bool HasWikiReference => Localization.UiLocalization.Instance.Locale == "zh-CN" && _selectedProject is { } project && ContentReferenceIndex.Find(project) is not null;
    private AsyncRelayCommand? translateDescriptionCommand, openWikiReferenceCommand;
    public AsyncRelayCommand TranslateDescriptionCommand => translateDescriptionCommand ??= new(TranslateDescriptionAsync);
    public AsyncRelayCommand OpenWikiReferenceCommand => openWikiReferenceCommand ??= new(() => _selectedProject is { } project && ContentReferenceIndex.Find(project) is { } reference ? Owner.RunControlAsync("打开 MC 百科", ct => Owner.ExternalLauncher.OpenUriAsync(reference.PageUri, ct)) : Task.CompletedTask);
    private async Task TranslateDescriptionAsync()
    {
        if (_selectedProject is not { } project || Service is not IContentDescriptionService service) return;
        var revision = _requestRevision;
        await ReadBrowseAsync("翻译简介", revision, async ct =>
        { var text = await service.GetChineseContentDescriptionAsync(project, ct); if (revision == _requestRevision && _selectedProject == project && !ct.IsCancellationRequested) TranslatedDescription = text ?? "暂无中文翻译。"; }, "description", page: false, failure: message => TranslatedDescription = message);
    }
    private void ResetProjectDescription()
    { TranslatedDescription = ""; OnPropertyChanged(nameof(CanTranslateDescription)); OnPropertyChanged(nameof(HasWikiReference)); }
}
