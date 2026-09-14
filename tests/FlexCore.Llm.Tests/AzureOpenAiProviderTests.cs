using Fx.ControlKit.Llm;
using Fx.ControlKit.Llm.Configuration;
using Fx.ControlKit.Llm.Providers;
using Xunit;

namespace FlexCore.Llm.Tests;

public class AzureOpenAiProviderTests
{
    private const string ChatResponse = """
        {"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"model":"gpt-4.1","usage":{"prompt_tokens":3,"completion_tokens":1}}
        """;

    [Fact]
    public async Task Uses_deployment_path_api_version_and_api_key_header()
    {
        var host = new TestHost()
            .Env("AZURE_OPENAI_ENDPOINT", "https://my-res.openai.azure.com")
            .Env("AZURE_OPENAI_API_KEY", "azkey")
            .Enqueue(CannedResponse.Ok(ChatResponse));
        host.Options.AzureOpenAi.Deployment = "ghostwriter-gpt41";
        var provider = new AzureOpenAiProvider(host.Http, host.Credentials);

        var result = await provider.ChatAsync(TestHost.Prompt("azureopenai:gpt-4.1") with { MaxOutputTokens = 50 }, CancellationToken.None);

        var sent = host.Handler.Last;
        Assert.Equal("https://my-res.openai.azure.com/openai/deployments/ghostwriter-gpt41/chat/completions?api-version=2024-10-21", sent.Url.ToString());
        Assert.Equal("azkey", sent.Header("api-key"));
        Assert.Null(sent.Headers.Authorization);
        Assert.Equal(50, sent.Json.GetProperty("max_tokens").GetInt32());
        Assert.Equal("ok", result.Text);
        Assert.Equal(new ModelRef("azureopenai", "gpt-4.1"), result.Resolved);
    }

    [Fact]
    public async Task Falls_back_to_token_provider_bearer_when_no_key()
    {
        var host = new TestHost()
            .Env("AZURE_OPENAI_ENDPOINT", "https://my-res.openai.azure.com/openai/deployments/whatever/chat/completions?api-version=old")
            .Enqueue(CannedResponse.Ok(ChatResponse));
        host.Options.AzureOpenAi.ApiVersion = "2025-01-01-preview";
        string? seenScope = null;
        var tokens = new DelegateTokenProvider((scope, _) =>
        {
            seenScope = scope;
            return Task.FromResult("eyJ-token");
        });
        var provider = new AzureOpenAiProvider(host.Http, host.Credentials, tokens);

        Assert.True(provider.IsConfigured);
        await provider.ChatAsync(TestHost.Prompt("azureopenai:gpt-5.4") with { MaxOutputTokens = 10, Temperature = 0.5 }, CancellationToken.None);

        var sent = host.Handler.Last;
        Assert.Equal("https://my-res.openai.azure.com/openai/deployments/gpt-5.4/chat/completions?api-version=2025-01-01-preview", sent.Url.ToString());
        Assert.Equal("Bearer eyJ-token", sent.Headers.Authorization!.ToString());
        Assert.Equal(AzureOpenAiProvider.DefaultTokenScope, seenScope);
        Assert.Null(sent.Header("api-key"));
        Assert.Equal(10, sent.Json.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(sent.Json.TryGetProperty("temperature", out _));
        Assert.False(sent.Json.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public async Task Extras_api_version_overrides_query()
    {
        var host = new TestHost()
            .Env("AZURE_OPENAI_ENDPOINT", "https://my-res.openai.azure.com")
            .Env("AZURE_OPENAI_API_KEY", "k")
            .Enqueue(CannedResponse.Ok(ChatResponse));
        var provider = new AzureOpenAiProvider(host.Http, host.Credentials);

        await provider.ChatAsync(TestHost.Prompt("azureopenai:gpt-4.1") with { Extras = new Dictionary<string, object?> { ["api-version"] = "2024-06-01" } }, CancellationToken.None);

        Assert.EndsWith("?api-version=2024-06-01", host.Handler.Last.Url.ToString());
        Assert.False(host.Handler.Last.Json.TryGetProperty("api-version", out _));
    }

    [Fact]
    public async Task Stream_uses_chat_completions_sse()
    {
        var sse = Streams.Sse(
            """{"choices":[{"delta":{"content":"A"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"length"}],"usage":{"prompt_tokens":2,"completion_tokens":1}}""",
            "[DONE]");
        var host = new TestHost().Env("AZURE_OPENAI_ENDPOINT", "https://r.openai.azure.com").Env("AZURE_OPENAI_API_KEY", "k").Enqueue(CannedResponse.Sse(sse));

        var deltas = await Streams.Collect(new AzureOpenAiProvider(host.Http, host.Credentials).StreamAsync(TestHost.Prompt("azureopenai:gpt-4.1"), CancellationToken.None));

        Assert.Equal("A", Streams.Text(deltas));
        Assert.Equal(FinishReasons.MaxTokens, deltas[^1].FinishReason);
        Assert.Equal(new LlmUsage(2, 1), deltas[^1].Usage);
    }
}
