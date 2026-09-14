using Fx.ControlKit.Llm;
using Fx.ControlKit.Llm.Providers;
using Xunit;

namespace FlexCore.Llm.Tests;

public class OpenAiResponsesProviderTests
{
    private const string Completed = """
        {"id":"resp_1","object":"response","status":"completed","model":"gpt-5.4-2026-01-01",
         "output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Answer text"}]}],
         "usage":{"input_tokens":50,"output_tokens":12,"input_tokens_details":{"cached_tokens":10},"output_tokens_details":{"reasoning_tokens":4}}}
        """;

    private static OpenAiResponsesProvider Provider(TestHost host) => new(host.Http, host.Credentials);

    [Fact]
    public async Task Chat_sends_responses_shape_and_reads_input_output_tokens()
    {
        var host = new TestHost().Env("OPENAI_API_KEY", "sk-test").Enqueue(CannedResponse.Ok(Completed));

        var request = TestHost.Prompt("openai:gpt-5.4", "Hello", "Be brief.") with
        {
            MaxOutputTokens = 300,
            Temperature = 0.7,
            Reasoning = new ReasoningOptions(Effort: "high"),
        };
        var result = await Provider(host).ChatAsync(request, CancellationToken.None);

        var sent = host.Handler.Last;
        Assert.Equal("https://api.openai.com/v1/responses", sent.Url.ToString());
        Assert.Equal("Bearer sk-test", sent.Headers.Authorization!.ToString());

        var json = sent.Json;
        Assert.Equal("gpt-5.4", json.GetProperty("model").GetString());
        Assert.Equal("Be brief.", json.GetProperty("instructions").GetString());
        var input = json.GetProperty("input");
        Assert.Equal(1, input.GetArrayLength());
        Assert.Equal("user", input[0].GetProperty("role").GetString());
        Assert.Equal("input_text", input[0].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("Hello", input[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(300, json.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("high", json.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(json.TryGetProperty("temperature", out _), "gpt-5 models must not receive temperature");

        Assert.Equal("Answer text", result.Text);
        Assert.Equal(FinishReasons.Stop, result.FinishReason);
        Assert.Equal(new LlmUsage(40, 12, 10, null), result.Usage);
        Assert.Equal("gpt-5.4-2026-01-01", result.Resolved.Model);
    }

    [Fact]
    public async Task Non_reasoning_model_keeps_temperature_and_json_schema_format()
    {
        var host = new TestHost().Env("OPENAI_API_KEY", "k").Enqueue(CannedResponse.Ok(Completed));
        var schema = System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{"a":{"type":"string"}},"required":["a"],"additionalProperties":false}""").RootElement;

        var request = TestHost.Prompt("openai:gpt-4.1-mini") with
        {
            Temperature = 0.3,
            ResponseFormat = ResponseFormat.JsonSchema,
            Schema = schema,
            SchemaName = "answer",
        };
        await Provider(host).ChatAsync(request, CancellationToken.None);

        var json = host.Handler.Last.Json;
        Assert.Equal(0.3, json.GetProperty("temperature").GetDouble());
        Assert.False(json.TryGetProperty("reasoning", out _));
        var format = json.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("answer", format.GetProperty("name").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.Equal("object", format.GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Incomplete_at_max_output_tokens_is_truncated()
    {
        var host = new TestHost().Env("OPENAI_API_KEY", "k").Enqueue(CannedResponse.Ok("""
            {"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},
             "output":[{"type":"message","content":[{"type":"output_text","text":"partial"}]}],
             "usage":{"input_tokens":5,"output_tokens":100}}
            """));

        var result = await Provider(host).ChatAsync(TestHost.Prompt("openai:gpt-5.4"), CancellationToken.None);

        Assert.Equal("partial", result.Text);
        Assert.True(result.IsTruncated);
        Assert.Equal(FinishReasons.MaxTokens, result.FinishReason);
    }

    [Fact]
    public async Task Function_calls_are_returned_and_replayed()
    {
        var host = new TestHost().Env("OPENAI_API_KEY", "k").Enqueue(CannedResponse.Ok("""
            {"status":"completed","output":[{"type":"function_call","call_id":"call_a","name":"weather","arguments":"{\"city\":\"Oslo\"}"}],
             "usage":{"input_tokens":1,"output_tokens":1}}
            """));
        var provider = Provider(host);

        var first = await provider.ChatAsync(TestHost.Prompt("openai:gpt-5.4") with
        {
            Tools = new[] { new ToolDefinition("weather", "Weather by city", null) },
            ToolChoice = ToolChoice.Required,
        }, CancellationToken.None);

        Assert.Equal(FinishReasons.ToolCalls, first.FinishReason);
        var call = Assert.Single(first.ToolCalls!);
        Assert.Equal("call_a", call.Id);
        var toolsSent = host.Handler.Last.Json.GetProperty("tools")[0];
        Assert.Equal("function", toolsSent.GetProperty("type").GetString());
        Assert.Equal("weather", toolsSent.GetProperty("name").GetString());
        Assert.Equal("required", host.Handler.Last.Json.GetProperty("tool_choice").GetString());

        host.Enqueue(CannedResponse.Ok(Completed));
        await provider.ChatAsync(new ChatRequest
        {
            Model = "openai:gpt-5.4",
            Messages = new[]
            {
                ChatMessage.User("Weather?"),
                ChatMessage.Assistant(new ToolCallPart(call)),
                ChatMessage.ToolResult(call.Id, "{\"temp\":3}"),
            },
        }, CancellationToken.None);

        var input = host.Handler.Last.Json.GetProperty("input");
        Assert.Equal(3, input.GetArrayLength());
        Assert.Equal("function_call", input[1].GetProperty("type").GetString());
        Assert.Equal("call_a", input[1].GetProperty("call_id").GetString());
        Assert.Equal("function_call_output", input[2].GetProperty("type").GetString());
        Assert.Equal("{\"temp\":3}", input[2].GetProperty("output").GetString());
    }

    [Fact]
    public async Task Stream_reads_output_text_deltas_and_completed_usage()
    {
        var sse = Streams.Sse(
            "response.created|" + """{"type":"response.created","response":{"id":"r"}}""",
            "response.output_text.delta|" + """{"type":"response.output_text.delta","delta":"Hel"}""",
            "response.output_text.delta|" + """{"type":"response.output_text.delta","delta":"lo"}""",
            "response.completed|" + """{"type":"response.completed","response":{"status":"completed","model":"gpt-5.4","usage":{"input_tokens":8,"output_tokens":2}}}""");
        var host = new TestHost().Env("OPENAI_API_KEY", "k").Enqueue(CannedResponse.Sse(sse));

        var deltas = await Streams.Collect(Provider(host).StreamAsync(TestHost.Prompt("openai:gpt-5.4"), CancellationToken.None));

        Assert.True(host.Handler.Last.Json.GetProperty("stream").GetBoolean());
        Assert.Equal("Hello", Streams.Text(deltas));
        var final = deltas[^1];
        Assert.True(final.IsFinal);
        Assert.Equal(new LlmUsage(8, 2, null, null), final.Usage);
        Assert.Equal(FinishReasons.Stop, final.FinishReason);
    }

    [Fact]
    public async Task Stream_incomplete_reports_max_tokens()
    {
        var sse = Streams.Sse(
            """{"type":"response.output_text.delta","delta":"x"}""",
            """{"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"usage":{"input_tokens":1,"output_tokens":9}}}""");
        var host = new TestHost().Env("OPENAI_API_KEY", "k").Enqueue(CannedResponse.Sse(sse));

        var deltas = await Streams.Collect(Provider(host).StreamAsync(TestHost.Prompt("openai:gpt-5.4"), CancellationToken.None));

        Assert.Equal(FinishReasons.MaxTokens, deltas[^1].FinishReason);
    }

    [Fact]
    public async Task Image_generation_posts_to_images_generations_and_decodes_b64()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var b64 = Convert.ToBase64String(png);
        var host = new TestHost().Env("OPENAI_API_KEY", "k")
            .Enqueue(CannedResponse.Ok("{\"created\":1,\"data\":[{\"b64_json\":\"" + b64 + "\"}],\"usage\":{\"input_tokens\":12,\"output_tokens\":1000}}"));

        var result = await Provider(host).GenerateImageAsync(new ImageRequest { Model = "openai:gpt-image-1", Prompt = "a cat", Size = "1024x1024" }, CancellationToken.None);

        var sent = host.Handler.Last;
        Assert.Equal("https://api.openai.com/v1/images/generations", sent.Url.ToString());
        Assert.Equal("gpt-image-1", sent.Json.GetProperty("model").GetString());
        Assert.Equal("a cat", sent.Json.GetProperty("prompt").GetString());
        Assert.Equal(1, sent.Json.GetProperty("n").GetInt32());
        Assert.Equal("1024x1024", sent.Json.GetProperty("size").GetString());
        Assert.Equal("png", sent.Json.GetProperty("output_format").GetString());
        Assert.False(sent.Json.TryGetProperty("response_format", out _));

        Assert.Equal(png, result.First.Bytes.ToArray());
        Assert.Equal("image/png", result.First.MimeType);
        Assert.Equal(new LlmUsage(12, 1000, null, null), result.Usage);
    }

    [Fact]
    public async Task Embeddings_post_to_embeddings_endpoint()
    {
        var host = new TestHost().Env("OPENAI_API_KEY", "k")
            .Enqueue(CannedResponse.Ok("""{"object":"list","data":[{"index":1,"embedding":[0.5,0.25]},{"index":0,"embedding":[1.0,2.0]}],"model":"text-embedding-3-small","usage":{"prompt_tokens":4,"total_tokens":4}}"""));

        var result = await Provider(host).EmbedAsync(new EmbeddingRequest { Model = "openai:text-embedding-3-small", Inputs = new[] { "a", "b" }, Dimensions = 2 }, CancellationToken.None);

        var sent = host.Handler.Last;
        Assert.Equal("https://api.openai.com/v1/embeddings", sent.Url.ToString());
        Assert.Equal(2, sent.Json.GetProperty("input").GetArrayLength());
        Assert.Equal(2, sent.Json.GetProperty("dimensions").GetInt32());
        Assert.Equal(2, result.Vectors.Count);
        Assert.Equal(new[] { 1.0f, 2.0f }, result.Vectors[0]);
        Assert.Equal(new[] { 0.5f, 0.25f }, result.Vectors[1]);
        Assert.Equal(4, result.Usage!.Value.Input);
    }

    [Fact]
    public async Task Legacy_full_endpoint_is_reduced_to_base()
    {
        var host = new TestHost().Env("OPENAI_API_KEY", "k").Env("OPENAI_BASE_URL", "https://api.openai.com/v1/responses").Enqueue(CannedResponse.Ok(Completed));

        await Provider(host).ChatAsync(TestHost.Prompt("openai:gpt-5.4"), CancellationToken.None);

        Assert.Equal("https://api.openai.com/v1/responses", host.Handler.Last.Url.ToString());
    }
}
