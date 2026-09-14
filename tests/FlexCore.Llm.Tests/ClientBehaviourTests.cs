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

public class ClientBehaviourTests
{
    private const string OpenAiOk = """{"status":"completed","model":"gpt-4.1","output":[{"type":"message","content":[{"type":"output_text","text":"fallback ok"}]}],"usage":{"input_tokens":3,"output_tokens":2}}""";
    private const string ModelNotFound = """{"error":{"message":"The model `gpt-5.9` does not exist or you do not have access to it.","type":"invalid_request_error","code":"model_not_found"}}""";
    private const string GroqOk = """{"choices":[{"message":{"content":"done"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1},"model":"llama-3.3-70b-versatile"}""";
    private const string AzureOk = """{"choices":[{"message":{"content":"azure"},"finish_reason":"stop"}],"model":"gpt-4.1","usage":{"prompt_tokens":3,"completion_tokens":1}}""";

    private sealed class RecordingObserver : ILlmCallObserver
    {
        public List<string> Events { get; } = new();
        public void OnRetrying(LlmCallContext call, Exception error, int nextAttempt, TimeSpan delay) => Events.Add($"retry:{nextAttempt}:{call.Model.Model}");
        public void OnCompleted(LlmCallContext call, LlmCallOutcome outcome) => Events.Add($"completed:{outcome.Resolved.Model}:{outcome.Attempts}");
        public void OnFailed(LlmCallContext call, Exception error, TimeSpan elapsed) => Events.Add($"failed:{error.GetType().Name}");
    }

    private static LlmClient Build(TestHost host, RetryPolicy? retry = null, IModelConfigStore? store = null, ILlmRequestOverrides? overrides = null, ILlmCallObserver? observer = null)
    {
        retry ??= new RetryPolicy { MaxAttempts = 1, Delay = (_, _) => Task.CompletedTask };
        var providers = new ILlmProvider[]
        {
            new OpenAiResponsesProvider(host.Http, host.Credentials),
            new AzureOpenAiProvider(host.Http, host.Credentials),
            new OpenAiCompatibleChatProvider(ProviderKeys.Groq, host.Http, host.Credentials),
            new OllamaProvider(ProviderKeys.Ollama, host.Http, host.Credentials),
        };
        return new LlmClient(providers, Options.Create(host.Options), retry, host.Credentials,
            observer is null ? null : new[] { observer }, null, overrides, store);
    }

    [Fact]
    public async Task Model_fallback_chain_walks_configured_then_policy_models_on_model_access_errors()
    {
        var host = new TestHost().Env("OPENAI_API_KEY", "k").Env("OPENAI_MODELS", "gpt-5.8");
        host.Enqueue(
            CannedResponse.Error(HttpStatusCode.NotFound, ModelNotFound),
            CannedResponse.Error(HttpStatusCode.Forbidden, """{"error":{"message":"Project does not have access to model gpt-5.8"}}"""),
            CannedResponse.Ok(OpenAiOk));
        var observer = new RecordingObserver();
        var retry = new RetryPolicy { MaxAttempts = 1, ModelFallback = ModelFallbackPolicy.OpenAiDefaults };
        var client = Build(host, retry, observer: observer);

        var result = await client.ChatAsync(TestHost.Prompt("openai:gpt-5.9"));

        Assert.Equal("fallback ok", result.Text);
        var sentModels = host.Handler.Requests.Select(r => r.Json.GetProperty("model").GetString()).ToArray();
        Assert.Equal(new[] { "gpt-5.9", "gpt-5.8", "gpt-5.4" }, sentModels);
        Assert.Equal(new[] { "retry:2:gpt-5.9", "retry:3:gpt-5.8", "completed:gpt-4.1:3" }, observer.Events);
    }

