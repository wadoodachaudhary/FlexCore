using Fx.ControlKit.Llm;
using Fx.ControlKit.Llm.Configuration;
using Fx.ControlKit.Llm.Providers;
using Xunit;

namespace FlexCore.Llm.Tests;

public class ProviderQuirkTests
{
    private const string ChatOk = """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";
    private const string OllamaOk = """{"model":"qwen3:8b","message":{"role":"assistant","content":"ok"},"done":true,"done_reason":"stop"}""";

    [Fact]
    public async Task Huggingface_router_disables_kimi_thinking_and_lists_router_models()
    {
        var host = new TestHost().Env("HF_TOKEN", "t").Enqueue(CannedResponse.Ok(ChatOk), CannedResponse.Ok(ChatOk), CannedResponse.Ok(ChatOk));
        var provider = new OpenAiCompatibleChatProvider(ProviderKeys.HuggingFace, host.Http, host.Credentials);

        await provider.ChatAsync(TestHost.Prompt("huggingface:moonshotai/Kimi-K2.6:novita"), CancellationToken.None);
        Assert.Equal("disabled", host.Handler.Last.Json.GetProperty("thinking").GetProperty("type").GetString());

        await provider.ChatAsync(TestHost.Prompt("huggingface:moonshotai/Kimi-K2.6-thinking:novita"), CancellationToken.None);
        Assert.False(host.Handler.Last.Json.TryGetProperty("thinking", out _));

        await provider.ChatAsync(TestHost.Prompt("huggingface:Qwen/Qwen3-32B:nscale"), CancellationToken.None);
        Assert.False(host.Handler.Last.Json.TryGetProperty("thinking", out _));

        Assert.True(provider.IsRouterEndpoint(host.Credentials.Resolve("huggingface")));
        Assert.True(provider.Supports(LlmCapabilities.ListModels));
        Assert.False(provider.Supports(LlmCapabilities.Embeddings));
        var ids = provider.ConfiguredModels.Select(m => m.Id).ToList();
        Assert.Contains("Qwen/Qwen3-32B:nscale", ids);
        Assert.Contains("moonshotai/Kimi-K2.6:novita", ids);
        Assert.DoesNotContain("MaziyarPanahi/calme-3.2-instruct-78b", ids);
    }

    [Fact]
    public async Task Huggingface_dedicated_endpoint_serves_its_own_model_list_and_embeddings()
    {
        var host = new TestHost()
            .Env("HF_TOKEN", "t")
            .Env("HUGGINGFACE_ENDPOINT", "https://abc123.us-east-1.aws.endpoints.huggingface.cloud")
            .Env("HUGGINGFACE_MODEL", "Qwen/Qwen3-32B:nscale")   // the router default; ignored for a dedicated endpoint
            .Enqueue(CannedResponse.Ok(ChatOk));
        var provider = new OpenAiCompatibleChatProvider(ProviderKeys.HuggingFace, host.Http, host.Credentials);

        Assert.False(provider.Supports(LlmCapabilities.ListModels));
        Assert.True(provider.Supports(LlmCapabilities.Embeddings));
        Assert.Equal("MaziyarPanahi/calme-3.2-instruct-78b", Assert.Single(provider.ConfiguredModels).Id);
        Assert.Equal("MaziyarPanahi/calme-3.2-instruct-78b", Assert.Single(await provider.ListModelsAsync(CancellationToken.None)).Id);

        await provider.ChatAsync(TestHost.Prompt("huggingface:"), CancellationToken.None);
        Assert.Equal("https://abc123.us-east-1.aws.endpoints.huggingface.cloud/v1/chat/completions", host.Handler.Last.Url.ToString());
        Assert.Equal("MaziyarPanahi/calme-3.2-instruct-78b", host.Handler.Last.Json.GetProperty("model").GetString());

        host.Env("HF_DEDICATED_MODELS", "org/a, org/b").Env("HF_DEDICATED_MODEL", "org/b");
        Assert.Equal(new[] { "org/b", "org/a" }, provider.ConfiguredModels.Select(m => m.Id));
        host.Enqueue(CannedResponse.Ok(ChatOk));
        await provider.ChatAsync(TestHost.Prompt("huggingface:"), CancellationToken.None);
        Assert.Equal("org/b", host.Handler.Last.Json.GetProperty("model").GetString());
    }

    [Fact]
    public async Task OpenAi_provider_pointed_at_an_azure_host_uses_api_key_and_openai_v1_path()
    {
        var host = new TestHost()
            .Env("OPENAI_BASE_URL", "https://my-res.openai.azure.com")
            .Env("OPENAI_API_KEY", "azkey")
            .Enqueue(CannedResponse.Ok("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"az"}]}]}"""));
        var provider = new OpenAiResponsesProvider(host.Http, host.Credentials);

        var result = await provider.ChatAsync(TestHost.Prompt("openai:gpt-4.1"), CancellationToken.None);

        Assert.Equal("https://my-res.openai.azure.com/openai/v1/responses", host.Handler.Last.Url.ToString());
        Assert.Equal("azkey", host.Handler.Last.Header("api-key"));
        Assert.Null(host.Handler.Last.Headers.Authorization);
        Assert.Equal("az", result.Text);

        host.Environment.Remove("OPENAI_API_KEY");
        var withToken = new OpenAiResponsesProvider(host.Http, host.Credentials, new DelegateTokenProvider(_ => Task.FromResult("tok")));
        Assert.True(withToken.IsConfigured);
        host.Enqueue(CannedResponse.Ok("""{"status":"completed","output":[]}"""));
        await withToken.ChatAsync(TestHost.Prompt("openai:gpt-4.1"), CancellationToken.None);
        Assert.Equal("Bearer tok", host.Handler.Last.Headers.Authorization!.ToString());
        Assert.False(new OpenAiResponsesProvider(host.Http, host.Credentials).IsConfigured);
    }

    [Fact]
    public async Task Anthropic_normalises_model_separators_and_filters_the_listing()
    {
        Assert.Equal("claude-sonnet-4-6", AnthropicMessagesProvider.NormalizeModelId("claude_sonnet_4.6"));
        Assert.Equal("gpt-4.1", AnthropicMessagesProvider.NormalizeModelId("gpt-4.1"));

        var host = new TestHost().Env("ANTHROPIC_API_KEY", "k")
            .Enqueue(CannedResponse.Ok("""{"type":"message","content":[{"type":"text","text":"hi"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}"""))
            .Enqueue(CannedResponse.Ok("""{"data":[{"id":"claude-opus-4-7","display_name":"Claude Opus 4.7"},{"id":"not-a-claude"}]}"""))
            .Enqueue(CannedResponse.Ok("""{"data":[]}"""));
        host.Options.Anthropic.Models.Add("claude-haiku-4-5");
        var provider = new AnthropicMessagesProvider(host.Http, host.Credentials);

        await provider.ChatAsync(TestHost.Prompt("anthropic:claude_sonnet_4.6"), CancellationToken.None);
        Assert.Equal("claude-sonnet-4-6", host.Handler.Last.Json.GetProperty("model").GetString());

        var listed = await provider.ListModelsAsync(CancellationToken.None);
        Assert.Equal("claude-opus-4-7", Assert.Single(listed).Id);

        var fallback = await provider.ListModelsAsync(CancellationToken.None);
        Assert.Equal("claude-haiku-4-5", Assert.Single(fallback).Id);
    }

    [Fact]
    public async Task Ollama_family_aliases_resolve_and_configuration_overrides_them()
    {
        var host = new TestHost().Enqueue(CannedResponse.Ok(OllamaOk), CannedResponse.Ok(OllamaOk), CannedResponse.Ok(OllamaOk));
        var provider = new OllamaProvider(ProviderKeys.Ollama, host.Http, host.Credentials);

        await provider.ChatAsync(TestHost.Prompt("ollama:qwen"), CancellationToken.None);
        Assert.Equal("qwen3:8b", host.Handler.Last.Json.GetProperty("model").GetString());

        await provider.ChatAsync(TestHost.Prompt("ollama:cloud-ollama:phi"), CancellationToken.None);
        Assert.Equal("phi3:medium", host.Handler.Last.Json.GetProperty("model").GetString());

        host.Options.Ollama.Aliases["qwen"] = "qwen3:32b";
        await provider.ChatAsync(TestHost.Prompt("ollama:qwen"), CancellationToken.None);
        Assert.Equal("qwen3:32b", host.Handler.Last.Json.GetProperty("model").GetString());

        Assert.Equal("ollama", ModelCatalog.InferProvider("deepseek"));
        Assert.Equal("ollama", ModelCatalog.InferProvider("mistral"));
        Assert.Equal("mistral:7b", ModelCatalog.ResolveAlias("ollama", "mistral"));
        Assert.Null(ModelCatalog.ResolveAlias("mistral", "mistral"));
    }

    [Theory]
    [InlineData("mistral:7b", "", "mistral:7b")]
    [InlineData("mistral:latest", "", "mistral:latest")]
    [InlineData("mistral:7b-instruct-q4_0", "", "mistral:7b-instruct-q4_0")]
    [InlineData("mistral:mistral-large-latest", "mistral", "mistral-large-latest")]
    [InlineData("mistral:codestral-latest", "mistral", "codestral-latest")]
    [InlineData("ollama:mistral:7b", "ollama", "mistral:7b")]
    public void Mistral_prefix_is_an_ollama_tag_when_followed_by_a_size_or_latest(string text, string provider, string model)
    {
        var parsed = ModelRef.Parse(text);
        Assert.Equal(provider, parsed.Provider);
        Assert.Equal(model, parsed.Model);
        if (provider.Length == 0) Assert.Equal("ollama", ModelCatalog.InferProvider(model));
    }

    [Fact]
    public async Task Ollama_listing_flags_cloud_proxied_tags_and_names_families()
    {
        var host = new TestHost().Enqueue(CannedResponse.Ok("""{"models":[{"name":"deepseek-v4-pro:cloud"},{"name":"gemma4:e4b"},{"name":"llama3.2:3b"},{"name":"gemma4:31b-cloud"},{"name":"phi3:mini"},{"name":"custom-thing:latest"}]}"""));
        var provider = new OllamaProvider(ProviderKeys.Ollama, host.Http, host.Credentials);

        var models = (await provider.ListModelsAsync(CancellationToken.None)).ToDictionary(m => m.Id);

        Assert.Equal("true", models["deepseek-v4-pro:cloud"].Metadata![OllamaProvider.CloudProxiedMetadata]);
        Assert.Equal("true", models["gemma4:31b-cloud"].Metadata![OllamaProvider.CloudProxiedMetadata]);
        Assert.False(models["gemma4:e4b"].Metadata!.ContainsKey(OllamaProvider.CloudProxiedMetadata));
        Assert.Equal("DeepSeek V4 Pro (cloud)", models["deepseek-v4-pro:cloud"].DisplayName);
        Assert.Equal("Gemma 4 Efficient 4B", models["gemma4:e4b"].DisplayName);
        Assert.Equal("Llama 3.2 3B", models["llama3.2:3b"].DisplayName);
        Assert.Equal("Phi 3 Mini", models["phi3:mini"].DisplayName);
        Assert.Equal("custom-thing:latest", models["custom-thing:latest"].DisplayName);
        Assert.Equal("DeepSeek R1 14B", OllamaProvider.DisplayNameFor("deepseek-r1:14b"));
        Assert.Equal("Qwen 2.5 Coder 32B", OllamaProvider.DisplayNameFor("qwen2.5-coder:32b"));
        Assert.Equal("Gemma 4 31B (Cloud)", OllamaProvider.DisplayNameFor("gemma4:31b:cloud"));
        Assert.Equal("DeepSeek V4 Pro (Cloud)", OllamaProvider.DisplayNameFor("deepseek-v4-pro:cloud"));
    }

    [Fact]
    public async Task Azure_openai_and_foundry_answer_configured_models_without_a_network_call()
    {
        var host = new TestHost()
            .Env("AZURE_OPENAI_ENDPOINT", "https://r.openai.azure.com").Env("AZURE_OPENAI_API_KEY", "k").Env("AZURE_OPENAI_DEPLOYMENT", "dep")
            .Env("AZURE_FOUNDRY_ENDPOINT", "https://f.services.ai.azure.com").Env("AZURE_FOUNDRY_API_KEY", "k").Env("AZURE_FOUNDRY_MODELS", "phi-4-mini-instruct;deepseek-v3.2");
        host.Options.AzureOpenAi.Models.Add("gpt-4.1");
        var azure = new AzureOpenAiProvider(host.Http, host.Credentials);
        var foundry = new OpenAiCompatibleChatProvider(ProviderKeys.AzureFoundry, host.Http, host.Credentials);

        Assert.False(azure.Supports(LlmCapabilities.ListModels));
        Assert.False(foundry.Supports(LlmCapabilities.ListModels));
        Assert.Equal(new[] { "dep", "gpt-4.1" }, (await azure.ListModelsAsync(CancellationToken.None)).Select(m => m.Id));
        Assert.Equal(new[] { "phi-4-mini-instruct", "deepseek-v3.2" }, (await foundry.ListModelsAsync(CancellationToken.None)).Select(m => m.Id));
        Assert.Empty(host.Handler.Requests);
    }

    [Fact]
    public void Every_provider_capability_set_matches_what_the_adapter_implements()
    {
        var host = new TestHost().Env("OPENAI_API_KEY", "k").Env("HF_TOKEN", "k");
        ILlmProvider[] providers =
        {
            new OpenAiResponsesProvider(host.Http, host.Credentials),
            new AzureOpenAiProvider(host.Http, host.Credentials),
            new AnthropicMessagesProvider(host.Http, host.Credentials),
            new GeminiProvider(host.Http, host.Credentials),
            new OllamaProvider(ProviderKeys.Ollama, host.Http, host.Credentials),
            new OllamaProvider(ProviderKeys.OllamaCloud, host.Http, host.Credentials),
            new OpenAiCompatibleChatProvider(ProviderKeys.AzureFoundry, host.Http, host.Credentials),
            new OpenAiCompatibleChatProvider(ProviderKeys.HuggingFace, host.Http, host.Credentials),
            new OpenAiCompatibleChatProvider(ProviderKeys.Groq, host.Http, host.Credentials),
            new OpenAiCompatibleChatProvider(ProviderKeys.XAi, host.Http, host.Credentials),
            new OpenAiCompatibleChatProvider(ProviderKeys.Mistral, host.Http, host.Credentials),
        };

        foreach (var provider in providers)
        {
            Assert.True(provider.Supports(LlmCapabilities.Chat | LlmCapabilities.Streaming), provider.Key);
            var type = provider.GetType();
            bool Overrides(string name) => type.GetMethod(name)!.DeclaringType != typeof(LlmProviderBase);
            Assert.Equal(provider.Supports(LlmCapabilities.ImageGeneration), Overrides(nameof(ILlmProvider.GenerateImageAsync)));
            if (provider.Supports(LlmCapabilities.Embeddings)) Assert.True(Overrides(nameof(ILlmProvider.EmbedAsync)), provider.Key);
            if (provider.Supports(LlmCapabilities.ListModels)) Assert.True(Overrides(nameof(ILlmProvider.ListModelsAsync)), provider.Key);
            // Azure deployments are named by the customer, so nothing is configured until they are; every other provider has catalog entries.
            if (provider.Key is not (ProviderKeys.AzureOpenAi or ProviderKeys.AzureFoundry)) Assert.NotEmpty(provider.ConfiguredModels);
        }

        Assert.True(providers.Single(p => p.Key == "openai").Supports(LlmCapabilities.ImageGeneration));
        Assert.False(providers.Single(p => p.Key == "anthropic").Supports(LlmCapabilities.Embeddings | LlmCapabilities.ImageGeneration));
        Assert.False(providers.Single(p => p.Key == "groq").Supports(LlmCapabilities.Embeddings));
        Assert.False(providers.Single(p => p.Key == "xai").Supports(LlmCapabilities.Embeddings));
    }
}
