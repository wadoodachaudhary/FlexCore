namespace Fx.ControlKit.Llm.Pricing;

/// <summary>Planning budget for one model: a safe context size and the output room to keep free.</summary>
public readonly record struct ContextBudget(string Key, int EstimatedContextTokens, int ReservedOutputTokens)
{
    public int AvailableInputTokens => Math.Max(0, EstimatedContextTokens - ReservedOutputTokens);
}

public interface ILlmContextBudget
{
    ContextBudget Lookup(ModelRef model);

    /// <summary>Rough English token count (chars / 4). Use provider usage when you have it.</summary>
    int EstimateTokens(string? text);

    /// <summary>True when <paramref name="text"/> is expected to fit alongside <paramref name="reservedOutputTokens"/> (or the budget's own reserve).</summary>
    bool Fits(ModelRef model, string? text, int? reservedOutputTokens = null);
}

/// <summary>
/// Conservative, ballpark context-window estimates for proactive chunking —
/// intentionally below vendor marketing numbers. A <see cref="ModelCatalog"/>
/// entry wins when it knows the model; otherwise the longest matching prefix
/// here, then a per-provider default, then 24K/3K.
/// </summary>
public sealed class LlmContextBudget : ILlmContextBudget
{
    public static readonly LlmContextBudget Default = new();

    private static readonly Dictionary<string, ContextBudget> BudgetsByPrefix =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // OpenAI / Azure OpenAI
        ["azureopenai:gpt-4.1"]   = new("azureopenai:gpt-4.1",   256_000, 8_000),
        ["azureopenai:gpt-5"]     = new("azureopenai:gpt-5",     256_000, 8_000),
        ["openai:gpt-4.1"]        = new("openai:gpt-4.1",        256_000, 8_000),
        ["openai:gpt-5"]          = new("openai:gpt-5",          256_000, 8_000),
        ["openai:o1"]             = new("openai:o1",             128_000, 8_000),
        ["openai:o4-mini"]        = new("openai:o4-mini",        128_000, 8_000),

        // Anthropic
        ["anthropic:claude-opus"]   = new("anthropic:claude-opus",   200_000, 8_000),
        ["anthropic:claude-sonnet"] = new("anthropic:claude-sonnet", 200_000, 8_000),
        ["anthropic:claude-haiku"]  = new("anthropic:claude-haiku",  200_000, 6_000),

        // Gemini
        ["gemini:gemini-2.5-pro"]   = new("gemini:gemini-2.5-pro",   512_000, 8_000),
        ["gemini:gemini-2.5-flash"] = new("gemini:gemini-2.5-flash", 512_000, 8_000),
        ["gemini:gemini-1.5"]       = new("gemini:gemini-1.5",       256_000, 8_000),

        // xAI / Groq / Mistral API
        ["xai:grok-4"]                     = new("xai:grok-4",                     256_000, 8_000),
        ["xai:grok-3"]                     = new("xai:grok-3",                     128_000, 8_000),
        ["groq:llama-3.3-70b-versatile"]   = new("groq:llama-3.3-70b-versatile",   128_000, 6_000),
        ["groq:openai/gpt-oss-120b"]       = new("groq:openai/gpt-oss-120b",       128_000, 6_000),
        ["groq:openai/gpt-oss-20b"]        = new("groq:openai/gpt-oss-20b",        128_000, 6_000),
        ["groq:llama-3.1-8b-instant"]      = new("groq:llama-3.1-8b-instant",      128_000, 4_000),
        ["mistral:codestral"]              = new("mistral:codestral",              256_000, 8_000),
        ["mistral:mistral-large"]          = new("mistral:mistral-large",          128_000, 8_000),
        ["mistral:mistral-small"]          = new("mistral:mistral-small",          128_000, 6_000),
        ["mistral:ministral"]              = new("mistral:ministral",               96_000, 4_000),