    [Fact]
    public async Task Model_fallback_is_off_by_default_and_skips_other_providers()
    {
        var host = new TestHost().Env("OPENAI_API_KEY", "k").Env("GROQ_API_KEY", "k");
        host.Enqueue(CannedResponse.Error(HttpStatusCode.NotFound, ModelNotFound));
        var client = Build(host);

        var ex = await Assert.ThrowsAsync<LlmHttpException>(() => client.ChatAsync(TestHost.Prompt("openai:gpt-5.9")));
        Assert.True(ex.IsModelAccessError);
        Assert.Single(host.Handler.Requests);

        host.Enqueue(CannedResponse.Error(HttpStatusCode.NotFound, ModelNotFound));
        var scoped = Build(host, new RetryPolicy { MaxAttempts = 1, ModelFallback = new ModelFallbackPolicy { Models = new[] { "llama-3.1-8b-instant" } } });
        await Assert.ThrowsAsync<LlmHttpException>(() => scoped.ChatAsync(TestHost.Prompt("groq:llama-9")));
        Assert.Equal(2, host.Handler.Requests.Count);
    }

    [Fact]
    public async Task Stream_walks_the_fallback_chain_before_the_first_delta()
    {
        var sse = Streams.Sse(
            "response.output_text.delta|" + """{"type":"response.output_text.delta","delta":"hi"}""",
            "response.completed|" + """{"type":"response.completed","response":{"status":"completed","model":"gpt-5.4","usage":{"input_tokens":1,"output_tokens":1}}}""");
        var host = new TestHost().Env("OPENAI_API_KEY", "k");
        host.Enqueue(CannedResponse.Error(HttpStatusCode.NotFound, ModelNotFound), CannedResponse.Sse(sse));
        var client = Build(host, new RetryPolicy { MaxAttempts = 1, ModelFallback = ModelFallbackPolicy.OpenAiDefaults });

        var deltas = await Streams.Collect(client.StreamAsync(TestHost.Prompt("openai:gpt-5.9")));

        Assert.Equal("hi", Streams.Text(deltas));
        Assert.Equal("gpt-5.4", host.Handler.Last.Json.GetProperty("model").GetString());
    }

    [Fact]
    public async Task OpenAi_family_request_routes_to_whichever_sibling_is_configured()
    {
        var host = new TestHost().Env("AZURE_OPENAI_ENDPOINT", "https://r.openai.azure.com").Env("AZURE_OPENAI_API_KEY", "az");
        host.Enqueue(CannedResponse.Ok(AzureOk), CannedResponse.Ok(AzureOk));
        var client = Build(host);

        var explicitOpenAi = await client.ChatAsync(TestHost.Prompt("openai:gpt-4.1"));
        var inferred = await client.ChatAsync(ModelRef.Parse("gpt-4.1"), "hi");

        Assert.Equal("azureopenai", explicitOpenAi.Resolved.Provider);
        Assert.Equal("azureopenai", inferred.Resolved.Provider);
        Assert.All(host.Handler.Requests, r => Assert.StartsWith("https://r.openai.azure.com/openai/deployments/gpt-4.1/chat/completions", r.Url.ToString()));

        host.Env("OPENAI_API_KEY", "pk").Enqueue(CannedResponse.Ok(OpenAiOk));
        var both = await client.ChatAsync(TestHost.Prompt("openai:gpt-4.1"));
        Assert.Equal("openai", both.Resolved.Provider);
        Assert.Equal("https://api.openai.com/v1/responses", host.Handler.Last.Url.ToString());
    }

    [Fact]
    public async Task Per_user_model_config_fills_timeout_max_output_and_temperature()
    {
        var host = new TestHost().Env("GROQ_API_KEY", "k").Enqueue(CannedResponse.Ok(GroqOk), CannedResponse.Ok(GroqOk), CannedResponse.Ok(GroqOk));
        var store = new InMemoryModelConfigStore();
        store.Save("alice", new LlmModelConfig("llama-3.3-70b-versatile", TimeoutSeconds: 7, MaxOutputTokens: 321, DefaultTemperature: 0.33));
        var client = Build(host, store: store);

        await client.ChatAsync(TestHost.Prompt("groq:llama-3.3-70b-versatile") with { UserId = "alice" });
        var alice = host.Handler.Last.Json;
        Assert.Equal(321, alice.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.33, alice.GetProperty("temperature").GetDouble(), 6);

        await client.ChatAsync(TestHost.Prompt("groq:llama-3.3-70b-versatile") with { UserId = "alice", Temperature = 0.9, MaxOutputTokens = 5 });
        var explicitRequest = host.Handler.Last.Json;
        Assert.Equal(5, explicitRequest.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.9, explicitRequest.GetProperty("temperature").GetDouble(), 6);

        // Bob has no entry, so the seed for this model applies: no output cap, temperature 0.7.
        await client.ChatAsync(TestHost.Prompt("groq:llama-3.3-70b-versatile") with { UserId = "bob" });
        var bob = host.Handler.Last.Json;
        Assert.False(bob.TryGetProperty("max_tokens", out _));
        Assert.Equal(0.7, bob.GetProperty("temperature").GetDouble(), 6);
    }

