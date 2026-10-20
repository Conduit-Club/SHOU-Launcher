using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.IO.Net;
using PCL.Core.IO.Net.Http;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.IdentityModel.Yggdrasil;
using PCL.Core.Minecraft.Profile.Models;
using YggdrasilProfile = PCL.Core.Minecraft.IdentityModel.Yggdrasil.Profile;

namespace PCL.Core.Minecraft.Profile.Authentication;

public sealed class AuthlibProvider : IAuthenticateProvider
{
    private readonly string _apiRoot;
    private readonly string _authServer;
    private readonly Func<HttpClient> _getClient;

    public AuthlibProvider(string server, Func<HttpClient>? getClient = null)
    {
        _apiRoot = _NormalizeServer(server);
        _authServer = _apiRoot + "/authserver";
        _getClient = getClient ?? (() => NetworkService.GetClient(NetworkService.Default));
    }

    public async Task<AuthenticationResult> AuthenticateAsync(AuthenticationRequest request, CancellationToken token)
    {
        var options = new YggdrasilLegacyAuthenticateOptions
        {
            YggdrasilApiLocation = _apiRoot,
            Username = request.Username,
            Password = request.Password,
            GetClient = _getClient
        };
        var result = await new YggdrasilLegacyClient(options).AuthenticateAsync(token).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Yggdrasil authentication response was empty.");
        if (!string.IsNullOrWhiteSpace(result.Error))
            throw new IdentityModelAuthenticationException(result.Error, result.ErrorMessage);
        // A profile-selection refresh may omit availableProfiles. Keep the
        // list from the password response so the service can import every
        // role without copying the selected role's token.
        var availableProfiles = result.AvailableProfiles;
        var selected = await _SelectProfileAsync(result, request, token).ConfigureAwait(false);
        if (selected is null)
            throw new IdentityModelAuthenticationException("invalid_profile", "The authentication server returned no selectable profile.");
        if (!_ProfileIdsEqual(result.SelectedProfile?.Id, selected.Id))
        {
            options.AccessToken = result.AccessToken;
            options.ClientToken = result.ClientToken;
            result = await new YggdrasilLegacyClient(options).RefreshAsync(token, selected).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Yggdrasil profile selection response was empty.");
            if (!string.IsNullOrWhiteSpace(result.Error))
                throw new IdentityModelAuthenticationException(result.Error, result.ErrorMessage);
            if (result.SelectedProfile is { } refreshed && !_ProfileIdsEqual(refreshed.Id, selected.Id))
                throw new IdentityModelAuthenticationException("invalid_profile",
                    "The authentication server returned a different profile than requested.");
            selected = result.SelectedProfile ?? selected;
        }
        var serverName = await _GetServerNameAsync(token).ConfigureAwait(false);
        return _CreateResult(result, selected, request.Username, request.Password, serverName, availableProfiles);
    }

    public async Task<AuthenticationResult> RefreshAsync(McProfile profile, CancellationToken token)
    {
        var options = new YggdrasilLegacyAuthenticateOptions
        {
            YggdrasilApiLocation = _apiRoot,
            AccessToken = profile.AccessToken,
            ClientToken = profile.ClientToken,
            GetClient = _getClient
        };
        var result = await new YggdrasilLegacyClient(options).RefreshAsync(token, new YggdrasilProfile
        {
            Id = profile.Uuid,
            Name = profile.UserName
        }).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Yggdrasil refresh response was empty.");
        if (!string.IsNullOrWhiteSpace(result.Error))
            throw new IdentityModelAuthenticationException(result.Error, result.ErrorMessage);
        if (result.SelectedProfile is { } returned && !_ProfileIdsEqual(returned.Id, profile.Uuid))
            throw new IdentityModelAuthenticationException("invalid_profile",
                "The authentication server returned a different profile than requested.");
        if (result.SelectedProfile is null && result.AvailableProfiles is { Length: > 0 } available &&
            !available.Any(candidate => _ProfileIdsEqual(candidate.Id, profile.Uuid)))
            throw new IdentityModelAuthenticationException("invalid_profile",
                "The authentication server did not return the requested profile.");
        var selected = result.SelectedProfile ?? new YggdrasilProfile { Id = profile.Uuid, Name = profile.UserName };
        var serverName = await _GetServerNameAsync(token).ConfigureAwait(false);
        return _CreateResult(result, selected, profile.LoginName, profile.Password, serverName, result.AvailableProfiles);
    }

