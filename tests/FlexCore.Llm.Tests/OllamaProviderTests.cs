using Fx.ControlKit.Llm;
using Fx.ControlKit.Llm.Providers;
using Xunit;

namespace FlexCore.Llm.Tests;

public class OllamaProviderTests
{
    private const string ChatResponse = """
        {"model":"qwen2.5-coder:32b","created_at":"2026-09-14T00:00:00Z","message":{"role":"assistant","content":"local hi"},
         "done":true,"done_reason":"length","total_duration":1,"prompt_eval_count":33,"eval_count":11}
        """;

    [Fact]
    public async Task Chat_posts_api_chat_with_options_and_no_auth_by_default()
    {
        var host = new TestHost().Enqueue(CannedResponse.Ok(ChatResponse));
        var provider = new OllamaProvider(ProviderKeys.Ollama, host.Http, host.Credentials);

        Assert.True(provider.IsConfigured);
        var request = TestHost.Prompt("ollama:qwen2.5-coder:32b", "Hello", "Be brief.") with
        {
            Temperature = 0.1,
            MaxOutputTokens = 200,
            ResponseFormat = ResponseFormat.Json,
            Extras = new Dictionary<string, object?> { ["num_ctx"] = 16384, ["keep_alive"] = "10m" },
        };
        var result = await provider.ChatAsync(request, CancellationToken.None);

        var sent = host.Handler.Last;
        Assert.Equal("http://localhost:11434/api/chat", sent.Url.ToString());
        Assert.Null(sent.Headers.Authorization);

        var json = sent.Json;
        Assert.Equal("qwen2.5-coder:32b", json.GetProperty("model").GetString());
        Assert.False(json.GetProperty("stream").GetBoolean());
        Assert.Equal("json", json.GetProperty("format").GetString());
        Assert.Equal("10m", json.GetProperty("keep_alive").GetString());
        var options = json.GetProperty("options");
        Assert.Equal(0.1, options.GetProperty("temperature").GetDouble());
        Assert.Equal(200, options.GetProperty("num_predict").GetInt32());
        Assert.Equal(16384, options.GetProperty("num_ctx").GetInt32());
        var messages = json.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("Hello", messages[1].GetProperty("content").GetString());

        Assert.Equal("local hi", result.Text);
        Assert.Equal(FinishReasons.MaxTokens, result.FinishReason);
        Assert.Equal(new LlmUsage(33, 11), result.Usage);
        Assert.Equal(new ModelRef("ollama", "qwen2.5-coder:32b"), result.Resolved);
    }

