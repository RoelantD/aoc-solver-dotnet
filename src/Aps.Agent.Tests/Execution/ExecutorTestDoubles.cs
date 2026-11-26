using System.Net;
using System.Text;
using Aps.Agent.Abstractions;
using Azure.Core;

namespace Aps.Agent.Tests.Execution;

/// <summary>A captured request (the body is read before the request is disposed).</summary>
internal sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Authorization, string Body);

/// <summary>Fake transport: answers each request with the next scripted response; no network.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<CapturedRequest, HttpResponseMessage>> _responses = new();

    public List<CapturedRequest> Requests { get; } = [];

    public FakeHttpHandler Respond(HttpStatusCode status, string body = "{}", Action<HttpResponseMessage>? configure = null)
    {
        _responses.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            configure?.Invoke(response);
            return response;
        });
        return this;
    }

    public FakeHttpHandler Throw(Exception exception)
    {
        _responses.Enqueue(_ => throw exception);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var captured = new CapturedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body);
        Requests.Add(captured);
        if (!_responses.TryDequeue(out var next))
            throw new InvalidOperationException($"no scripted response for {request.Method} {request.RequestUri}");
        return next(captured);
    }
}

internal sealed class FakeTokenCredential(string token, DateTimeOffset expiresOn) : TokenCredential
{
    public int Calls { get; private set; }

    public List<string[]> Scopes { get; } = [];

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        Calls++;
        Scopes.Add(requestContext.Scopes);
        return new AccessToken(token, expiresOn);
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}

/// <summary>Clock whose delays return immediately and are recorded.</summary>
internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;

    public List<TimeSpan> Delays { get; } = [];

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        Delays.Add(delay);
        UtcNow += delay;
        return Task.CompletedTask;
    }
}
