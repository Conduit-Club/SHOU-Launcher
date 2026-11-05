using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Desktop.ViewModels;

public sealed partial class AccountsPageViewModel
{
    private LegacyMicrosoftAccountChoice? _selectedLegacyAccount;
    private string _legacyImportSummary = "读取本机旧版保存的微软账户，选定后重新验证授权与游戏所有权。旧版文件保持原状。";
    private ILegacyMicrosoftAccountMigrationService? LegacyImport => Service as ILegacyMicrosoftAccountMigrationService;
    public bool SupportsLegacyMicrosoftImport => LegacyImport?.SupportsLegacyMicrosoftImport == true;
    public ObservableCollection<LegacyMicrosoftAccountChoice> LegacyMicrosoftAccounts { get; } = [];
    public LegacyMicrosoftAccountChoice? SelectedLegacyMicrosoftAccount
    {
        get => _selectedLegacyAccount;
        set { if (SetProperty(ref _selectedLegacyAccount, value)) NotifyCommands(); }
    }
    public bool HasLegacyMicrosoftAccounts => LegacyMicrosoftAccounts.Count > 0;
    public string LegacyImportSummary { get => _legacyImportSummary; private set => SetProperty(ref _legacyImportSummary, value); }
    public AsyncRelayCommand ReadLegacyMicrosoftAccountsCommand { get; private set; } = null!;
    public AsyncRelayCommand ImportLegacyMicrosoftAccountCommand { get; private set; } = null!;

    private void InitializeLegacyImport()
    {
        ReadLegacyMicrosoftAccountsCommand = AsyncCommand(ReadLegacyMicrosoftAccountsAsync, () => CanEdit && SupportsLegacyMicrosoftImport);
        ImportLegacyMicrosoftAccountCommand = AsyncCommand(ImportLegacyMicrosoftAccountAsync,
            () => CanEdit && SupportsLegacyMicrosoftImport && SelectedLegacyMicrosoftAccount is not null);
    }

    private void ClearLegacyImportChoices()
    {
        SelectedLegacyMicrosoftAccount = null;
        LegacyMicrosoftAccounts.Clear();
        OnPropertyChanged(nameof(HasLegacyMicrosoftAccounts));
    }

    private async Task ReadLegacyMicrosoftAccountsAsync()
    {
        ClearLegacyImportChoices();
        var generation = BeginAuthentication();
        try
        {
            IReadOnlyList<LegacyMicrosoftAccountChoice> choices = [];
            var success = await Owner.RunAsync("只读检查旧版微软账户", async ct =>
                choices = await LegacyImport!.ReadLegacyMicrosoftAccountsAsync(ct));
            if (!IsCurrentAuthentication(generation)) return;
            if (success)
            {
                foreach (var choice in choices) LegacyMicrosoftAccounts.Add(choice);
                LegacyImportSummary = choices.Count == 0 ? "未找到可迁移的微软账户，可使用官网授权登录。"
                    : "请选择一个账户，再点击验证并导入。仅验证成功的账户会保存到新版。";
            }
            else LegacyImportSummary = "未能读取旧账户，可重试或使用官网授权登录；旧文件保持原状。";
            OnPropertyChanged(nameof(HasLegacyMicrosoftAccounts));
        }
        finally { EndAuthentication(generation); }
    }

    private async Task ImportLegacyMicrosoftAccountAsync()
    {
        if (SelectedLegacyMicrosoftAccount is not { } choice) return;
        var generation = BeginAuthentication();
        try
        {
            if (await Owner.RunAsync("验证并导入微软账户", async ct =>
                await LegacyImport!.ImportLegacyMicrosoftAccountAsync(choice.SourceId, ct)))
            {
                ClearLegacyImportChoices();
                IsAddingAccount = false;
            }
            else LegacyImportSummary = "验证未完成，未导入该账户。可重试，或使用微软官网重新授权。";
        }
        finally { EndAuthentication(generation); }
    }
}
