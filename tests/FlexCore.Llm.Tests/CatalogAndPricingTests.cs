using Fx.ControlKit.Llm;
using Fx.ControlKit.Llm.Configuration;
using Fx.ControlKit.Llm.Pricing;
using Xunit;

namespace FlexCore.Llm.Tests;

public class CatalogAndPricingTests
{
    [Theory]
    [InlineData("anthropic:claude-sonnet-4-6", "anthropic", "claude-sonnet-4-6")]
    [InlineData("claude:claude-opus-4-7", "anthropic", "claude-opus-4-7")]
    [InlineData("ollama:qwen2.5-coder:32b", "ollama", "qwen2.5-coder:32b")]
    [InlineData("cloud-ollama:deepseek-v4-pro:cloud", "ollamacloud", "deepseek-v4-pro:cloud")]
    [InlineData("grok:grok-4", "xai", "grok-4")]
    [InlineData("mistralai:codestral-latest", "mistral", "codestral-latest")]
    [InlineData("qwen3:8b", "", "qwen3:8b")]
    [InlineData("gpt-5.4", "", "gpt-5.4")]
    [InlineData("huggingface:Qwen/Qwen3-32B:nscale", "huggingface", "Qwen/Qwen3-32B:nscale")]
    public void ModelRef_parses_provider_prefixes_and_aliases(string text, string provider, string model)
    {
        var parsed = ModelRef.Parse(text);
        Assert.Equal(provider, parsed.Provider);
        Assert.Equal(model, parsed.Model);
        Assert.Equal(provider.Length == 0 ? model : $"{provider}:{model}", parsed.ToString());
    }

    [Theory]
    [InlineData("claude-opus-4-7", "anthropic")]
    [InlineData("gpt-5.4-mini", "openai")]
    [InlineData("gemini-2.5-flash", "gemini")]
    [InlineData("grok-code-fast-1", "xai")]
    [InlineData("deepseek-v4-pro:cloud", "ollama")]
    [InlineData("qwen2.5-coder:32b", "ollama")]
    [InlineData("mistral-large-latest", "mistral")]
    [InlineData("llama-3.3-70b-versatile", "groq")]
    [InlineData("Qwen/Qwen3-32B:nscale", "huggingface")]
    [InlineData("text-embedding-3-small", "openai")]
    [InlineData("something-unknown", null)]
    public void Catalog_infers_provider(string model, string? provider)
        => Assert.Equal(provider, ModelCatalog.InferProvider(model));

    [Fact]
    public void Catalog_contains_the_ids_the_apps_use()
    {
        foreach (var id in new[] { "claude-opus-4-7", "claude-sonnet-4-6", "claude-haiku-4-5", "gpt-5.4", "gpt-5.4-mini", "gemini-2.5-pro", "gemini-2.5-flash", "deepseek-v4-pro:cloud", "qwen2.5-coder:32b" })
        {
            var info = ModelCatalog.Find(ModelRef.Parse(id));
            Assert.NotNull(info);
            Assert.True(info!.ContextTokens > 0);
            Assert.True(info.MaxOutputTokens > 0);
        }

        Assert.Equal("ollamacloud", ModelCatalog.Find(new ModelRef("ollamacloud", "qwen3:32b"))!.Provider);
        Assert.Equal("gpt-5.4", ModelCatalog.DefaultModel("openai"));
    }

    [Fact]
    public void Pricing_uses_longest_prefix_and_cache_multipliers()
    {
        var pricing = LlmPricing.Default;

        Assert.Equal("gpt-5.4-mini", pricing.Lookup(new ModelRef("openai", "gpt-5.4-mini-2026-03")) !.Value.Key);
        Assert.Equal("groq:llama-3.3-70b-versatile", pricing.Lookup(ModelRef.Parse("groq:llama-3.3-70b-versatile"))!.Value.Key);
        Assert.Equal("azureopenai:gpt-4.1", pricing.Lookup(new ModelRef("azureopenai", "ghostwriter-gpt41"))!.Value.Key);
        Assert.Equal("local", pricing.Lookup(ModelRef.Parse("ollama:qwen3:8b"))!.Value.Key);
        Assert.Null(pricing.Lookup(ModelRef.Parse("groq:unknown-model")));

        var sonnet = pricing.EstimateCostUsd(ModelRef.Parse("anthropic:claude-sonnet-4-6"), new LlmUsage(1_000_000, 0, 1_000_000, 1_000_000));
        Assert.Equal(3.00 + 3.00 * 0.10 + 3.00 * 1.25, sonnet!.Value, 6);

        var gpt = pricing.EstimateCostUsd(ModelRef.Parse("openai:gpt-5.4"), new LlmUsage(1_000_000, 1_000_000, 1_000_000, null));
        Assert.Equal(2.50 + 15.00 + 2.50 * 0.25, gpt!.Value, 6);

        Assert.Equal(0.0, pricing.EstimateCostUsd(ModelRef.Parse("ollamacloud:qwen3:8b"), new LlmUsage(5, 5)));
    }