        // Azure AI Foundry deployments
        ["azurefoundry:phi-4-mini"]                             = new("azurefoundry:phi-4-mini",                             128_000, 6_000),
        ["azurefoundry:phi-4-mini-instruct"]                    = new("azurefoundry:phi-4-mini-instruct",                    128_000, 6_000),
        ["azurefoundry:deepseek-v3.2"]                          = new("azurefoundry:deepseek-v3.2",                          128_000, 8_000),
        ["azurefoundry:llama-3.3-70b-instruct"]                 = new("azurefoundry:llama-3.3-70b-instruct",                 128_000, 8_000),
        ["azurefoundry:llama-4-maverick-17b-128e-instruct-fp8"] = new("azurefoundry:llama-4-maverick-17b-128e-instruct-fp8", 128_000, 8_000),

        // Hugging Face router / dedicated endpoints
        ["huggingface:qwen/qwen3-235b"]                         = new("huggingface:qwen/qwen3-235b",                         128_000, 8_000),
        ["huggingface:qwen/qwen3-coder-30b-a3b-instruct"]       = new("huggingface:qwen/qwen3-coder-30b-a3b-instruct",       128_000, 8_000),
        ["huggingface:qwen/qwen3-32b"]                          = new("huggingface:qwen/qwen3-32b",                          128_000, 8_000),
        ["huggingface:alpindale/wizardlm-2-8x22b"]              = new("huggingface:alpindale/wizardlm-2-8x22b",               64_000, 6_000),
        ["huggingface:moonshotai/kimi-k2.6"]                    = new("huggingface:moonshotai/kimi-k2.6",                     64_000, 4_000),
        ["huggingface:maziyarpanahi/calme-3.2-instruct-78b"]    = new("huggingface:maziyarpanahi/calme-3.2-instruct-78b",    32_000, 4_000),

