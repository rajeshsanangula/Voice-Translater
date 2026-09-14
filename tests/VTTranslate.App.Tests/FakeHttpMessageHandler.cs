using System.Net;
using System.Net.Http.Headers;

namespace VTTranslate.App.Tests;

/// <summary>Deterministic <see cref="HttpMessageHandler"/> test double — scripts a queue of responses and records every request's bearer token, so the bounded-401-retry policy can be proven without a real backend.</summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();
    public List<string?> RequestBearerTokens { get; } = new();
    public int RequestCount { get; private set; }

    // ---- Phase 7.3: request path/method/body capture — additive, does not change
    // any existing test's behavior — needed to verify the exact request contract
    // (e.g. POST /subscription/cancel's body) rather than only the response side. ----
    public List<(HttpMethod Method, string? Path)> Requests { get; } = new();
    public List<string?> RequestBodies { get; } = new();

    public void Enqueue(HttpStatusCode status, string? jsonBody = null) =>
        _responses.Enqueue(() =>
        {
            var response = new HttpResponseMessage(status);
            if (jsonBody is not null)
                response.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
            return response;
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        RequestBearerTokens.Add(request.Headers.Authorization?.Parameter);
        Requests.Add((request.Method, request.RequestUri?.PathAndQuery));
        RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));

        if (_responses.Count == 0)
            throw new InvalidOperationException("FakeHttpMessageHandler: no more scripted responses.");

        return _responses.Dequeue()();
    }
}
