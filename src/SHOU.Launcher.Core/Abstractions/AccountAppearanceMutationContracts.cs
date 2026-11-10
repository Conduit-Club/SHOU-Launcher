namespace SHOU.Launcher.Core.Abstractions;

public enum AccountAppearanceAction { UploadSkin, WearCape, HideCape }
public enum AccountAppearanceWriteState { Verified, Rejected, AcceptedNeedsRefresh, OutcomeUnknown }
public sealed record AccountAppearanceChange(AccountAppearanceAction Action, ReadOnlyMemory<byte> Png = default,
    AccountSkinModel Model = AccountSkinModel.Classic, string? CapeId = null);
public sealed record AccountAppearanceWriteResult(AccountAppearanceWriteState State, AccountAppearance? Appearance, string Message,
    string? BackupDirectory = null);
public interface IAccountAppearanceMutationService
{
    Task<AccountAppearanceWriteResult> ChangeAccountAppearanceAsync(string accountId, AccountAppearanceChange change, CancellationToken cancellationToken = default);
}