        // Ollama families (local or cloud daemon)
        ["ollama:qwen3"]            = new("ollama:qwen3",            32_000, 4_000),
        ["ollama:qwen2.5-coder"]    = new("ollama:qwen2.5-coder",    32_000, 4_000),
        ["ollama:deepseek-r1"]      = new("ollama:deepseek-r1",      32_000, 4_000),
        ["ollama:mistral-nemo"]     = new("ollama:mistral-nemo",     32_000, 4_000),
        ["ollama:mistral"]          = new("ollama:mistral",          32_000, 4_000),
        ["ollama:wizardlm2"]        = new("ollama:wizardlm2",        16_000, 3_000),
        ["ollama:wizardlm"]         = new("ollama:wizardlm",         16_000, 3_000),
        ["ollama:phi3:medium"]      = new("ollama:phi3:medium",      16_000, 3_000),
        ["ollama:phi3"]             = new("ollama:phi3",             16_000, 3_000),
        ["ollama:phi4"]             = new("ollama:phi4",             32_000, 4_000),
        ["ollama:llama3.1"]         = new("ollama:llama3.1",         64_000, 4_000),
        ["ollama:llama3.2"]         = new("ollama:llama3.2",         64_000, 4_000),
        ["ollama:gemma3"]           = new("ollama:gemma3",           32_000, 4_000),
        ["ollama:gemma2"]           = new("ollama:gemma2",           32_000, 4_000),
        // Gemma 4: local quantised builds cap around 64K; the cloud-proxied
        // 31b variant reports a 128K window.
        ["ollama:gemma4:31b-cloud"] = new("ollama:gemma4:31b-cloud", 128_000, 6_000),
        ["ollama:gemma4"]           = new("ollama:gemma4",            64_000, 4_000),
    };

    private static readonly Dictionary<string, ContextBudget> ProviderDefaults =
        new(StringComparer.OrdinalIgnoreCase)
    {
        [ProviderKeys.OpenAi]       = new("openai:default",       128_000, 6_000),
        [ProviderKeys.AzureOpenAi]  = new("azureopenai:default",  128_000, 6_000),
        [ProviderKeys.AzureFoundry] = new("azurefoundry:default", 128_000, 6_000),
        [ProviderKeys.Anthropic]    = new("anthropic:default",    200_000, 8_000),
        [ProviderKeys.Gemini]       = new("gemini:default",       256_000, 8_000),
        [ProviderKeys.XAi]          = new("xai:default",          128_000, 6_000),
        [ProviderKeys.Groq]         = new("groq:default",          96_000, 4_000),
        [ProviderKeys.Mistral]      = new("mistral:default",       96_000, 4_000),
        [ProviderKeys.HuggingFace]  = new("huggingface:default",   64_000, 4_000),
        [ProviderKeys.Ollama]       = new("ollama:default",        24_000, 3_000),
        [ProviderKeys.OllamaCloud]  = new("ollamacloud:default",   24_000, 3_000),
    };

    private static readonly string[] OrderedPrefixes = BudgetsByPrefix.Keys
        .OrderByDescending(k => k.Length)
        .ToArray();

    private static readonly (string Needle, string CanonicalKey)[] ModelAliasRules =
    {
        ("ghostwriter-gpt41",          "azureopenai:gpt-4.1"),
        ("ghostwriter-gpt41nano",      "azureopenai:gpt-4.1-nano"),
        ("ghostwriter-phi4mini",       "azurefoundry:phi-4-mini-instruct"),
        ("ghostwriter-deepseekv32",    "azurefoundry:deepseek-v3.2"),
        ("ghostwriter-llama33-70b",    "azurefoundry:llama-3.3-70b-instruct"),
        ("ghostwriter-llama4maverick", "azurefoundry:llama-4-maverick-17b-128e-instruct-fp8"),
    };

    public ContextBudget Lookup(ModelRef model)
    {
        var providerKey = ProviderKeys.Normalize(model.Provider) ?? string.Empty;
        var modelKey = string.IsNullOrWhiteSpace(model.Model) ? string.Empty : model.Model.Trim().ToLowerInvariant();

        if (ModelCatalog.Find(new ModelRef(providerKey, modelKey)) is { ContextTokens: > 0 } known)
        {
            return new ContextBudget(known.Ref.ToString(), known.ContextTokens.Value, Math.Min(known.MaxOutputTokens ?? 4_000, 8_000));
        }

        foreach (var lookupKey in BuildLookupKeys(providerKey, modelKey))
        {
            foreach (var prefix in OrderedPrefixes)
            {
                if (lookupKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return BudgetsByPrefix[prefix];
                }
            }
        }

        if (providerKey.Length > 0 && ProviderDefaults.TryGetValue(providerKey, out var providerBudget))
        {
            return providerBudget;
        }

        return new ContextBudget("default", 24_000, 3_000);
    }

    public int EstimateTokens(string? text)
        => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    public bool Fits(ModelRef model, string? text, int? reservedOutputTokens = null)
    {
        var budget = Lookup(model);
        var reserve = reservedOutputTokens ?? budget.ReservedOutputTokens;
        return EstimateTokens(text) <= Math.Max(0, budget.EstimatedContextTokens - reserve);
    }

    private static IEnumerable<string> BuildLookupKeys(string providerKey, string modelKey)
    {
        if (modelKey.Length == 0) yield break;

        var emitted = new List<string>();
        void Emit(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !emitted.Contains(value, StringComparer.OrdinalIgnoreCase)) emitted.Add(value);
        }

        if (providerKey.Length > 0) Emit($"{providerKey}:{modelKey}");
        // The cloud daemon serves the same tags as a local one.
        if (providerKey == ProviderKeys.OllamaCloud) Emit($"{ProviderKeys.Ollama}:{modelKey}");
        Emit(modelKey);

        foreach (var (needle, canonicalKey) in ModelAliasRules)
        {
            if (modelKey.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                Emit(canonicalKey);
                var split = canonicalKey.IndexOf(':');
                if (split >= 0 && split + 1 < canonicalKey.Length) Emit(canonicalKey[(split + 1)..]);
            }
        }

        foreach (var key in emitted) yield return key;
    }
}