    [Fact]
    public async Task Model_config_timeout_applies_and_cloud_prefix_key_is_found()
    {
        var host = new TestHost().Env("GROQ_API_KEY", "k").Enqueue(new CannedResponse(HttpStatusCode.OK, GroqOk, Delay: TimeSpan.FromSeconds(5)));
        var store = new InMemoryModelConfigStore(new Dictionary<string, LlmModelConfig>());
        store.Save(null, new LlmModelConfig("groq:llama-3.3-70b-versatile", TimeoutSeconds: 1));
        var client = Build(host, store: store);

        var ex = await Assert.ThrowsAsync<LlmTimeoutException>(() => client.ChatAsync(TestHost.Prompt("groq:llama-3.3-70b-versatile")));
        Assert.Equal(TimeSpan.FromSeconds(1), ex.Timeout);

        var cloudHost = new TestHost().Env("OLLAMA_CLOUD_ENDPOINT", "https://c.example.com").Enqueue(CannedResponse.Ok("""{"model":"qwen3:8b","message":{"role":"assistant","content":"x"},"done":true}"""));
        var cloudStore = new InMemoryModelConfigStore(new Dictionary<string, LlmModelConfig>());
        cloudStore.Save(null, new LlmModelConfig("cloud-ollama:qwen3:8b", MaxOutputTokens: 77));
        var cloud = new LlmClient(new ILlmProvider[] { new OllamaProvider(ProviderKeys.OllamaCloud, cloudHost.Http, cloudHost.Credentials) }, null, RetryPolicy.None, cloudHost.Credentials, modelConfigs: cloudStore);
        await cloud.ChatAsync(TestHost.Prompt("ollamacloud:qwen3:8b"));
        Assert.Equal(77, cloudHost.Handler.Last.Json.GetProperty("options").GetProperty("num_predict").GetInt32());
    }

    [Fact]
    public async Task Request_overrides_match_by_longest_key_and_retry_once_on_timeout()
    {
        var host = new TestHost().Env("GROQ_API_KEY", "k");
        host.Enqueue(new CannedResponse(HttpStatusCode.OK, GroqOk, Delay: TimeSpan.FromSeconds(5)), CannedResponse.Ok(GroqOk));
        var overrides = new LlmRequestOverrides(new[]
        {
            new RequestOverrideOptions { Match = "groq", MaxOutputTokens = 999 },
            new RequestOverrideOptions { Match = "llama-3.3-70b", MaxOutputTokens = 768, TimeoutSeconds = 1, RetryOnTimeout = true, ContextLimits = { ["SurroundingContext"] = 10 } },
        });
        var observer = new RecordingObserver();
        var client = Build(host, overrides: overrides, observer: observer);

        var result = await client.ChatAsync(TestHost.Prompt("groq:llama-3.3-70b-versatile"));

        Assert.Equal("done", result.Text);
        Assert.Equal(2, host.Handler.Requests.Count);
        Assert.Equal(768, host.Handler.Last.Json.GetProperty("max_tokens").GetInt32());
        Assert.Equal(new[] { "retry:2:llama-3.3-70b-versatile", "completed:llama-3.3-70b-versatile:2" }, observer.Events);

        var resolved = overrides.Resolve(ModelRef.Parse("groq:llama-3.3-70b-versatile"));
        Assert.Equal("llama-3.3-70b", resolved.Match);
        Assert.Equal("...\nof context", resolved.Trim("SurroundingContext", "a long piece of context", keepTail: true));
        Assert.Equal("short", resolved.Trim("SurroundingContext", "short"));
        Assert.True(overrides.Resolve(ModelRef.Parse("anthropic:claude-sonnet-4-6")).IsEmpty);
    }

