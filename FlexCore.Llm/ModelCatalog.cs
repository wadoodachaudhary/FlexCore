namespace Fx.ControlKit.Llm;

/// <summary>
/// Built-in list of models the FlexCore applications use, with conservative
/// context-window and output caps for planning (deliberately below vendor
/// maxima). Live listings from <see cref="ILlmProvider.ListModelsAsync"/>
/// are the source of truth for what is actually available; this table fills
/// in limits and display names and lets <see cref="ILlmClient"/> infer a
/// provider from a bare model id.
/// </summary>
public static class ModelCatalog
{
    private const LlmCapabilities Text = LlmCapabilities.Chat | LlmCapabilities.Streaming | LlmCapabilities.JsonMode | LlmCapabilities.Tools;
    private const LlmCapabilities Frontier = Text | LlmCapabilities.JsonSchema | LlmCapabilities.Vision | LlmCapabilities.Reasoning;

    private static ModelInfo M(string provider, string id, string display, int context, int maxOutput, LlmCapabilities caps = Frontier)
        => new(provider, id) { DisplayName = display, ContextTokens = context, MaxOutputTokens = maxOutput, Capabilities = caps };

    public static readonly IReadOnlyList<ModelInfo> Known = new[]
    {
        // Anthropic
        M(ProviderKeys.Anthropic, "claude-opus-4-7", "Claude Opus 4.7", 200_000, 16_384, Frontier | LlmCapabilities.PromptCaching),
        M(ProviderKeys.Anthropic, "claude-sonnet-4-6", "Claude Sonnet 4.6", 200_000, 16_384, Frontier | LlmCapabilities.PromptCaching),
        M(ProviderKeys.Anthropic, "claude-haiku-4-5", "Claude Haiku 4.5", 200_000, 8_192, Frontier | LlmCapabilities.PromptCaching),
        M(ProviderKeys.Anthropic, "claude-opus-4-20250514", "Claude Opus 4 (2025-05)", 200_000, 16_384, Frontier | LlmCapabilities.PromptCaching),
        M(ProviderKeys.Anthropic, "claude-sonnet-4-20250514", "Claude Sonnet 4 (2025-05)", 200_000, 16_384, Frontier | LlmCapabilities.PromptCaching),

        // OpenAI
        M(ProviderKeys.OpenAi, "gpt-5.5", "GPT-5.5", 256_000, 16_384, Frontier | LlmCapabilities.PromptCaching),
        M(ProviderKeys.OpenAi, "gpt-5.4", "GPT-5.4", 256_000, 16_384, Frontier | LlmCapabilities.PromptCaching),
        M(ProviderKeys.OpenAi, "gpt-5.4-mini", "GPT-5.4 Mini", 256_000, 16_384, Frontier | LlmCapabilities.PromptCaching),
        M(ProviderKeys.OpenAi, "gpt-5.4-nano", "GPT-5.4 Nano", 256_000, 16_384, Frontier | LlmCapabilities.PromptCaching),
        M(ProviderKeys.OpenAi, "gpt-5.2-codex", "GPT-5.2 Codex", 256_000, 16_384, Frontier | LlmCapabilities.PromptCaching),
        M(ProviderKeys.OpenAi, "gpt-4.1", "GPT-4.1", 256_000, 8_000, Text | LlmCapabilities.JsonSchema | LlmCapabilities.Vision | LlmCapabilities.PromptCaching),
        M(ProviderKeys.OpenAi, "gpt-4.1-mini", "GPT-4.1 Mini", 256_000, 8_000, Text | LlmCapabilities.JsonSchema | LlmCapabilities.Vision | LlmCapabilities.PromptCaching),
        M(ProviderKeys.OpenAi, "gpt-4o-mini", "GPT-4o Mini", 128_000, 8_000, Text | LlmCapabilities.JsonSchema | LlmCapabilities.Vision),
        M(ProviderKeys.OpenAi, "gpt-image-1", "GPT Image 1", 0, 0, LlmCapabilities.ImageGeneration),
        M(ProviderKeys.OpenAi, "text-embedding-3-small", "Text Embedding 3 Small", 8_191, 0, LlmCapabilities.Embeddings),
        M(ProviderKeys.OpenAi, "text-embedding-3-large", "Text Embedding 3 Large", 8_191, 0, LlmCapabilities.Embeddings),

        // Google Gemini
        M(ProviderKeys.Gemini, "gemini-2.5-pro", "Gemini 2.5 Pro", 512_000, 16_384),
        M(ProviderKeys.Gemini, "gemini-2.5-flash", "Gemini 2.5 Flash", 512_000, 8_192),
        M(ProviderKeys.Gemini, "gemini-2.5-flash-lite", "Gemini 2.5 Flash Lite", 512_000, 8_192),
        M(ProviderKeys.Gemini, "gemini-embedding-001", "Gemini Embedding", 2_048, 0, LlmCapabilities.Embeddings),

        // xAI
        M(ProviderKeys.XAi, "grok-4", "Grok 4", 256_000, 8_000),
        M(ProviderKeys.XAi, "grok-4-fast-reasoning", "Grok 4 Fast (reasoning)", 256_000, 8_000),
        M(ProviderKeys.XAi, "grok-code-fast-1", "Grok Code Fast 1", 256_000, 8_000),
        M(ProviderKeys.XAi, "grok-4.20-reasoning", "Grok 4.20 (reasoning)", 256_000, 8_000),

        // Groq
        M(ProviderKeys.Groq, "llama-3.3-70b-versatile", "Llama 3.3 70B Versatile", 128_000, 6_000, Text),
        M(ProviderKeys.Groq, "llama-3.1-8b-instant", "Llama 3.1 8B Instant", 128_000, 4_000, Text),
        M(ProviderKeys.Groq, "openai/gpt-oss-120b", "GPT-OSS 120B", 128_000, 6_000, Text | LlmCapabilities.Reasoning),
        M(ProviderKeys.Groq, "openai/gpt-oss-20b", "GPT-OSS 20B", 128_000, 6_000, Text | LlmCapabilities.Reasoning),

        // Mistral AI
        M(ProviderKeys.Mistral, "mistral-large-latest", "Mistral Large", 128_000, 8_000, Text | LlmCapabilities.JsonSchema),
        M(ProviderKeys.Mistral, "mistral-small-latest", "Mistral Small", 128_000, 6_000, Text | LlmCapabilities.JsonSchema),
        M(ProviderKeys.Mistral, "codestral-latest", "Codestral", 256_000, 8_000, Text | LlmCapabilities.JsonSchema),
        M(ProviderKeys.Mistral, "ministral-14b-latest", "Ministral 14B", 96_000, 4_000, Text),
        M(ProviderKeys.Mistral, "ministral-8b-latest", "Ministral 8B", 96_000, 4_000, Text),
        M(ProviderKeys.Mistral, "mistral-embed", "Mistral Embed", 8_192, 0, LlmCapabilities.Embeddings),

        // Hugging Face router
        M(ProviderKeys.HuggingFace, "Qwen/Qwen3-32B:nscale", "Qwen3 32B (nscale)", 128_000, 8_000, Text),
        M(ProviderKeys.HuggingFace, "Qwen/Qwen3-Coder-30B-A3B-Instruct:ovhcloud", "Qwen3 Coder 30B (ovhcloud)", 128_000, 8_000, Text),
        M(ProviderKeys.HuggingFace, "Qwen/Qwen3-235B-A22B:nscale", "Qwen3 235B (nscale)", 128_000, 8_000, Text),
        M(ProviderKeys.HuggingFace, "alpindale/WizardLM-2-8x22B:novita", "WizardLM-2 8x22B (novita)", 64_000, 6_000, Text),
        M(ProviderKeys.HuggingFace, "moonshotai/Kimi-K2.6:novita", "Kimi K2.6 (novita)", 64_000, 4_000, Text),

        // Ollama — local tags
        M(ProviderKeys.Ollama, "deepseek-coder:33b", "DeepSeek Coder 33B", 16_000, 4_096, Text),
        M(ProviderKeys.Ollama, "deepseek-r1:14b", "DeepSeek R1 14B", 32_000, 4_096, Text | LlmCapabilities.Reasoning),
        M(ProviderKeys.Ollama, "deepseek-r1:32b", "DeepSeek R1 32B", 32_000, 4_096, Text | LlmCapabilities.Reasoning),
        M(ProviderKeys.Ollama, "qwen2.5-coder:32b", "Qwen 2.5 Coder 32B", 32_000, 4_096, Text),
        M(ProviderKeys.Ollama, "qwen3:8b", "Qwen 3 8B", 32_000, 4_096, Text | LlmCapabilities.Reasoning),
        M(ProviderKeys.Ollama, "qwen3:32b", "Qwen 3 32B", 32_000, 4_096, Text | LlmCapabilities.Reasoning),
        M(ProviderKeys.Ollama, "codellama:34b", "Code Llama 34B", 16_000, 4_096, Text),
        M(ProviderKeys.Ollama, "llama3.1:8b", "Llama 3.1 8B", 64_000, 4_096, Text),
        M(ProviderKeys.Ollama, "mistral:7b", "Mistral 7B", 32_000, 4_096, Text),
        M(ProviderKeys.Ollama, "gemma3:4b", "Gemma 3 4B", 32_000, 4_096, Text | LlmCapabilities.Vision),
        M(ProviderKeys.Ollama, "phi3:medium", "Phi-3 Medium", 16_000, 3_000, Text),
        M(ProviderKeys.Ollama, "wizardlm2:7b", "WizardLM-2 7B", 16_000, 3_000, Text),
        // Ollama — cloud-proxied tags served by the local daemon via ollama.com
        M(ProviderKeys.Ollama, "deepseek-v4-pro:cloud", "DeepSeek V4 Pro (cloud)", 64_000, 8_192, Text | LlmCapabilities.Reasoning),
        M(ProviderKeys.Ollama, "gemma4:31b-cloud", "Gemma 4 31B (cloud)", 128_000, 8_192, Text | LlmCapabilities.Vision),
    };

