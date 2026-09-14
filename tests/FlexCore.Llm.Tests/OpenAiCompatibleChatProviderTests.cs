using System.Net;
using System.Text.Json;
using Fx.ControlKit.Llm;
using Fx.ControlKit.Llm.Providers;
using Xunit;

namespace FlexCore.Llm.Tests;

public class OpenAiCompatibleChatProviderTests
{
    private const string ChatResponse = """
        {"id":"chatcmpl-1","object":"chat.completion","model":"llama-3.3-70b-versatile",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hi there"},"finish_reason":"length"}],
         "usage":{"prompt_tokens":120,"completion_tokens":7,"prompt_tokens_details":{"cached_tokens":20}}}
        """;

    private static OpenAiCompatibleChatProvider Groq(TestHost host)
        => new(ProviderKeys.Groq, host.Http, host.Credentials);

    [Fact]
    public async Task Chat_sends_chat_completions_shape_with_bearer_auth()
    {
        var host = new TestHost().Env("GROQ_API_KEY", "gsk_test").Enqueue(CannedResponse.Ok(ChatResponse));
        var provider = Groq(host);

        var request = TestHost.Prompt("groq:llama-3.3-70b-versatile", "Hello", "Be brief.") with
        {
            Temperature = 0.2,
            MaxOutputTokens = 256,
            ResponseFormat = ResponseFormat.Json,
            StopSequences = new[] { "END" },
        };
        var result = await provider.ChatAsync(request, CancellationToken.None);

        var sent = host.Handler.Last;
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://api.groq.com/openai/v1/chat/completions", sent.Url.ToString());
        Assert.Equal("Bearer gsk_test", sent.Headers.Authorization!.ToString());

        var json = sent.Json;
        Assert.Equal("llama-3.3-70b-versatile", json.GetProperty("model").GetString());
        var messages = json.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("Be brief.", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("Hello", messages[1].GetProperty("content").GetString());
        Assert.Equal(0.2, json.GetProperty("temperature").GetDouble());
        Assert.Equal(256, json.GetProperty("max_tokens").GetInt32());
        Assert.Equal("json_object", json.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("END", json.GetProperty("stop")[0].GetString());
        Assert.False(json.TryGetProperty("stream", out _));

        Assert.Equal("Hi there", result.Text);
        Assert.Equal(FinishReasons.MaxTokens, result.FinishReason);
        Assert.True(result.IsTruncated);
        Assert.Equal(new LlmUsage(100, 7, 20, null), result.Usage);
        Assert.Equal(new ModelRef("groq", "llama-3.3-70b-versatile"), result.Resolved);
        Assert.NotNull(result.RawJson);
    }

    [Fact]
    public async Task Chat_serialises_tools_images_and_tool_round_trip()
    {
        var host = new TestHost().Env("GROQ_API_KEY", "k").Enqueue(CannedResponse.Ok("""
            {"choices":[{"message":{"role":"assistant","content":null,
              "tool_calls":[{"id":"call_1","type":"function","function":{"name":"lookup","arguments":"{\"q\":\"x\"}"}}]},
              "finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":5}}
            """));
        var provider = Groq(host);
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}""").RootElement;

        var request = new ChatRequest
        {
            Model = "groq:llama-3.3-70b-versatile",
            Messages = new[]
            {
                ChatMessage.User(new TextPart("What is this?"), new ImagePart(new byte[] { 1, 2, 3 }, "image/png")),
                ChatMessage.Assistant(new ToolCallPart(new ToolCall("call_0", "lookup", "{\"q\":\"y\"}"))),
                ChatMessage.ToolResult("call_0", "result-y"),
            },
            Tools = new[] { new ToolDefinition("lookup", "Look something up", schema) },
            ToolChoice = ToolChoice.Function("lookup"),
        };
        var result = await provider.ChatAsync(request, CancellationToken.None);

        var json = host.Handler.Last.Json;
        var messages = json.GetProperty("messages");
        Assert.Equal("text", messages[0].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,AQID", messages[0].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("call_0", messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("call_0", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("function", json.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Equal("lookup", json.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("lookup", json.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString());

        Assert.Equal(FinishReasons.ToolCalls, result.FinishReason);
        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal(new ToolCall("call_1", "lookup", "{\"q\":\"x\"}"), call);
    }

    [Fact]
    public async Task Stream_reads_sse_chunks_usage_and_finish_reason()
    {
        var sse = Streams.Sse(
            """{"choices":[{"delta":{"role":"assistant","content":"Hel"},"finish_reason":null}],"model":"llama-3.3-70b-versatile"}""",
            """{"choices":[{"delta":{"content":"lo"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":9,"completion_tokens":2}}""",
            "[DONE]");
        var host = new TestHost().Env("GROQ_API_KEY", "k").Enqueue(CannedResponse.Sse(sse));
        var provider = Groq(host);

        var deltas = await Streams.Collect(provider.StreamAsync(TestHost.Prompt("groq:llama-3.3-70b-versatile"), CancellationToken.None));

        var json = host.Handler.Last.Json;
        Assert.True(json.GetProperty("stream").GetBoolean());
        Assert.True(json.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());

        Assert.Equal("Hello", Streams.Text(deltas));
        var final = deltas[^1];
        Assert.True(final.IsFinal);
        Assert.Equal(FinishReasons.Stop, final.FinishReason);
        Assert.Equal(new LlmUsage(9, 2), final.Usage);
        Assert.Equal("llama-3.3-70b-versatile", final.Resolved!.Value.Model);
        Assert.All(deltas.SkipLast(1), d => Assert.False(d.IsFinal));
    }

    [Fact]
    public async Task Stream_accumulates_tool_call_fragments()
    {
        var sse = Streams.Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_9","type":"function","function":{"name":"lookup","arguments":""}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"q\":"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"x\"}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            "[DONE]");
        var host = new TestHost().Env("GROQ_API_KEY", "k").Enqueue(CannedResponse.Sse(sse));

        var deltas = await Streams.Collect(Groq(host).StreamAsync(TestHost.Prompt("groq:llama-3.3-70b-versatile"), CancellationToken.None));

        var final = deltas[^1];
        Assert.Equal(FinishReasons.ToolCalls, final.FinishReason);
        Assert.Equal(new ToolCall("call_9", "lookup", "{\"q\":\"x\"}"), Assert.Single(final.ToolCalls!));
    }

    [Fact]
    public async Task Azure_foundry_uses_api_key_header_and_api_version_query()
    {
        var host = new TestHost()
            .Env("AZURE_FOUNDRY_ENDPOINT", "https://my-foundry.eastus.models.ai.azure.com")
            .Env("AZURE_FOUNDRY_API_KEY", "fk")
            .Enqueue(CannedResponse.Ok(ChatResponse));
        var provider = new OpenAiCompatibleChatProvider(ProviderKeys.AzureFoundry, host.Http, host.Credentials);

        await provider.ChatAsync(TestHost.Prompt("azurefoundry:phi-4-mini-instruct"), CancellationToken.None);

        var sent = host.Handler.Last;
        Assert.Equal("https://my-foundry.eastus.models.ai.azure.com/models/chat/completions?api-version=2024-05-01-preview", sent.Url.ToString());
        Assert.Equal("fk", sent.Header("api-key"));
        Assert.Null(sent.Headers.Authorization);
    }

    [Fact]
    public async Task Huggingface_honours_HF_TOKEN_and_router_default()
    {
        var host = new TestHost().Env("HF_TOKEN", "hf_x").Enqueue(CannedResponse.Ok(ChatResponse));
        var provider = new OpenAiCompatibleChatProvider(ProviderKeys.HuggingFace, host.Http, host.Credentials);

        await provider.ChatAsync(TestHost.Prompt("huggingface:Qwen/Qwen3-32B:nscale"), CancellationToken.None);

        Assert.Equal("https://router.huggingface.co/v1/chat/completions", host.Handler.Last.Url.ToString());
        Assert.Equal("Bearer hf_x", host.Handler.Last.Headers.Authorization!.ToString());
        Assert.Equal("Qwen/Qwen3-32B:nscale", host.Handler.Last.Json.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ListModels_hits_models_endpoint()
    {
        var host = new TestHost().Env("XAI_API_KEY", "x").Enqueue(CannedResponse.Ok("""{"data":[{"id":"grok-4","created":1700000000,"owned_by":"xai"}]}"""));
        var provider = new OpenAiCompatibleChatProvider(ProviderKeys.XAi, host.Http, host.Credentials);

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(HttpMethod.Get, host.Handler.Last.Method);
        Assert.Equal("https://api.x.ai/v1/models", host.Handler.Last.Url.ToString());
        var model = Assert.Single(models);
        Assert.Equal("xai:grok-4", model.ToString());
        Assert.Equal("xai", model.OwnedBy);
    }

    [Fact]
    public async Task Non_success_status_maps_to_LlmHttpException_with_retry_after()
    {
        var host = new TestHost().Env("GROQ_API_KEY", "k")
            .Enqueue(CannedResponse.Error(HttpStatusCode.TooManyRequests, """{"error":{"message":"slow down"}}""", TimeSpan.FromSeconds(7)));

        var ex = await Assert.ThrowsAsync<LlmHttpException>(() => Groq(host).ChatAsync(TestHost.Prompt("groq:llama-3.3-70b-versatile"), CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
        Assert.True(ex.IsTransient);
        Assert.Contains("slow down", ex.Message);
    }

    [Fact]
    public void Missing_key_reports_the_environment_variables()
    {
        var host = new TestHost();
        var provider = Groq(host);

        Assert.False(provider.IsConfigured);
        var ex = Assert.ThrowsAsync<LlmConfigurationException>(() => provider.ChatAsync(TestHost.Prompt("groq:llama-3.3-70b-versatile"), CancellationToken.None)).Result;
        Assert.Contains("GROQ_API_KEY", ex.Message);
    }

    [Fact]
    public async Task Unsupported_capability_throws_NotSupported()
    {
        var host = new TestHost().Env("GROQ_API_KEY", "k");
        await Assert.ThrowsAsync<NotSupportedException>(() => Groq(host).GenerateImageAsync(new ImageRequest { Model = "groq:x", Prompt = "p" }, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => Groq(host).EmbedAsync(EmbeddingRequest.For("groq:x", "a"), CancellationToken.None));
    }
}
