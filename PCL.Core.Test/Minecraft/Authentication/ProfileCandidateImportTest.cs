using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Authentication;
using PCL.Core.Minecraft.Profile.Models;

namespace PCL.Core.Test.Minecraft.Authentication;

[TestClass]
public sealed class ProfileCandidateImportTest
{
    [TestMethod]
    public void CandidatePlanDeduplicatesUuidAndServerWithoutCopyingTokens()
    {
        const string selectedId = "11111111-1111-1111-1111-111111111111";
        const string existingId = "22222222222222222222222222222222";
        const string newId = "33333333-3333-3333-3333-333333333333";
        var result = new AuthenticationResult
        {
            ProfileType = ProfileType.Authlib,
            UserName = "Selected",
            Uuid = selectedId,
            AccessToken = "selected-token",
            Server = "https://auth.example.test/api/yggdrasil/authserver",
            LoginName = "user@example.test",
            Password = "password-test",
            AvailableProfiles = new[]
            {
                new AuthenticationCandidate(selectedId, "Selected"),
                new AuthenticationCandidate("22222222-2222-2222-2222-222222222222", "Existing renamed"),
                new AuthenticationCandidate(existingId, "Existing duplicate"),
                new AuthenticationCandidate(newId, "New role")
            }
        };
        var existing = new McProfile
        {
            ProfileType = ProfileType.Authlib,
            Uuid = existingId.ToUpperInvariant(),
            UserName = "Existing",
            AccessToken = "existing-token",
            Server = "https://auth.example.test/api/yggdrasil",
            LoginName = "old-login",
            Password = "old-password"
        };
        var otherServer = new McProfile
        {
            ProfileType = ProfileType.Authlib,
            Uuid = existingId,
            UserName = "Other server",
            AccessToken = "other-server-token",
            Server = "https://other.example.test/api/yggdrasil",
            LoginName = "other-login",
            Password = "other-password"
        };

        var plan = ProfileService.PlanAuthenticationCandidates(result,
            new McProfile { ProfileType = ProfileType.Authlib, Uuid = selectedId },
            new[] { existing, otherServer }, importAvailableProfiles: true, cancellationToken: CancellationToken.None);

        Assert.AreEqual(1, plan.Updates.Count);
        Assert.AreEqual(1, plan.Additions.Count);
        Assert.AreEqual("Existing renamed", plan.Updates[0].Current.UserName);
        Assert.AreEqual("existing-token", plan.Updates[0].Current.AccessToken);
        Assert.AreEqual("Existing", existing.UserName);
        Assert.AreEqual("old-login", existing.LoginName);
        Assert.AreEqual("old-password", existing.Password);
        Assert.AreEqual(newId, plan.Additions[0].Uuid);
        Assert.AreEqual(string.Empty, plan.Additions[0].AccessToken);
        Assert.AreEqual("user@example.test", plan.Additions[0].LoginName);
        Assert.AreEqual("password-test", plan.Additions[0].Password);
        Assert.AreEqual("other-server-token", otherServer.AccessToken);
        Assert.AreEqual("Other server", otherServer.UserName);
        Assert.AreEqual("other-login", otherServer.LoginName);
        Assert.AreEqual("other-password", otherServer.Password);
    }

    [TestMethod]
    public void CandidatePlanDoesNotReimportWithoutExplicitNewLogin()
    {
        var result = CreateResult();
        var plan = ProfileService.PlanAuthenticationCandidates(result,
            new McProfile { ProfileType = ProfileType.Authlib, Uuid = result.Uuid },
            Array.Empty<McProfile>(), importAvailableProfiles: false, cancellationToken: CancellationToken.None);

        Assert.AreEqual(0, plan.Updates.Count);
        Assert.AreEqual(0, plan.Additions.Count);
    }

    [TestMethod]
    public void CandidatePlanChecksCancellationBeforeReturningChanges()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
        {
            _ = ProfileService.PlanAuthenticationCandidates(
                CreateResult(),
                new McProfile { ProfileType = ProfileType.Authlib, Uuid = "selected" },
                Array.Empty<McProfile>(), importAvailableProfiles: true, cancellationToken: cancellation.Token);
        });
    }

    private static AuthenticationResult CreateResult()
        => new()
        {
            ProfileType = ProfileType.Authlib,
            UserName = "Selected",
            Uuid = "selected",
            AccessToken = "selected-token",
            Server = "https://auth.example.test/api/yggdrasil/authserver",
            LoginName = "user@example.test",
            Password = "password-test",
            AvailableProfiles = new[]
            {
                new AuthenticationCandidate("selected", "Selected"),
                new AuthenticationCandidate("other", "Other")
            }
        };
}