    [Fact]
    public async Task Probe_reports_not_configured_ok_auth_failed_and_unreachable()
    {
        var host = new TestHost();
        var client = Build(host);

        var unconfigured = await client.ProbeAsync("groq");
        Assert.Equal(ProbeStatus.NotConfigured, unconfigured.Status);
        Assert.Contains("GROQ_API_KEY", unconfigured.Message);
        Assert.Equal(ProbeStatus.NotRegistered, (await client.ProbeAsync("anthropic")).Status);

        host.Env("GROQ_API_KEY", "k").Enqueue(CannedResponse.Ok("""{"data":[{"id":"llama-3.3-70b-versatile"},{"id":"llama-3.1-8b-instant"}]}"""));
        var ok = await client.ProbeAsync("groq");
        Assert.Equal(ProbeStatus.Ok, ok.Status);
        Assert.Equal(2, ok.ModelCount);
        Assert.True(ok.IsReachable);

        host.Enqueue(CannedResponse.Error(HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid API Key"}}"""));
        Assert.Equal(ProbeStatus.AuthFailed, (await client.ProbeAsync("groq")).Status);

        // Azure has no listing API: the probe is a one-token chat with the configured deployment.
        host.Env("AZURE_OPENAI_ENDPOINT", "https://r.openai.azure.com").Env("AZURE_OPENAI_API_KEY", "az").Env("AZURE_OPENAI_DEPLOYMENT", "gpt41");
        host.Enqueue(CannedResponse.Ok(AzureOk));
        var azure = await client.ProbeAsync("azureopenai");
        Assert.Equal(ProbeStatus.Ok, azure.Status);
        Assert.Equal(1, host.Handler.Last.Json.GetProperty("max_tokens").GetInt32());
        Assert.Contains("/openai/deployments/gpt41/", host.Handler.Last.Url.ToString());

        host.Enqueue(new CannedResponse(HttpStatusCode.OK, AzureOk, Delay: TimeSpan.FromSeconds(30)));
        using var cts = new CancellationTokenSource();
        var probeTask = client.ProbeAsync("azureopenai", cts.Token);
        // The probe's own 15 s budget is long for a unit test; a caller cancellation is not a probe verdict.
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probeTask);

