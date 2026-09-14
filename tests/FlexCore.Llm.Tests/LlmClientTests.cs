using System.Net;
using Fx.ControlKit.Llm;
using Fx.ControlKit.Llm.Configuration;
using Fx.ControlKit.Llm.DependencyInjection;
using Fx.ControlKit.Llm.Pricing;
using Fx.ControlKit.Llm.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlexCore.Llm.Tests;

public class LlmClientTests
{
    private const string GroqOk = """{"choices":[{"message":{"content":"done"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1},"model":"llama-3.3-70b-versatile"}""";

    private sealed class RecordingObserver : ILlmCallObserver
    {
        public List<string> Events { get; } = new();
        public LlmCallOutcome? Outcome { get; private set; }
        public void OnStarted(LlmCallContext call) => Events.Add($"started:{call.Model}:{call.PromptLength}");
        public void OnCompleted(LlmCallContext call, LlmCallOutcome outcome) { Events.Add($"completed:{outcome.Attempts}"); Outcome = outcome; }
        public void OnFailed(LlmCallContext call, Exception error, TimeSpan elapsed) => Events.Add($"failed:{error.GetType().Name}");
        public void OnRetrying(LlmCallContext call, Exception error, int nextAttempt, TimeSpan delay) => Events.Add($"retry:{nextAttempt}:{(int)delay.TotalSeconds}");
    }

    private static (LlmClient Client, TestHost Host, RecordingObserver Observer, List<TimeSpan> Delays) Build(RetryPolicy? retry = null, LlmOptions? options = null)
    {
        var host = new TestHost().Env("GROQ_API_KEY", "k").Env("ANTHROPIC_API_KEY", "k");
        var delays = new List<TimeSpan>();
        retry ??= new RetryPolicy { MaxAttempts = 3, BaseDelay = TimeSpan.FromSeconds(1), UseJitter = false, Delay = (d, _) => { delays.Add(d); return Task.CompletedTask; } };
        var observer = new RecordingObserver();
        var providers = new ILlmProvider[]
        {
            new OpenAiCompatibleChatProvider(ProviderKeys.Groq, host.Http, host.Credentials),
            new AnthropicMessagesProvider(host.Http, host.Credentials),
        };
        var client = new LlmClient(providers, Options.Create(options ?? host.Options), retry, host.Credentials, new[] { observer });
        return (client, host, observer, delays);
    }

