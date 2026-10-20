using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.IdentityModel.OAuth;

namespace PCL.Core.Test.Minecraft.Authentication;

[TestClass]
public class OAuthClientResponseTest
{
    [TestMethod]
    public async Task DeviceAuthorizationPendingErrorRemainsAvailableToPoller()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.BadRequest,
            "{\"error\":\"authorization_pending\",\"error_description\":\"pending\"}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var result = await client.AuthorizeWithDeviceAsync(
            new DeviceCodeData { DeviceCode = "device-code" }, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual("authorization_pending", result.Error);
        Assert.AreEqual("pending", result.ErrorDescription);
    }

    [TestMethod]
    public async Task NonJsonGatewayErrorDoesNotExposeResponseBody()
    {
        const string marker = "oauth-gateway-body-marker";
        var handler = new StaticResponseHandler(HttpStatusCode.BadGateway, $"<html>{marker}</html>", "text/html");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await AssertThrowsAsync<IdentityModelAuthenticationException>(
            () => client.AuthorizeWithDeviceAsync(
                new DeviceCodeData { DeviceCode = "device-code" }, CancellationToken.None));

        Assert.AreEqual("http_502", exception.Error);
        Assert.IsFalse(exception.ToString().Contains(marker, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task NullSuccessResponseBecomesProtocolError()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK, "null");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await AssertThrowsAsync<IdentityModelAuthenticationException>(
            () => client.AuthorizeWithDeviceAsync(
                new DeviceCodeData { DeviceCode = "device-code" }, CancellationToken.None));

        Assert.AreEqual("invalid_response", exception.Error);
    }

    [TestMethod]
    public async Task JsonGatewayErrorWithoutProtocolFieldsBecomesStatusError()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.BadGateway, "{}");
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await AssertThrowsAsync<IdentityModelAuthenticationException>(
            () => client.AuthorizeWithDeviceAsync(
                new DeviceCodeData { DeviceCode = "device-code" }, CancellationToken.None));

        Assert.AreEqual("http_502", exception.Error);
    }

    private static SimpleOAuthClient CreateClient(HttpClient httpClient)
        => new(new OAuthClientOptions
        {
            ClientId = "client-id",
            RedirectUri = string.Empty,
            AddRequestMetadata = false,
            GetClient = () => httpClient,
            Meta = new EndpointMeta
            {
                AuthorizeEndpoint = "https://auth.example.test/open/authorize",
                DeviceEndpoint = "https://auth.example.test/open/device",
                TokenEndpoint = "https://auth.example.test/open/token"
            }
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            });
    }
}
