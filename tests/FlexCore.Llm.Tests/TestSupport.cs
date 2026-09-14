using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fx.ControlKit.Llm;
using Fx.ControlKit.Llm.Configuration;
using Microsoft.Extensions.Options;

namespace FlexCore.Llm.Tests;

/// <summary>One request the fake handler saw, with the body already read.</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Url, HttpRequestHeaders Headers, string Body)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;

    public string? Header(string name)
        => Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
}

/// <summary>Canned response returned by <see cref="FakeHttpMessageHandler"/>.</summary>
public sealed record CannedResponse(HttpStatusCode Status, string Body, string ContentType = "application/json", TimeSpan? RetryAfter = null, TimeSpan? Delay = null)
{
    public static CannedResponse Ok(string body, string contentType = "application/json") => new(HttpStatusCode.OK, body, contentType);
    public static CannedResponse Sse(string body) => new(HttpStatusCode.OK, body, "text/event-stream");
    public static CannedResponse Ndjson(string body) => new(HttpStatusCode.OK, body, "application/x-ndjson");
    public static CannedResponse Error(HttpStatusCode status, string body, TimeSpan? retryAfter = null) => new(status, body, "application/json", retryAfter);
}

public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly object _lock = new();
    private readonly Queue<CannedResponse> _responses = new();

    public List<RecordedRequest> Requests { get; } = new();

    public FakeHttpMessageHandler Enqueue(params CannedResponse[] responses)
    {
        lock (_lock)
        {
            foreach (var response in responses) _responses.Enqueue(response);
        }

        return this;
    }

    public RecordedRequest Last => Requests[^1];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        CannedResponse canned;
        lock (_lock)
        {
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers, body));
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException($"No canned response for {request.Method} {request.RequestUri}");
            }

            canned = _responses.Dequeue();
        }

        if (canned.Delay is { } delay)
        {
            await Task.Delay(delay, cancellationToken);
        }

        var response = new HttpResponseMessage(canned.Status)
        {
            Content = new StringContent(canned.Body, Encoding.UTF8, canned.ContentType),
            RequestMessage = request,
        };
        if (canned.RetryAfter is { } retryAfter)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        }

        return response;
    }
}

public sealed class SingleClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
}

public sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>Builds providers against a fake handler with an in-memory environment and options.</summary>
public sealed class TestHost
{
    public FakeHttpMessageHandler Handler { get; } = new();
    public Dictionary<string, string?> Environment { get; } = new(StringComparer.Ordinal);
    public LlmOptions Options { get; } = new();

    public IHttpClientFactory Http => new SingleClientFactory(Handler);

    public ICredentialResolver Credentials
        => new EnvironmentFirstCredentialResolver(new StaticOptionsMonitor<LlmOptions>(Options), name => Environment.TryGetValue(name, out var v) ? v : null);

    public TestHost Env(string name, string? value)
    {
        Environment[name] = value;
        return this;
    }

    public TestHost Enqueue(params CannedResponse[] responses)
    {
        Handler.Enqueue(responses);
        return this;
    }

    public static ChatRequest Prompt(string model, string user = "Hello", string? system = "Be brief.")
        => ChatRequest.FromPrompt(ModelRef.Parse(model), user, system);
}

public static class Streams
{
    /// <summary>Joins SSE events; each entry is "event-name|json" or just "json".</summary>
    public static string Sse(params string[] events)
    {
        var sb = new StringBuilder();
        foreach (var evt in events)
        {
            var split = evt.IndexOf('|');
            if (split > 0 && !evt.StartsWith('{'))
            {
                sb.Append("event: ").Append(evt[..split]).Append('\n');
                sb.Append("data: ").Append(evt[(split + 1)..]).Append("\n\n");
            }
            else
            {
                sb.Append("data: ").Append(evt).Append("\n\n");
            }
        }

        return sb.ToString();
    }

    public static async Task<List<ChatDelta>> Collect(IAsyncEnumerable<ChatDelta> stream)
    {
        var list = new List<ChatDelta>();
        await foreach (var delta in stream) list.Add(delta);
        return list;
    }

    public static string Text(IEnumerable<ChatDelta> deltas) => string.Concat(deltas.Select(d => d.TextDelta));
}