    [Fact]
    public void Context_budget_prefers_catalog_then_prefix_then_provider_default()
    {
        var budget = LlmContextBudget.Default;

        var catalog = budget.Lookup(ModelRef.Parse("anthropic:claude-sonnet-4-6"));
        Assert.Equal(200_000, catalog.EstimatedContextTokens);
        Assert.Equal(8_000, catalog.ReservedOutputTokens);

        var prefix = budget.Lookup(ModelRef.Parse("anthropic:claude-haiku-9-9"));
        Assert.Equal("anthropic:claude-haiku", prefix.Key);

        var cloud = budget.Lookup(ModelRef.Parse("ollamacloud:phi3:medium-128k"));
        Assert.Equal("ollama:phi3:medium", cloud.Key);

        Assert.Equal("groq:default", budget.Lookup(ModelRef.Parse("groq:new-thing")).Key);
        Assert.Equal("default", budget.Lookup(new ModelRef(string.Empty, "x")).Key);

        Assert.Equal(3, budget.EstimateTokens("twelve chars"));
        Assert.True(budget.Fits(ModelRef.Parse("openai:gpt-5.4"), new string('a', 4 * 100_000)));
        Assert.False(budget.Fits(ModelRef.Parse("ollama:phi3:medium"), new string('a', 4 * 20_000)));
    }

    [Theory]
    [InlineData("api-key", AuthMode.ApiKeyHeader)]
    [InlineData("header", AuthMode.ApiKeyHeader)]
    [InlineData("managedidentity", AuthMode.Bearer)]
    [InlineData("Entra", AuthMode.Bearer)]
    [InlineData("basic", AuthMode.Basic)]
    [InlineData("None", AuthMode.None)]
    [InlineData("Auto", null)]
    [InlineData("", null)]
    public void AuthModes_parse_legacy_spellings(string text, AuthMode? expected)
        => Assert.Equal(expected, AuthModes.Parse(text));

    [Fact]
    public void Every_provider_has_an_environment_table_and_openai_family_env_names_match_ghostwriter()
    {
        foreach (var key in ProviderKeys.All)
        {
            Assert.NotNull(LlmEnvironmentVariables.For(key));
        }

        Assert.Equal(new[] { "OPENAI_API_KEY", "CODEX_API_KEY" }, LlmEnvironmentVariables.For("openai")!.ApiKey);
        Assert.Equal(new[] { "AZURE_OPENAI_API_KEY" }, LlmEnvironmentVariables.For("azureopenai")!.ApiKey);
        Assert.Equal(new[] { "ANTHROPIC_API_KEY", "CLAUDE_API_KEY" }, LlmEnvironmentVariables.For("anthropic")!.ApiKey);
        Assert.Equal(new[] { "GEMINI_API_KEY", "GOOGLE_API_KEY" }, LlmEnvironmentVariables.For("gemini")!.ApiKey);
        Assert.Equal(new[] { "XAI_API_KEY", "GROK_API_KEY" }, LlmEnvironmentVariables.For("xai")!.ApiKey);
        Assert.Equal(new[] { "HUGGINGFACE_API_KEY", "HF_TOKEN", "HF_API_KEY" }, LlmEnvironmentVariables.For("huggingface")!.ApiKey);
        Assert.Contains("OLLAMA_HOST", LlmEnvironmentVariables.For("ollama")!.Endpoint);
        Assert.Contains("OLLAMA_CLOUD_ENDPOINT", LlmEnvironmentVariables.For("ollamacloud")!.Endpoint);
        Assert.Equal(new[] { "OLLAMA_AUTH_MODE" }, LlmEnvironmentVariables.For("ollama")!.AuthMode);
        Assert.Equal(new[] { "OLLAMA_AUTH_HEADER" }, LlmEnvironmentVariables.For("ollama")!.HeaderName);
    }
}