    private static readonly Dictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        [ProviderKeys.Anthropic] = "claude-sonnet-4-6",
        [ProviderKeys.OpenAi] = "gpt-5.4",
        [ProviderKeys.Gemini] = "gemini-2.5-pro",
        [ProviderKeys.XAi] = "grok-4",
        [ProviderKeys.Groq] = "llama-3.3-70b-versatile",
        [ProviderKeys.Mistral] = "mistral-large-latest",
        [ProviderKeys.HuggingFace] = "Qwen/Qwen3-32B:nscale",
        [ProviderKeys.Ollama] = "deepseek-v4-pro:cloud",
    };

    public static IEnumerable<ModelInfo> ForProvider(string providerKey)
    {
        var key = ProviderKeys.Normalize(providerKey);
        if (key is null) return Array.Empty<ModelInfo>();
        // The cloud daemon serves the same tags as a local one.
        var lookup = key == ProviderKeys.OllamaCloud ? ProviderKeys.Ollama : key;
        return Known.Where(m => m.Provider == lookup).Select(m => lookup == key ? m : m with { Provider = key });
    }

    /// <summary>Exact (case-insensitive) match on provider + id. A reference without a provider matches on id alone.</summary>
    public static ModelInfo? Find(ModelRef model)
    {
        if (!model.HasModel) return null;
        var candidates = model.HasProvider ? ForProvider(model.Provider) : Known;
        return candidates.FirstOrDefault(m => string.Equals(m.Id, model.Model, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The model a provider falls back to when neither the request nor its configuration names one.</summary>
    public static string? DefaultModel(string providerKey)
    {
        var key = ProviderKeys.Normalize(providerKey);
        if (key is null) return null;
        if (key == ProviderKeys.OllamaCloud) key = ProviderKeys.Ollama;
        return Defaults.TryGetValue(key, out var model) ? model : null;
    }

    /// <summary>
    /// Best-effort provider for a bare model id: a catalog hit wins, then the
    /// vendor naming conventions (claude-* → anthropic, gpt-*/o-series →
    /// openai, gemini-* → gemini, grok-* → xai, *-latest/codestral → mistral,
    /// anything with an Ollama-style <c>name:tag</c> → ollama). Null when
    /// nothing fits.
    /// </summary>
    public static string? InferProvider(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var id = model.Trim();
        if (Find(new ModelRef(string.Empty, id)) is { } known) return known.Provider;

        var lower = id.ToLowerInvariant();
        if (lower.StartsWith("claude", StringComparison.Ordinal)) return ProviderKeys.Anthropic;
        if (lower.StartsWith("gemini", StringComparison.Ordinal) || lower.StartsWith("imagen", StringComparison.Ordinal)) return ProviderKeys.Gemini;
        if (lower.StartsWith("grok", StringComparison.Ordinal)) return ProviderKeys.XAi;
        if (lower.StartsWith("gpt-", StringComparison.Ordinal) || lower.StartsWith("o1", StringComparison.Ordinal)
            || lower.StartsWith("o3", StringComparison.Ordinal) || lower.StartsWith("o4", StringComparison.Ordinal)
            || lower.StartsWith("text-embedding", StringComparison.Ordinal) || lower.StartsWith("dall-e", StringComparison.Ordinal)
            || lower.StartsWith("chatgpt", StringComparison.Ordinal) || lower.Contains("codex", StringComparison.Ordinal))
        {
            return ProviderKeys.OpenAi;
        }

        if (lower.Contains('/') && lower.Contains(':')) return ProviderKeys.HuggingFace;
        if (lower.Contains(':')) return ProviderKeys.Ollama;
        if (lower.EndsWith("-latest", StringComparison.Ordinal) || lower.StartsWith("codestral", StringComparison.Ordinal)
            || lower.StartsWith("ministral", StringComparison.Ordinal) || lower.StartsWith("pixtral", StringComparison.Ordinal)
            || lower.StartsWith("magistral", StringComparison.Ordinal) || lower == "mistral-embed")
        {
            return ProviderKeys.Mistral;
        }

        if (lower.StartsWith("openai/gpt-oss", StringComparison.Ordinal) || lower.EndsWith("-versatile", StringComparison.Ordinal) || lower.EndsWith("-instant", StringComparison.Ordinal))
        {
            return ProviderKeys.Groq;
        }

        if (lower.StartsWith("llama", StringComparison.Ordinal) || lower.StartsWith("qwen", StringComparison.Ordinal)
            || lower.StartsWith("gemma", StringComparison.Ordinal) || lower.StartsWith("phi", StringComparison.Ordinal)
            || lower.StartsWith("deepseek", StringComparison.Ordinal) || lower.StartsWith("mistral", StringComparison.Ordinal)
            || lower.StartsWith("wizardlm", StringComparison.Ordinal) || lower.StartsWith("codellama", StringComparison.Ordinal))
        {
            return ProviderKeys.Ollama;
        }

        return null;
    }
}
