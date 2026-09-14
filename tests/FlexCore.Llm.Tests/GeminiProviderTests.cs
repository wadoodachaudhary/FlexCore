using Fx.ControlKit.Llm;
using Fx.ControlKit.Llm.Providers;
using Xunit;

namespace FlexCore.Llm.Tests;

public class GeminiProviderTests
{
    private const string GenerateResponse = """
        {"candidates":[{"content":{"role":"model","parts":[{"text":"Gemini says hi"}]},"finishReason":"MAX_TOKENS","index":0}],
         "usageMetadata":{"promptTokenCount":40,"candidatesTokenCount":6,"cachedContentTokenCount":15,"thoughtsTokenCount":2,"totalTokenCount":48},
         "modelVersion":"gemini-2.5-pro"}
        """;

    private static GeminiProvider Provider(TestHost host) => new(host.Http, host.Credentials);

    [Fact]
    public async Task Chat_posts_generateContent_with_header_key_and_native_shape()
    {
        var host = new TestHost().Env("GEMINI_API_KEY", "AIza-test").Enqueue(CannedResponse.Ok(GenerateResponse));

        var request = new ChatRequest
        {
            Model = "gemini:gemini-2.5-pro",
            System = "Be brief.",
            Messages = new[] { ChatMessage.User("Hi"), ChatMessage.Assistant("Hello!"), ChatMessage.User("Again") },
            Temperature = 0.4,
            MaxOutputTokens = 128,
            ResponseFormat = ResponseFormat.Json,
        };
        var result = await Provider(host).ChatAsync(request, CancellationToken.None);

        var sent = host.Handler.Last;
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:generateContent", sent.Url.ToString());
        Assert.Equal("AIza-test", sent.Header("x-goog-api-key"));
        Assert.DoesNotContain("key=", sent.Url.ToString());

        var json = sent.Json;
        Assert.Equal("Be brief.", json.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        var contents = json.GetProperty("contents");
        Assert.Equal(3, contents.GetArrayLength());
        Assert.Equal("user", contents[0].GetProperty("role").GetString());
        Assert.Equal("model", contents[1].GetProperty("role").GetString());
        Assert.Equal("Hello!", contents[1].GetProperty("parts")[0].GetProperty("text").GetString());
        var generation = json.GetProperty("generationConfig");
        Assert.Equal(0.4, generation.GetProperty("temperature").GetDouble());
        Assert.Equal(128, generation.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal("application/json", generation.GetProperty("responseMimeType").GetString());

        Assert.Equal("Gemini says hi", result.Text);
        Assert.Equal(FinishReasons.MaxTokens, result.FinishReason);
        Assert.Equal(new LlmUsage(25, 8, 15, null), result.Usage);
        Assert.Equal("gemini-2.5-pro", result.Resolved.Model);
    }

    [Fact]
    public async Task GOOGLE_API_KEY_and_legacy_template_endpoint_are_accepted()
    {
        var host = new TestHost()
            .Env("GOOGLE_API_KEY", "g")
            .Env("GEMINI_ENDPOINT", "https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent")
            .Enqueue(CannedResponse.Ok(GenerateResponse));

        await Provider(host).ChatAsync(TestHost.Prompt("gemini:gemini-2.5-flash"), CancellationToken.None);

        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent", host.Handler.Last.Url.ToString());
        Assert.Equal("g", host.Handler.Last.Header("x-goog-api-key"));
    }

    [Fact]
    public async Task Tools_and_function_calls_round_trip()
    {
        var host = new TestHost().Env("GEMINI_API_KEY", "g").Enqueue(CannedResponse.Ok("""
            {"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"lookup","args":{"q":"x"}}}]},"finishReason":"STOP"}],
             "usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1}}
            """));

        var result = await Provider(host).ChatAsync(new ChatRequest
        {
            Model = "gemini:gemini-2.5-pro",
            Messages = new[]
            {
                ChatMessage.User("Go"),
                ChatMessage.Assistant(new ToolCallPart(new ToolCall("call_0", "lookup", "{\"q\":\"y\"}"))),
                ChatMessage.ToolResult("call_0", "{\"hits\":2}", name: "lookup"),
            },
            Tools = new[] { new ToolDefinition("lookup", "d", null) },
            ToolChoice = ToolChoice.Auto,
        }, CancellationToken.None);

        var json = host.Handler.Last.Json;
        Assert.Equal("lookup", json.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("name").GetString());
        Assert.Equal("AUTO", json.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString());
        var contents = json.GetProperty("contents");
        Assert.Equal("y", contents[1].GetProperty("parts")[0].GetProperty("functionCall").GetProperty("args").GetProperty("q").GetString());
        Assert.Equal("lookup", contents[2].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("name").GetString());
        Assert.Equal(2, contents[2].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("response").GetProperty("hits").GetInt32());

        Assert.Equal(FinishReasons.ToolCalls, result.FinishReason);
        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("lookup", call.Name);
        Assert.Equal("{\"q\":\"x\"}", call.ArgumentsJson);
    }

    [Fact]
    public async Task Stream_uses_streamGenerateContent_alt_sse()
    {
        var sse = Streams.Sse(
            """{"candidates":[{"content":{"role":"model","parts":[{"text":"Hel"}]}}],"usageMetadata":{"promptTokenCount":4,"candidatesTokenCount":1}}""",
            """{"candidates":[{"content":{"role":"model","parts":[{"text":"lo"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":4,"candidatesTokenCount":2},"modelVersion":"gemini-2.5-pro"}""");
        var host = new TestHost().Env("GEMINI_API_KEY", "g").Enqueue(CannedResponse.Sse(sse));

        var deltas = await Streams.Collect(Provider(host).StreamAsync(TestHost.Prompt("gemini:gemini-2.5-pro"), CancellationToken.None));

        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:streamGenerateContent?alt=sse", host.Handler.Last.Url.ToString());
        Assert.Equal("Hello", Streams.Text(deltas));
        var final = deltas[^1];
        Assert.Equal(FinishReasons.Stop, final.FinishReason);
        Assert.Equal(new LlmUsage(4, 2, null, null), final.Usage);
        Assert.Equal("gemini-2.5-pro", final.Resolved!.Value.Model);
    }

    [Fact]
    public async Task ListModels_and_embeddings_use_v1beta_paths()
    {
        var host = new TestHost().Env("GEMINI_API_KEY", "g")
            .Enqueue(CannedResponse.Ok("""{"models":[{"name":"models/gemini-2.5-pro","displayName":"Gemini 2.5 Pro","inputTokenLimit":1048576,"outputTokenLimit":65536,"supportedGenerationMethods":["generateContent"]},{"name":"models/aqa","supportedGenerationMethods":["generateAnswer"]}]}"""))
            .Enqueue(CannedResponse.Ok("""{"embeddings":[{"values":[0.1,0.2]},{"values":[0.3,0.4]}]}"""));
        var provider = Provider(host);

        var models = await provider.ListModelsAsync(CancellationToken.None);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models?pageSize=200", host.Handler.Last.Url.ToString());
        var model = Assert.Single(models);
        Assert.Equal("gemini-2.5-pro", model.Id);
        Assert.Equal(1048576, model.ContextTokens);

        var embeddings = await provider.EmbedAsync(EmbeddingRequest.For("gemini:gemini-embedding-001", "a", "b"), CancellationToken.None);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:batchEmbedContents", host.Handler.Last.Url.ToString());
        Assert.Equal("models/gemini-embedding-001", host.Handler.Last.Json.GetProperty("requests")[0].GetProperty("model").GetString());
        Assert.Equal(2, embeddings.Vectors.Count);
        Assert.Equal(new[] { 0.3f, 0.4f }, embeddings.Vectors[1]);
    }
}
