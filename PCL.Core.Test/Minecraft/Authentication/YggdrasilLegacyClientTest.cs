using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.IdentityModel.Yggdrasil;

namespace PCL.Core.Test.Minecraft.Authentication;

[TestClass]
public class YggdrasilLegacyClientTest
{
    [TestMethod]
    public async Task SuccessfulResponseReturnsSelectedProfile()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            "{\"accessToken\":\"access-token\",\"clientToken\":\"client-token\",\"selectedProfile\":{\"id\":\"profile-id\",\"name\":\"Player\"},\"availableProfiles\":[{\"id\":\"profile-id\",\"name\":\"Player\"}]} ");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var result = await client.AuthenticateAsync(CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual("access-token", result.AccessToken);
        Assert.AreEqual("profile-id", result.SelectedProfile!.Id);
        Assert.AreEqual("Player", result.SelectedProfile.Name);
        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual("/api/yggdrasil/authserver/authenticate", handler.RequestUri!.AbsolutePath);
    }

    [TestMethod]
    public async Task HtmlSuccessResponseBecomesSafeProtocolError()
    {
        const string marker = "html-marker-that-must-not-leak";
        var handler = new StaticResponseHandler(HttpStatusCode.OK, $"<html>{marker}</html>", "text/html");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await AssertThrowsAsync<IdentityModelAuthenticationException>(
            () => client.AuthenticateAsync(CancellationToken.None));

        Assert.AreEqual("invalid_response", exception.Error);
        Assert.IsFalse(exception.ToString().Contains(marker, StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains("password-test", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GatewayHtmlErrorBecomesStatusOnlyProtocolError()
    {
        const string marker = "gateway-body-marker-that-must-not-leak";
        var handler = new StaticResponseHandler(HttpStatusCode.BadGateway, $"<html>{marker}</html>", "text/html");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await AssertThrowsAsync<IdentityModelAuthenticationException>(
            () => client.AuthenticateAsync(CancellationToken.None));

        Assert.AreEqual("http_502", exception.Error);
        Assert.IsTrue(exception.Message.Contains("HTTP 502", StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains(marker, StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains("password-test", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GatewayJsonWithoutProtocolErrorBecomesStatusOnlyProtocolError()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.BadGateway, "{}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await AssertThrowsAsync<IdentityModelAuthenticationException>(
            () => client.AuthenticateAsync(CancellationToken.None));

        Assert.AreEqual("http_502", exception.Error);
        Assert.IsTrue(exception.Message.Contains("HTTP 502", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task NullSuccessResponseBecomesSafeProtocolError()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK, "null");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await AssertThrowsAsync<IdentityModelAuthenticationException>(
            () => client.AuthenticateAsync(CancellationToken.None));

        Assert.AreEqual("invalid_response", exception.Error);
    }

    [TestMethod]
    public async Task JsonErrorResponseRemainsAvailableToProvider()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.Forbidden,
            "{\"error\":\"ForbiddenOperationException\",\"errorMessage\":\"Invalid credentials\"}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var result = await client.AuthenticateAsync(CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual("ForbiddenOperationException", result.Error);
        Assert.AreEqual("Invalid credentials", result.ErrorMessage);
    }

    [TestMethod]
    public async Task RefreshGatewayHtmlBecomesStatusOnlyProtocolError()
    {
        const string marker = "refresh-gateway-body-marker-that-must-not-leak";
        var handler = new StaticResponseHandler(HttpStatusCode.BadGateway, $"<html>{marker}</html>", "text/html");
        using var httpClient = new HttpClient(handler);
        var client = CreateRefreshClient(httpClient);

        var exception = await AssertThrowsAsync<IdentityModelAuthenticationException>(
            () => client.RefreshAsync(CancellationToken.None, new Profile { Id = "profile-id", Name = "Player" }));

        Assert.AreEqual("http_502", exception.Error);
        Assert.IsFalse(exception.ToString().Contains(marker, StringComparison.Ordinal));
    }

    private static YggdrasilLegacyClient CreateClient(HttpClient httpClient)
        => new(new YggdrasilLegacyAuthenticateOptions
        {
            YggdrasilApiLocation = "https://auth.example.test/api/yggdrasil",
            Username = "user@example.test",
            Password = "password-test",
            AddRequestMetadata = false,
            GetClient = () => httpClient
        });

    private static YggdrasilLegacyClient CreateRefreshClient(HttpClient httpClient)
        => new(new YggdrasilLegacyAuthenticateOptions
        {
            YggdrasilApiLocation = "https://auth.example.test/api/yggdrasil",
            AccessToken = "access-token",
            ClientToken = "client-token",
            AddRequestMetadata = false,
            GetClient = () => httpClient
        });

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

    private sealed class StaticResponseHandler(HttpStatusCode statusCode, string body, string contentType = "application/json")
        : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            });
        }
    }
}
