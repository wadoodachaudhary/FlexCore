using System.Text.Json;
using Fx.ControlKit.Llm;
using Fx.ControlKit.Llm.Providers;
using Xunit;

namespace FlexCore.Llm.Tests;

public class AnthropicMessagesProviderTests
{
    private const string MessageResponse = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-6",
         "content":[{"type":"text","text":"Hello from Claude"}],
         "stop_reason":"max_tokens","stop_sequence":null,
         "usage":{"input_tokens":25,"output_tokens":9,"cache_creation_input_tokens":100,"cache_read_input_tokens":40}}
        """;

    private static AnthropicMessagesProvider Provider(TestHost host) => new(host.Http, host.Credentials);

    [Fact]
    public async Task Chat_sends_messages_shape_with_headers_and_top_level_system()
    {
        var host = new TestHost().Env("ANTHROPIC_API_KEY", "sk-ant").Enqueue(CannedResponse.Ok(MessageResponse));

        var request = TestHost.Prompt("anthropic:claude-sonnet-4-6", "Hello", "Be brief.") with { MaxOutputTokens = 512, Temperature = 0.1 };
        var result = await Provider(host).ChatAsync(request, CancellationToken.None);

        var sent = host.Handler.Last;
        Assert.Equal("https://api.anthropic.com/v1/messages", sent.Url.ToString());
        Assert.Equal("sk-ant", sent.Header("x-api-key"));
        Assert.Equal("2023-06-01", sent.Header("anthropic-version"));
        Assert.Null(sent.Headers.Authorization);

        var json = sent.Json;
        Assert.Equal("claude-sonnet-4-6", json.GetProperty("model").GetString());
        Assert.Equal(512, json.GetProperty("max_tokens").GetInt32());
        Assert.Equal("Be brief.", json.GetProperty("system").GetString());
        Assert.Equal(0.1, json.GetProperty("temperature").GetDouble());
        var messages = json.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("Hello", messages[0].GetProperty("content").GetString());
        Assert.False(json.TryGetProperty("stream", out _));

        Assert.Equal("Hello from Claude", result.Text);
        Assert.Equal(FinishReasons.MaxTokens, result.FinishReason);
        Assert.True(result.IsTruncated);
        Assert.Equal(new LlmUsage(25, 9, 40, 100), result.Usage);
    }

    [Fact]
    public async Task Claude_alias_and_CLAUDE_API_KEY_are_honoured()
    {
        var host = new TestHost().Env("CLAUDE_API_KEY", "legacy").Enqueue(CannedResponse.Ok(MessageResponse));

        var result = await Provider(host).ChatAsync(TestHost.Prompt("claude:claude-haiku-4-5"), CancellationToken.None);

        Assert.Equal("legacy", host.Handler.Last.Header("x-api-key"));
        Assert.Equal("claude-haiku-4-5", host.Handler.Last.Json.GetProperty("model").GetString());
        Assert.Equal(4096, host.Handler.Last.Json.GetProperty("max_tokens").GetInt32());
        Assert.Equal("anthropic", result.Resolved.Provider);
    }

    [Fact]
    public async Task Tools_images_thinking_and_cache_control_serialise_natively()
    {
        var host = new TestHost().Env("ANTHROPIC_API_KEY", "k").Enqueue(CannedResponse.Ok("""
            {"type":"message","content":[{"type":"text","text":"Let me check."},{"type":"tool_use","id":"toolu_1","name":"lookup","input":{"q":"x"}}],
             "stop_reason":"tool_use","usage":{"input_tokens":1,"output_tokens":1}}
            """));
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}""").RootElement;

        var request = new ChatRequest
        {
            Model = "anthropic:claude-sonnet-4-6",
            System = "sys",
            Messages = new[]
            {
                ChatMessage.User(new TextPart("Look"), new ImagePart(new byte[] { 1, 2, 3 }, "image/jpeg")),
                ChatMessage.Assistant(new ToolCallPart(new ToolCall("toolu_0", "lookup", "{\"q\":\"y\"}"))),
                ChatMessage.ToolResult("toolu_0", "found y"),
            },
            Tools = new[] { new ToolDefinition("lookup", "desc", schema) },
            ToolChoice = ToolChoice.Required,
            Reasoning = new ReasoningOptions(Enabled: true, BudgetTokens: 2048),
            MaxOutputTokens = 8000,
            Temperature = 0.9,
            Extras = new Dictionary<string, object?> { ["cache_control"] = true },
        };
        var result = await Provider(host).ChatAsync(request, CancellationToken.None);

        var json = host.Handler.Last.Json;
        Assert.Equal("ephemeral", json.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.False(json.TryGetProperty("cache_control", out _));
        Assert.Equal("enabled", json.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(2048, json.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.False(json.TryGetProperty("temperature", out _), "temperature is dropped when thinking is on");
        Assert.Equal("any", json.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.Equal("lookup", json.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("object", json.GetProperty("tools")[0].GetProperty("input_schema").GetProperty("type").GetString());

        var messages = json.GetProperty("messages");
        Assert.Equal("image", messages[0].GetProperty("content")[1].GetProperty("type").GetString());
        Assert.Equal("image/jpeg", messages[0].GetProperty("content")[1].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal("AQID", messages[0].GetProperty("content")[1].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("tool_use", messages[1].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("y", messages[1].GetProperty("content")[0].GetProperty("input").GetProperty("q").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Equal("tool_result", messages[2].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("toolu_0", messages[2].GetProperty("content")[0].GetProperty("tool_use_id").GetString());

        Assert.Equal("Let me check.", result.Text);
        Assert.Equal(FinishReasons.ToolCalls, result.FinishReason);
        Assert.Equal(new ToolCall("toolu_1", "lookup", "{\"q\":\"x\"}"), Assert.Single(result.ToolCalls!));
    }

    [Fact]
    public async Task JsonSchema_uses_a_forced_tool_and_returns_its_input_as_text()
    {
        var host = new TestHost().Env("ANTHROPIC_API_KEY", "k").Enqueue(CannedResponse.Ok("""
            {"type":"message","content":[{"type":"tool_use","id":"toolu_s","name":"structured_output","input":{"answer":42}}],
             "stop_reason":"tool_use","usage":{"input_tokens":1,"output_tokens":1}}
            """));
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"answer":{"type":"integer"}}}""").RootElement;

        var result = await Provider(host).ChatAsync(TestHost.Prompt("anthropic:claude-sonnet-4-6") with { ResponseFormat = ResponseFormat.JsonSchema, Schema = schema }, CancellationToken.None);

        var json = host.Handler.Last.Json;
        Assert.Equal("tool", json.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.Equal("structured_output", json.GetProperty("tool_choice").GetProperty("name").GetString());
        Assert.Equal("{\"answer\":42}", result.Text);
        Assert.Equal(FinishReasons.Stop, result.FinishReason);
        Assert.Null(result.ToolCalls);
    }

    [Fact]
    public async Task Stream_reads_message_start_deltas_and_message_delta_usage()
    {
        var sse = Streams.Sse(
            "message_start|" + """{"type":"message_start","message":{"id":"m","model":"claude-sonnet-4-6","usage":{"input_tokens":30,"output_tokens":1,"cache_read_input_tokens":5,"cache_creation_input_tokens":0}}}""",
            "content_block_start|" + """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            "ping|" + """{"type":"ping"}""",
            "content_block_delta|" + """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}""",
            "content_block_delta|" + """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lo"}}""",
            "content_block_stop|" + """{"type":"content_block_stop","index":0}""",
            "message_delta|" + """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":12}}""",
            "message_stop|" + """{"type":"message_stop"}""");
        var host = new TestHost().Env("ANTHROPIC_API_KEY", "k").Enqueue(CannedResponse.Sse(sse));

        var deltas = await Streams.Collect(Provider(host).StreamAsync(TestHost.Prompt("anthropic:claude-sonnet-4-6"), CancellationToken.None));

        Assert.True(host.Handler.Last.Json.GetProperty("stream").GetBoolean());
        Assert.Equal("Hello", Streams.Text(deltas));
        var final = deltas[^1];
        Assert.True(final.IsFinal);
        Assert.Equal(FinishReasons.Stop, final.FinishReason);
        Assert.Equal(new LlmUsage(30, 12, 5, 0), final.Usage);
        Assert.Equal("claude-sonnet-4-6", final.Resolved!.Value.Model);
    }

    [Fact]
    public async Task Stream_assembles_tool_use_input_json_deltas()
    {
        var sse = Streams.Sse(
            """{"type":"message_start","message":{"usage":{"input_tokens":1,"output_tokens":0}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_7","name":"lookup","input":{}}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"q\":"}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"\"z\"}"}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":6}}""",
            """{"type":"message_stop"}""");
        var host = new TestHost().Env("ANTHROPIC_API_KEY", "k").Enqueue(CannedResponse.Sse(sse));

        var deltas = await Streams.Collect(Provider(host).StreamAsync(TestHost.Prompt("anthropic:claude-sonnet-4-6"), CancellationToken.None));

        var final = deltas[^1];
        Assert.Equal(FinishReasons.ToolCalls, final.FinishReason);
        Assert.Equal(new ToolCall("toolu_7", "lookup", "{\"q\":\"z\"}"), Assert.Single(final.ToolCalls!));
    }

    [Fact]
    public async Task Stream_error_event_throws_LlmResponseException()
    {
        var sse = Streams.Sse("error|" + """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""");
        var host = new TestHost().Env("ANTHROPIC_API_KEY", "k").Enqueue(CannedResponse.Sse(sse));

        var ex = await Assert.ThrowsAsync<LlmResponseException>(async () => await Streams.Collect(Provider(host).StreamAsync(TestHost.Prompt("anthropic:claude-sonnet-4-6"), CancellationToken.None)));

        Assert.Contains("Overloaded", ex.Message);
        Assert.Equal("overloaded_error", ex.ErrorType);
        Assert.True(ex.IsTransient, "an in-band overloaded_error is the HTTP 529 equivalent and must be retryable");
        Assert.True(new RetryPolicy().IsTransient(ex));
    }

    [Theory]
    [InlineData("overloaded_error", true)]
    [InlineData("overloaded", true)]
    [InlineData("rate_limit_error", true)]
    [InlineData("api_error", false)]
    [InlineData("invalid_request_error", false)]
    [InlineData(null, false)]
    public void In_band_error_types_classify_as_transient_or_not(string? errorType, bool transient)
    {
        var ex = new LlmResponseException("x", "anthropic", errorType: errorType);

        Assert.Equal(transient, ex.IsTransient);
        Assert.Equal(transient, new RetryPolicy().IsTransient(ex));
        Assert.False(new LlmResponseException("x", "anthropic", errorType: "overloaded_error", transient: false).IsTransient);
    }

    [Fact]
    public async Task ListModels_reads_v1_models()
    {
        var host = new TestHost().Env("ANTHROPIC_API_KEY", "k").Enqueue(CannedResponse.Ok("""{"data":[{"id":"claude-sonnet-4-6","display_name":"Claude Sonnet 4.6","created_at":"2026-02-01T00:00:00Z","type":"model"}],"has_more":false}"""));

        var models = await Provider(host).ListModelsAsync(CancellationToken.None);

        Assert.Equal("https://api.anthropic.com/v1/models?limit=100", host.Handler.Last.Url.ToString());
        Assert.Equal("k", host.Handler.Last.Header("x-api-key"));
        var model = Assert.Single(models);
        Assert.Equal("Claude Sonnet 4.6", model.DisplayName);
        Assert.Equal(200_000, model.ContextTokens);
    }
}
