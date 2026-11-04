using SHOU.Launcher.Core.Abstractions;

namespace SHOU.Launcher.Core.Authentication;

public static class AccountIdentity
{
    public static bool Matches(AccountProfile metadata, AccountProfile protectedIdentity)
    {
        if (metadata.Id != protectedIdentity.Id || metadata.Kind != protectedIdentity.Kind || !Enum.IsDefined(metadata.Kind)
            || !Guid.TryParse(metadata.PlayerUuid, out var actual) || !Guid.TryParse(protectedIdentity.PlayerUuid, out var expected) || actual != expected) return false;
        if (metadata.Kind != AccountKind.Yggdrasil) return string.IsNullOrWhiteSpace(metadata.AuthenticationServer) && string.IsNullOrWhiteSpace(protectedIdentity.AuthenticationServer);
        try
        {
            return string.Equals(AuthenticationClient.NormalizeYggdrasilRoot(new Uri(metadata.AuthenticationServer!)),
                AuthenticationClient.NormalizeYggdrasilRoot(new Uri(protectedIdentity.AuthenticationServer!)), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is AuthenticationException or UriFormatException or ArgumentNullException) { return false; }
    }
}