    [Fact]
    public async Task Retries_429_with_retry_after_then_succeeds()
    {
        var (client, host, observer, delays) = Build();
        host.Enqueue(
            CannedResponse.Error(HttpStatusCode.TooManyRequests, """{"error":"rate limited"}""", TimeSpan.FromSeconds(5)),
            CannedResponse.Error(HttpStatusCode.ServiceUnavailable, "overloaded"),
            CannedResponse.Ok(GroqOk));

        var result = await client.ChatAsync(TestHost.Prompt("groq:llama-3.3-70b-versatile", "Hello", "sys"));

        Assert.Equal("done", result.Text);
        Assert.Equal(3, host.Handler.Requests.Count);
        // Retry-After wins for the first retry; the second has no header so it takes the second point on the curve (2 x base).
        Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2) }, delays);
        Assert.Equal(new[] { "started:groq:llama-3.3-70b-versatile:8", "retry:2:5", "retry:3:2", "completed:3" }, observer.Events);
        Assert.Equal(new LlmUsage(2, 1), observer.Outcome!.Usage);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts_and_reports_failure()
    {
        var (client, host, observer, _) = Build(new RetryPolicy { MaxAttempts = 2, Delay = (_, _) => Task.CompletedTask });
        host.Enqueue(
            CannedResponse.Error(HttpStatusCode.BadGateway, "bad"),
            CannedResponse.Error(HttpStatusCode.BadGateway, "bad again"));

        var ex = await Assert.ThrowsAsync<LlmHttpException>(() => client.ChatAsync(TestHost.Prompt("groq:llama-3.3-70b-versatile")));

        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.Equal(2, host.Handler.Requests.Count);
        Assert.Contains("failed:LlmHttpException", observer.Events);
    }

    [Fact]
    public async Task Non_transient_errors_are_not_retried()
    {
        var (client, host, _, _) = Build();
        host.Enqueue(CannedResponse.Error(HttpStatusCode.BadRequest, """{"error":{"message":"bad request"}}"""));

        var ex = await Assert.ThrowsAsync<LlmHttpException>(() => client.ChatAsync(TestHost.Prompt("groq:llama-3.3-70b-versatile")));

        Assert.False(ex.IsTransient);
        Assert.Single(host.Handler.Requests);
    }

    [Fact]
    public async Task Timeout_surfaces_as_LlmTimeoutException_not_cancellation()
    {
        var (client, host, observer, _) = Build();
        host.Enqueue(new CannedResponse(HttpStatusCode.OK, GroqOk, Delay: TimeSpan.FromSeconds(5)));

        var request = TestHost.Prompt("groq:llama-3.3-70b-versatile") with { Timeout = TimeSpan.FromMilliseconds(100) };
        var ex = await Assert.ThrowsAsync<LlmTimeoutException>(() => client.ChatAsync(request));

        Assert.Equal(TimeSpan.FromMilliseconds(100), ex.Timeout);
        Assert.Equal("groq", ex.ProviderKey);
        Assert.Contains("failed:LlmTimeoutException", observer.Events);
        Assert.Single(host.Handler.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_is_an_OperationCanceledException()
    {
        var (client, host, observer, _) = Build();
        host.Enqueue(new CannedResponse(HttpStatusCode.OK, GroqOk, Delay: TimeSpan.FromSeconds(5)));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var request = TestHost.Prompt("groq:llama-3.3-70b-versatile") with { Timeout = TimeSpan.FromSeconds(30) };
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ChatAsync(request, cts.Token));

        Assert.IsNotType<LlmTimeoutException>(ex);
        Assert.Contains("failed:TaskCanceledException", observer.Events);
    }

    [Fact]
    public async Task Stream_retries_before_first_delta_and_maps_idle_timeout()
    {
        var (client, host, observer, delays) = Build();
        var sse = Streams.Sse("""{"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}""", "[DONE]");
        host.Enqueue(CannedResponse.Error(HttpStatusCode.TooManyRequests, "slow"), CannedResponse.Sse(sse));

        var deltas = await Streams.Collect(client.StreamAsync(TestHost.Prompt("groq:llama-3.3-70b-versatile")));
        Assert.Equal("ok", Streams.Text(deltas));
        Assert.Single(delays);
        Assert.Contains("completed:2", observer.Events);

        host.Enqueue(new CannedResponse(HttpStatusCode.OK, sse, "text/event-stream", Delay: TimeSpan.FromSeconds(5)));
        var timeoutRequest = TestHost.Prompt("groq:llama-3.3-70b-versatile") with { Timeout = TimeSpan.FromMilliseconds(100) };
        await Assert.ThrowsAsync<LlmTimeoutException>(async () => await Streams.Collect(client.StreamAsync(timeoutRequest)));
    }

    [Fact]
    public async Task Provider_is_inferred_from_bare_model_ids()
    {
        var (client, host, _, _) = Build();
        host.Enqueue(CannedResponse.Ok("""{"type":"message","content":[{"type":"text","text":"claude"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}"""));

        var result = await client.ChatAsync(ModelRef.Parse("claude-sonnet-4-6"), "hi");

        Assert.Equal("claude", result.Text);
        Assert.Equal("anthropic", result.Resolved.Provider);
        Assert.Same(client.Resolve("claude"), client.Resolve(ModelRef.Parse("claude-opus-4-7")));
        Assert.Throws<LlmConfigurationException>(() => client.Resolve("openai"));
        Assert.Throws<LlmConfigurationException>(() => client.Resolve(ModelRef.Parse("mystery-model")));
    }

    [Fact]
    public void Options_timeout_and_retry_bind_from_configuration_and_DI_resolves_everything()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:TimeoutSeconds"] = "45",
            ["Llm:Retry:MaxAttempts"] = "5",
            ["Llm:OpenAi:ApiKeyVariable"] = "MY_OPENAI_KEY",
            ["Llm:OllamaCloud:Endpoint"] = "https://cloud.example.com",
            ["Llm:OllamaCloud:AuthMode"] = "header",
            ["Llm:OllamaCloud:HeaderName"] = "X-API-Key",
            ["Llm:Anthropic:Models:0"] = "claude-sonnet-4-6",
            ["Llm:AzureOpenAi:Endpoint"] = "https://r.openai.azure.com",
            ["Llm:AzureOpenAi:Deployment"] = "dep",
        }).Build();

        var services = new ServiceCollection();
        services.AddFlexCoreLlm(configuration, llm => llm
            .UseTokenProvider(_ => Task.FromResult("tok"))
            .AddLoggingObserver());
        using var sp = services.BuildServiceProvider();

        var client = sp.GetRequiredService<ILlmClient>();
        Assert.Equal(ProviderKeys.All.OrderBy(k => k, StringComparer.Ordinal), client.Providers.Select(p => p.Key));
        Assert.Equal(5, sp.GetRequiredService<RetryPolicy>().MaxAttempts);
        Assert.Equal(45, sp.GetRequiredService<IOptions<LlmOptions>>().Value.TimeoutSeconds);
        Assert.NotNull(sp.GetRequiredService<ILlmPricing>());
        Assert.NotNull(sp.GetRequiredService<ILlmContextBudget>());
        Assert.IsType<LoggingLlmCallObserver>(Assert.Single(sp.GetServices<ILlmCallObserver>()));

        var credentials = sp.GetRequiredService<ICredentialResolver>();
        var cloud = credentials.Resolve("ollamacloud");
        Assert.Equal("https://cloud.example.com", cloud.Endpoint);
        Assert.Equal(AuthMode.ApiKeyHeader, cloud.AuthMode);
        Assert.Equal("X-API-Key", cloud.HeaderName);
        Assert.Equal("claude-sonnet-4-6", Assert.Single(credentials.Resolve("anthropic").Models));
        Assert.True(client.Resolve("azureopenai").IsConfigured, "token provider makes Azure usable without a key");

        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(LlmProviderBase.HttpClientName("openai"));
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
    }

    [Fact]
    public void Environment_wins_over_configuration()
    {
        var host = new TestHost().Env("OPENAI_API_KEY", "from-env").Env("OPENAI_MODELS", "gpt-5.4, gpt-5.4-mini");
        host.Options.OpenAi.ApiKey = "from-config";
        host.Options.OpenAi.Models.Add("gpt-4.1");
        host.Options.OpenAi.TimeoutSeconds = 9;

        var settings = host.Credentials.Resolve("openai");

        Assert.Equal("from-env", settings.ApiKey);
        Assert.Equal(new[] { "gpt-5.4", "gpt-5.4-mini", "gpt-4.1" }, settings.Models);
        Assert.Equal(TimeSpan.FromSeconds(9), settings.Timeout);
    }
}