    public async Task<bool> ValidateAsync(McProfile profile, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(profile.AccessToken)) return false;
        var options = new YggdrasilLegacyAuthenticateOptions
        {
            YggdrasilApiLocation = _apiRoot,
            AccessToken = profile.AccessToken,
            ClientToken = profile.ClientToken,
            GetClient = _getClient
        };
        return await new YggdrasilLegacyClient(options).ValidateAsync(token).ConfigureAwait(false);
    }

    private async Task<YggdrasilProfile?> _SelectProfileAsync(YggdrasilAuthenticateResult result, AuthenticationRequest request, CancellationToken token)
    {
        var available = result.AvailableProfiles ?? [];
        if (!string.IsNullOrWhiteSpace(request.PreferredProfileId))
        {
            var preferred = available.FirstOrDefault(p =>
                _ProfileIdsEqual(p.Id, request.PreferredProfileId));
            if (preferred is not null) return preferred;
            if (result.SelectedProfile is { } selectedMatch &&
                _ProfileIdsEqual(selectedMatch.Id, request.PreferredProfileId))
                return selectedMatch;
            throw new IdentityModelAuthenticationException("invalid_profile",
                "The authentication server did not return the requested profile.");
        }
        if (!request.ForceReselectProfile && result.SelectedProfile is not null)
            return result.SelectedProfile;
        if (available.Length == 0) return result.SelectedProfile;
        if (request.ImportAvailableProfiles)
            return result.SelectedProfile ?? available[0];
        if (available.Length == 1 || request.ProfileSelector is null) return available[0];
        var candidates = available.Select(p => new AuthenticationCandidate(p.Id, p.Name ?? p.Id)).ToArray();
        var selected = await request.ProfileSelector(candidates, token).ConfigureAwait(false);
        return selected is null ? null : available.FirstOrDefault(p => p.Id == selected.Id);
    }

    private AuthenticationResult _CreateResult(YggdrasilAuthenticateResult result, YggdrasilProfile selected,
        string? username, string? password, string? serverName,
        IReadOnlyList<YggdrasilProfile>? availableProfiles = null)
        => new()
        {
            ProfileType = ProfileType.Authlib,
            UserName = selected.Name ?? string.Empty,
            Uuid = selected.Id,
            AccessToken = result.AccessToken ?? string.Empty,
            ClientToken = result.ClientToken ?? string.Empty,
            LoginName = username,
            Password = password,
            Server = _authServer,
            ServerName = serverName,
            Provider = "authlib",
            AvailableProfiles = (availableProfiles ?? Array.Empty<YggdrasilProfile>())
                .Where(profile => !string.IsNullOrWhiteSpace(profile.Id))
                .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var profile = group.First();
                    return new AuthenticationCandidate(profile.Id, profile.Name ?? profile.Id);
                })
                .ToArray()
        };

    private async Task<string?> _GetServerNameAsync(CancellationToken token)
    {
        try
        {
            using var response = await HttpRequest.Create(_apiRoot)
                .SendAsync(_getClient(), cancellationToken: token)
                .ConfigureAwait(false);
            if (!response.IsSuccess) return null;
            var metadata = await response.AsJsonAsync<JsonObject>(cancellationToken: token).ConfigureAwait(false);
            return metadata?["meta"]?["serverName"]?.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string _NormalizeServer(string server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        var value = server.TrimEnd('/');
        return value.EndsWith("/authserver", StringComparison.OrdinalIgnoreCase)
            ? value[..^"/authserver".Length]
            : value;
    }

    private static bool _ProfileIdsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        if (Guid.TryParse(left, out var leftGuid) && Guid.TryParse(right, out var rightGuid))
            return leftGuid == rightGuid;
        return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
