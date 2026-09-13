using System.Net;
using System.Net.Http.Headers;

namespace VTTranslate.App.Tests;

/// <summary>Deterministic <see cref="HttpMessageHandler"/> test double — scripts a queue of responses and records every request's bearer token, so the bounded-401-retry policy can be proven without a real backend.</summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();
    public List<string?> RequestBearerTokens { get; } = new();
    public int RequestCount { get; private set; }

    public void Enqueue(HttpStatusCode status, string? jsonBody = null) =>
        _responses.Enqueue(() =>
        {
            var response = new HttpResponseMessage(status);
            if (jsonBody is not null)
                response.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
            return response;
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        RequestBearerTokens.Add(request.Headers.Authorization?.Parameter);

        if (_responses.Count == 0)
            throw new InvalidOperationException("FakeHttpMessageHandler: no more scripted responses.");

        return Task.FromResult(_responses.Dequeue()());
    }
}