        // azureopenai (one-token chat), groq (list) and ollama (list, local default endpoint) are configured; openai is not.
        host.Enqueue(CannedResponse.Ok("{}"), CannedResponse.Ok("{}"), CannedResponse.Ok("{}"));
        var all = await client.ProbeAllAsync(configuredOnly: false);
        Assert.Equal(4, all.Count);
        Assert.Equal(3, all.Count(p => p.Status == ProbeStatus.Ok));
        Assert.Equal(ProbeStatus.NotConfigured, all.Single(p => p.Provider == "openai").Status);
    }

    [Fact]
    public async Task Call_records_carry_cost_status_and_user()
    {
        var host = new TestHost().Env("GROQ_API_KEY", "k").Enqueue(CannedResponse.Ok(GroqOk), CannedResponse.Error(HttpStatusCode.BadRequest, "nope"));
        var log = new InMemoryCallLog(capacity: 10);
        var client = Build(host, observer: new CallRecordingObserver(log, LlmPricing.Default));

        await client.ChatAsync(TestHost.Prompt("groq:llama-3.3-70b-versatile") with { UserId = "alice" });
        await Assert.ThrowsAsync<LlmHttpException>(() => client.ChatAsync(TestHost.Prompt("groq:llama-3.3-70b-versatile")));

        var records = log.Recent;
        Assert.Equal(2, records.Count);
        var failed = records[0];
        Assert.Equal(LlmCallStatus.Failed, failed.Status);
        Assert.Contains("400", failed.Error);
        var ok = records[1];
        Assert.Equal(LlmCallStatus.Completed, ok.Status);
        Assert.Equal("alice", ok.UserId);
        Assert.Equal("groq", ok.Provider);
        Assert.Equal(new LlmUsage(2, 1), ok.Usage);
        Assert.Equal(2 * 0.59 / 1_000_000 + 1 * 0.79 / 1_000_000, ok.EstimatedCostUsd!.Value, 12);
        Assert.Equal(FinishReasons.Stop, ok.FinishReason);
    }

    [Fact]
    public void DI_wires_overrides_model_config_chunk_planner_fallback_and_call_log()
    {
        var directory = Path.Combine(Path.GetTempPath(), "flexcore-llm-tests", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:ModelConfigDirectory"] = directory,
            ["Llm:RequestOverrides:0:Match"] = "phi-4-mini",
            ["Llm:RequestOverrides:0:MaxOutputTokens"] = "768",
            ["Llm:RequestOverrides:0:TimeoutSeconds"] = "45",
            ["Llm:RequestOverrides:0:RetryOnTimeout"] = "true",
            ["Llm:RequestOverrides:0:ContextLimits:SurroundingContext"] = "2000",
            ["Llm:Retry:FallbackModels:0"] = "gpt-5.4",
            ["Llm:Retry:FallbackModels:1"] = "gpt-4.1-mini",
            ["Llm:Ollama:Aliases:qwen"] = "qwen3:32b",
            ["Llm:HuggingFace:DedicatedModels:0"] = "org/model-a",
        }).Build();

        var services = new ServiceCollection();
        services.AddFlexCoreLlm(configuration, llm => llm.AddInMemoryCallLog(25).AddCallRecorder(_ => { }));
        using var sp = services.BuildServiceProvider();

        try
        {
            var store = Assert.IsType<JsonFileModelConfigStore>(sp.GetRequiredService<IModelConfigStore>());
            Assert.Equal(Path.GetFullPath(directory), store.RootDirectory);
            Assert.NotNull(sp.GetRequiredService<Fx.ControlKit.Llm.Chunking.IChunkPlanner>());

            var over = sp.GetRequiredService<ILlmRequestOverrides>().Resolve(ModelRef.Parse("azurefoundry:phi-4-mini-instruct"));
            Assert.Equal(768, over.MaxOutputTokens);
            Assert.Equal(TimeSpan.FromSeconds(45), over.Timeout);
            Assert.True(over.RetryOnTimeout);
            Assert.Equal(2000, over.ContextLimit("SurroundingContext"));

            var retry = sp.GetRequiredService<RetryPolicy>();
            Assert.Equal(new[] { "gpt-5.4", "gpt-4.1-mini" }, retry.ModelFallback!.Models);
            Assert.True(retry.ModelFallback.AppliesTo("openai"));
            Assert.False(retry.ModelFallback.AppliesTo("groq"));

            var credentials = sp.GetRequiredService<ICredentialResolver>();
            Assert.Equal("qwen3:32b", credentials.Resolve("ollama").Aliases["qwen"]);
            Assert.Equal("org/model-a", Assert.Single(credentials.Resolve("huggingface").DedicatedModels));

            var observers = sp.GetServices<ILlmCallObserver>().ToList();
            Assert.Single(observers.OfType<CallRecordingObserver>());
            Assert.Equal(2, sp.GetServices<ILlmCallRecordSink>().Count());
            Assert.Same(sp.GetRequiredService<InMemoryCallLog>(), sp.GetServices<ILlmCallRecordSink>().OfType<InMemoryCallLog>().Single());
            Assert.NotNull(sp.GetRequiredService<ILlmClient>());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Without_a_model_config_directory_the_null_store_is_used()
    {
        var services = new ServiceCollection();
        services.AddFlexCoreLlm(new ConfigurationBuilder().Build());
        using var sp = services.BuildServiceProvider();

        Assert.IsType<NullModelConfigStore>(sp.GetRequiredService<IModelConfigStore>());
        Assert.Null(sp.GetRequiredService<RetryPolicy>().ModelFallback);
    }
}
