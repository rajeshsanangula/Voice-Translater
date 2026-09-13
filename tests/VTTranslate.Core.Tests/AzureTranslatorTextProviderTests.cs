using System.Net;
using VTTranslate.Core.Streaming;

namespace VTTranslate.Core.Tests;

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }
    public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        LastRequestBody = request.Content != null ? await request.Content.ReadAsStringAsync(ct) : null;
        return ResponseFactory?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
    }
}

public class AzureTranslatorTextProviderTests
{
    // ---- Unauthorized (the actual, confirmed real-world result in this environment) ----
    [Fact]
    public async Task Unauthorized401_ReportedHonestly_NoFabricatedTranslation()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { ReasonPhrase = "Unauthorized" },
        };
        var provider = new AzureTranslatorTextProvider(new HttpClient(handler), "fake-key", "eastus");

        var result = await provider.TranslateAsync(
            new TranslationProviderRequest("en-US", "de-DE", "Hello"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.CandidateTranslatedText);
        Assert.Equal(401, result.HttpStatusCode);
        Assert.NotNull(result.FailureReason);
    }

    // ---- Successful response parsed correctly ----
    [Fact]
    public async Task SuccessfulResponse_ParsesCandidateTranslation()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"translations":[{"text":"Hallo","to":"de"}]}]""", System.Text.Encoding.UTF8, "application/json"),
            },
        };
        var provider = new AzureTranslatorTextProvider(new HttpClient(handler), "fake-key", "eastus");

        var result = await provider.TranslateAsync(
            new TranslationProviderRequest("en-US", "de-DE", "Hello"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Hallo", result.CandidateTranslatedText);
    }

    // ---- Bounded context is prepended to the request text (documented approximation) ----
    [Fact]
    public async Task BoundedContext_IsPrependedToRequestText()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"translations":[{"text":"x","to":"de"}]}]""", System.Text.Encoding.UTF8, "application/json"),
            },
        };
        var provider = new AzureTranslatorTextProvider(new HttpClient(handler), "fake-key", "eastus");

        await provider.TranslateAsync(
            new TranslationProviderRequest("en-US", "de-DE", "to schedule", "I would like"), CancellationToken.None);

        Assert.Contains("I would like to schedule", handler.LastRequestBody);
    }

    // ---- Credentials never appear in the failure reason or exception text ----
    [Fact]
    public async Task Credentials_NeverAppearInFailureReason()
    {
        const string secretKey = "super-secret-test-key-value";
        var handler = new FakeHttpMessageHandler
        {
            ResponseFactory = _ => throw new HttpRequestException("network unreachable"),
        };
        var provider = new AzureTranslatorTextProvider(new HttpClient(handler), secretKey, "eastus");

        var result = await provider.TranslateAsync(
            new TranslationProviderRequest("en-US", "de-DE", "Hello"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain(secretKey, result.FailureReason ?? "");
    }

    // ---- Step 5.6a: custom endpoint override is used when configured, default otherwise ----
    [Fact]
    public async Task CustomEndpoint_FromCredentialConfig_IsUsedInTheRequestUrl()
    {
        var handler = new FakeHttpMessageHandler();
        var config = new TranslatorCredentialConfig("fake-key", "eastus", "https://my-dedicated-translator.cognitiveservices.azure.com");
        var provider = AzureTranslatorTextProvider.FromCredentialConfig(new HttpClient(handler), config);

        await provider.TranslateAsync(new TranslationProviderRequest("en-US", "de-DE", "Hello"), CancellationToken.None);

        Assert.StartsWith("https://my-dedicated-translator.cognitiveservices.azure.com/translate", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task NoEndpointConfigured_UsesDefaultGlobalTranslatorEndpoint()
    {
        var handler = new FakeHttpMessageHandler();
        var config = new TranslatorCredentialConfig("fake-key", "eastus", null);
        var provider = AzureTranslatorTextProvider.FromCredentialConfig(new HttpClient(handler), config);

        await provider.TranslateAsync(new TranslationProviderRequest("en-US", "de-DE", "Hello"), CancellationToken.None);

        Assert.StartsWith(AzureTranslatorTextProvider.DefaultEndpoint, handler.LastRequest!.RequestUri!.ToString());
    }

    // ---- Credentials sent only as headers, never in the URL or body ----
    [Fact]
    public async Task Credentials_SentOnlyAsHeaders_NeverInUrlOrBody()
    {
        const string secretKey = "super-secret-test-key-value";
        var handler = new FakeHttpMessageHandler();
        var provider = new AzureTranslatorTextProvider(new HttpClient(handler), secretKey, "eastus");

        await provider.TranslateAsync(new TranslationProviderRequest("en-US", "de-DE", "Hello"), CancellationToken.None);

        Assert.DoesNotContain(secretKey, handler.LastRequest!.RequestUri!.ToString());
        Assert.DoesNotContain(secretKey, handler.LastRequestBody ?? "");
        Assert.Equal(secretKey, handler.LastRequest.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
    }
}