    [Fact]
    public async Task Bare_tag_parses_without_a_provider_and_cloud_prefix_is_stripped()
    {
        var parsed = ModelRef.Parse("qwen2.5-coder:32b");
        Assert.Equal(string.Empty, parsed.Provider);
        Assert.Equal("qwen2.5-coder:32b", parsed.Model);

        var cloud = ModelRef.Parse("cloud-ollama:qwen2.5-coder:32b");
        Assert.Equal(new ModelRef("ollamacloud", "qwen2.5-coder:32b"), cloud);

        var host = new TestHost().Env("OLLAMA_CLOUD_ENDPOINT", "https://ollama.example.com/api/chat").Enqueue(CannedResponse.Ok(ChatResponse));
        var provider = new OllamaProvider(ProviderKeys.OllamaCloud, host.Http, host.Credentials);
        await provider.ChatAsync(TestHost.Prompt("cloud-ollama:qwen2.5-coder:32b"), CancellationToken.None);

        Assert.Equal("https://ollama.example.com/api/chat", host.Handler.Last.Url.ToString());
        Assert.Equal("qwen2.5-coder:32b", host.Handler.Last.Json.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Stream_reads_ndjson_lines_until_done()
    {
        var ndjson = string.Join("\n",
            """{"model":"qwen3:8b","message":{"role":"assistant","content":"Hel"},"done":false}""",
            """{"model":"qwen3:8b","message":{"role":"assistant","content":"lo"},"done":false}""",
            """{"model":"qwen3:8b","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","prompt_eval_count":7,"eval_count":2}""",
            "");
        var host = new TestHost().Enqueue(CannedResponse.Ndjson(ndjson));
        var provider = new OllamaProvider(ProviderKeys.Ollama, host.Http, host.Credentials);

        var deltas = await Streams.Collect(provider.StreamAsync(TestHost.Prompt("ollama:qwen3:8b"), CancellationToken.None));

        Assert.True(host.Handler.Last.Json.GetProperty("stream").GetBoolean());
        Assert.Equal("Hello", Streams.Text(deltas));
        var final = deltas[^1];
        Assert.True(final.IsFinal);
        Assert.Equal(FinishReasons.Stop, final.FinishReason);
        Assert.Equal(new LlmUsage(7, 2), final.Usage);
    }

    [Fact]
    public async Task Tools_and_json_schema_serialise_natively()
    {
        var host = new TestHost().Enqueue(CannedResponse.Ok("""
            {"model":"qwen3:8b","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"lookup","arguments":{"q":"x"}}}]},"done":true,"done_reason":"stop","prompt_eval_count":1,"eval_count":1}
            """));
        var schema = System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}""").RootElement;
        var provider = new OllamaProvider(ProviderKeys.Ollama, host.Http, host.Credentials);

        var result = await provider.ChatAsync(TestHost.Prompt("ollama:qwen3:8b") with
        {
            Tools = new[] { new ToolDefinition("lookup", "d", schema) },
            ResponseFormat = ResponseFormat.JsonSchema,
            Schema = schema,
            Reasoning = new ReasoningOptions(Enabled: false),
        }, CancellationToken.None);

        var json = host.Handler.Last.Json;
        Assert.Equal("object", json.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("lookup", json.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.False(json.GetProperty("think").GetBoolean());

        Assert.Equal(FinishReasons.ToolCalls, result.FinishReason);
        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("lookup", call.Name);
        Assert.Equal("{\"q\":\"x\"}", call.ArgumentsJson);
    }

    [Fact]
    public async Task Local_and_cloud_instances_have_independent_endpoint_and_auth()
    {
        var host = new TestHost()
            .Env("OLLAMA_HOST", "http://gpu-box:11434")
            .Env("OLLAMA_CLOUD_ENDPOINT", "https://cloud.example.com")
            .Env("OLLAMA_CLOUD_API_KEY", "cloud-secret")
            .Enqueue(CannedResponse.Ok("""{"models":[{"name":"qwen3:8b","model":"qwen3:8b","size":123,"details":{"family":"qwen3","parameter_size":"8B"}}]}"""))
            .Enqueue(CannedResponse.Ok("""{"models":[{"name":"deepseek-v4-pro:cloud"}]}"""));
        var local = new OllamaProvider(ProviderKeys.Ollama, host.Http, host.Credentials);
        var cloud = new OllamaProvider(ProviderKeys.OllamaCloud, host.Http, host.Credentials);

        var localModels = await local.ListModelsAsync(CancellationToken.None);
        var localRequest = host.Handler.Last;
        var cloudModels = await cloud.ListModelsAsync(CancellationToken.None);
        var cloudRequest = host.Handler.Last;

        Assert.Equal("http://gpu-box:11434/api/tags", localRequest.Url.ToString());
        Assert.Null(localRequest.Headers.Authorization);
        Assert.Equal("https://cloud.example.com/api/tags", cloudRequest.Url.ToString());
        Assert.Equal("Bearer cloud-secret", cloudRequest.Headers.Authorization!.ToString());

        var model = Assert.Single(localModels);
        Assert.Equal("ollama:qwen3:8b", model.ToString());
        Assert.Equal("8B", model.Metadata!["parameter_size"]);
        Assert.Equal(32_000, model.ContextTokens);
        Assert.Equal("ollamacloud", Assert.Single(cloudModels).Provider);
    }

    [Fact]
    public async Task Header_and_basic_auth_modes_follow_configuration()
    {
        var host = new TestHost()
            .Env("OLLAMA_AUTH_MODE", "header").Env("OLLAMA_API_KEY", "hk").Env("OLLAMA_AUTH_HEADER", "X-Custom-Key")
            .Enqueue(CannedResponse.Ok(ChatResponse), CannedResponse.Ok(ChatResponse));
        var provider = new OllamaProvider(ProviderKeys.Ollama, host.Http, host.Credentials);
        await provider.ChatAsync(TestHost.Prompt("ollama:qwen3:8b"), CancellationToken.None);
        Assert.Equal("hk", host.Handler.Last.Header("X-Custom-Key"));

        host.Environment.Clear();
        host.Env("OLLAMA_AUTH_MODE", "basic").Env("OLLAMA_USERNAME", "u").Env("OLLAMA_PASSWORD", "p");
        await provider.ChatAsync(TestHost.Prompt("ollama:qwen3:8b"), CancellationToken.None);
        Assert.Equal("Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("u:p")), host.Handler.Last.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Embeddings_post_to_api_embed()
    {
        var host = new TestHost().Enqueue(CannedResponse.Ok("""{"model":"nomic-embed-text","embeddings":[[0.5,0.5]],"prompt_eval_count":3}"""));
        var provider = new OllamaProvider(ProviderKeys.Ollama, host.Http, host.Credentials);

        var result = await provider.EmbedAsync(EmbeddingRequest.For("ollama:nomic-embed-text", "hello"), CancellationToken.None);

        Assert.Equal("http://localhost:11434/api/embed", host.Handler.Last.Url.ToString());
        Assert.Equal("hello", host.Handler.Last.Json.GetProperty("input")[0].GetString());
        Assert.Equal(new[] { 0.5f, 0.5f }, Assert.Single(result.Vectors));
        Assert.Equal(3, result.Usage!.Value.Input);
    }
}
