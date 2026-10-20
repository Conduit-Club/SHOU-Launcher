using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.Profile.Authentication;
using PCL.Core.Minecraft.Profile.Models;

namespace PCL.Core.Test.Minecraft.Authentication;

[TestClass]
public sealed class AuthlibProviderTest
{
    [TestMethod]
    public async Task PasswordLoginKeepsAllCandidatesAndHonorsPreferredProfile()
    {
        var httpClientHandler = new MultiProfileHandler();
        using var httpClient = new HttpClient(httpClientHandler);
        var provider = new AuthlibProvider("https://auth.example.test/api/yggdrasil", () => httpClient);

        var result = await provider.AuthenticateAsync(new AuthenticationRequest
        {
            Username = "user@example.test",
            Password = "password-test",
            PreferredProfileId = "profile-two"
        }, CancellationToken.None);

        Assert.AreEqual("profile-two", result.Uuid);
        Assert.AreEqual("token-two", result.AccessToken);
        CollectionAssert.AreEquivalent(new[] { "profile-one", "profile-two" },
            result.AvailableProfiles.Select(candidate => candidate.Id).ToArray());
        Assert.AreEqual("MUA test", result.ServerName);
        StringAssert.Contains(httpClientHandler.RefreshBody!,
            "\"selectedProfile\":{\"id\":\"profile-two\"");
    }

    [TestMethod]
    public async Task ImportModeUsesServerSelectedProfileWithoutAnotherRoleDialog()
    {
        using var httpClient = new HttpClient(new ImportWithoutSelectionHandler());
        var provider = new AuthlibProvider("https://auth.example.test/api/yggdrasil", () => httpClient);

        var result = await provider.AuthenticateAsync(new AuthenticationRequest
        {
            Username = "user@example.test",
            Password = "password-test",
            ImportAvailableProfiles = true,
            ProfileSelector = (_, _) => throw new AssertFailedException("import mode must not open a role dialog")
        }, CancellationToken.None);

        Assert.AreEqual("profile-one", result.Uuid);
        Assert.AreEqual("token-one", result.AccessToken);
    }

    [TestMethod]
    public async Task MissingPreferredProfileFailsInsteadOfFallingBackToFirstProfile()
    {
        using var httpClient = new HttpClient(new MissingPreferredProfileHandler());
        var provider = new AuthlibProvider("https://auth.example.test/api/yggdrasil", () => httpClient);

        var exception = await AssertThrowsAsync<IdentityModelAuthenticationException>(() => provider.AuthenticateAsync(
            new AuthenticationRequest
            {
                Username = "user@example.test",
                Password = "password-test",
                PreferredProfileId = "profile-two"
            }, CancellationToken.None));

        Assert.AreEqual("invalid_profile", exception.Error);
    }

    [TestMethod]
    public async Task RefreshRejectsAResponseForAnotherProfile()
    {
        using var httpClient = new HttpClient(new MismatchedRefreshHandler());
        var provider = new AuthlibProvider("https://auth.example.test/api/yggdrasil", () => httpClient);

        var exception = await AssertThrowsAsync<IdentityModelAuthenticationException>(() => provider.RefreshAsync(
            new McProfile
            {
                ProfileType = ProfileType.Authlib,
                Uuid = "profile-one",
                UserName = "One",
                AccessToken = "token-one",
                ClientToken = "client-token",
                Server = "https://auth.example.test/api/yggdrasil/authserver"
            }, CancellationToken.None));

        Assert.AreEqual("invalid_profile", exception.Error);
    }

    private static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        Assert.Fail($"Expected {typeof(TException).Name}.");
        return null!;
    }

    private abstract class RoutingHandler : HttpMessageHandler
    {
        protected readonly List<string> RequestBodies = [];

        protected static HttpResponseMessage Json(string content, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return Respond(request);
        }

        protected abstract HttpResponseMessage Respond(HttpRequestMessage request);
    }

    private sealed class MultiProfileHandler : RoutingHandler
    {
        public string? RefreshBody => RequestBodies.Count > 1 ? RequestBodies[1] : null;

        protected override HttpResponseMessage Respond(HttpRequestMessage request)
            => request.RequestUri!.AbsolutePath switch
            {
                "/api/yggdrasil/authserver/authenticate" => Json(
                    "{\"accessToken\":\"token-one\",\"clientToken\":\"client-token\",\"selectedProfile\":{\"id\":\"profile-one\",\"name\":\"One\"},\"availableProfiles\":[{\"id\":\"profile-one\",\"name\":\"One\"},{\"id\":\"profile-two\",\"name\":\"Two\"}] }"),
                "/api/yggdrasil/authserver/refresh" => Json(
                    "{\"accessToken\":\"token-two\",\"clientToken\":\"client-two\",\"selectedProfile\":{\"id\":\"profile-two\",\"name\":\"Two\"}}"),
                "/api/yggdrasil" => Json("{\"meta\":{\"serverName\":\"MUA test\"}}"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
    }

    private sealed class MissingPreferredProfileHandler : RoutingHandler
    {
        protected override HttpResponseMessage Respond(HttpRequestMessage request)
            => Json(
                "{\"accessToken\":\"token-one\",\"clientToken\":\"client-token\",\"selectedProfile\":{\"id\":\"profile-one\",\"name\":\"One\"},\"availableProfiles\":[{\"id\":\"profile-one\",\"name\":\"One\"}]} ");
    }

    private sealed class ImportWithoutSelectionHandler : RoutingHandler
    {
        protected override HttpResponseMessage Respond(HttpRequestMessage request)
            => request.RequestUri!.AbsolutePath switch
            {
                "/api/yggdrasil/authserver/authenticate" => Json(
                    "{\"accessToken\":\"token-one\",\"clientToken\":\"client-token\",\"availableProfiles\":[{\"id\":\"profile-one\",\"name\":\"One\"},{\"id\":\"profile-two\",\"name\":\"Two\"}] }"),
                "/api/yggdrasil/authserver/refresh" => Json(
                    "{\"accessToken\":\"token-one\",\"clientToken\":\"client-token\",\"selectedProfile\":{\"id\":\"profile-one\",\"name\":\"One\"}}"),
                "/api/yggdrasil" => Json("{\"meta\":{\"serverName\":\"MUA test\"}}"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
    }

    private sealed class MismatchedRefreshHandler : RoutingHandler
    {
        protected override HttpResponseMessage Respond(HttpRequestMessage request)
            => Json("{\"accessToken\":\"token-two\",\"selectedProfile\":{\"id\":\"profile-two\",\"name\":\"Two\"}}");
    }
}
