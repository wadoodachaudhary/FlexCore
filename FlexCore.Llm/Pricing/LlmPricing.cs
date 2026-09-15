namespace Fx.ControlKit.Llm.Pricing;

/// <summary>USD per million tokens.</summary>
public readonly record struct ModelPrice(double InputPerMTok, double OutputPerMTok);

/// <summary>The table entry that matched (longest-prefix) and its price.</summary>
public readonly record struct PricingMatch(string Key, ModelPrice Price);

public interface ILlmPricing
{
    /// <summary>Price for a model; local providers return $0 under the key "local"; unknown hosted models return null so a UI can show "—" rather than a misleading $0.</summary>
    PricingMatch? Lookup(ModelRef model);

    /// <summary>Estimated cost in USD, cache-aware; null when the model is unknown.</summary>
    double? EstimateCostUsd(ModelRef model, LlmUsage usage);
}

/// <summary>
/// Static price table. Keys are prefix-matched, longest prefix wins;
/// provider-qualified keys (<c>groq:…</c>, <c>azureopenai:…</c>) take
/// precedence over bare model prefixes where the same base model is priced
/// differently per host. Last refreshed 2026-04-27.
/// </summary>
public sealed class LlmPricing : ILlmPricing
{
    public static readonly LlmPricing Default = new();

    private static readonly HashSet<string> LocalProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        ProviderKeys.Ollama, ProviderKeys.OllamaCloud,
    };

    private static readonly Dictionary<string, ModelPrice> PricesByPrefix = new(StringComparer.OrdinalIgnoreCase)
    {
        // Azure OpenAI (East US 2 regional rates)
        ["azureopenai:gpt-4.1-mini"] = new(0.44, 1.76),
        ["azureopenai:gpt-4.1-nano"] = new(0.11, 0.44),
        ["azureopenai:gpt-4.1"]      = new(2.20, 8.80),

        // Azure AI Foundry deployments
        ["azurefoundry:phi-4-mini"]                             = new(0.075, 0.30),
        ["azurefoundry:phi-4-mini-instruct"]                    = new(0.075, 0.30),
        ["azurefoundry:deepseek-v3.2"]                          = new(0.58, 1.68),
        ["azurefoundry:deepseek-v3.2-exp"]                      = new(0.58, 1.68),
        ["azurefoundry:llama-3.3-70b-instruct"]                 = new(0.71, 0.71),
        ["azurefoundry:llama-4-maverick-17b-128e-instruct-fp8"] = new(0.25, 1.00),

        // Hugging Face router ids (provider suffix matters here)
        ["huggingface:qwen/qwen3-32b:nscale"]                      = new(0.08, 0.25),
        ["huggingface:qwen/qwen3-coder-30b-a3b-instruct:ovhcloud"] = new(0.07, 0.26),
        ["huggingface:qwen/qwen3-235b-a22b:nscale"]                = new(0.20, 0.60),
        ["huggingface:alpindale/wizardlm-2-8x22b:novita"]          = new(0.62, 0.62),

        // Groq
        ["groq:llama-3.1-8b-instant"]    = new(0.05, 0.08),
        ["groq:llama-3.3-70b-versatile"] = new(0.59, 0.79),
        ["groq:openai/gpt-oss-120b"]     = new(0.15, 0.60),
        ["groq:openai/gpt-oss-20b"]      = new(0.075, 0.30),

        // Mistral AI
        ["mistral:mistral-large-latest"] = new(0.50, 1.50),
        ["mistral:mistral-small-latest"] = new(0.15, 0.60),
        ["mistral:codestral-latest"]     = new(0.30, 0.90),
        ["mistral:ministral-14b-latest"] = new(0.20, 0.20),
        ["mistral:ministral-8b-latest"]  = new(0.15, 0.15),

        // Anthropic
        ["claude-opus-4-7"]   = new(15.00, 75.00),
        ["claude-opus-4-6"]   = new(15.00, 75.00),
        ["claude-opus-4-5"]   = new(15.00, 75.00),
        ["claude-opus-4-1"]   = new(15.00, 75.00),
        ["claude-opus-4"]     = new(15.00, 75.00),
        ["claude-sonnet-4-6"] = new(3.00, 15.00),
        ["claude-sonnet-4-5"] = new(3.00, 15.00),
        ["claude-sonnet-4"]   = new(3.00, 15.00),
        ["claude-3-7-sonnet"] = new(3.00, 15.00),
        ["claude-3-5-sonnet"] = new(3.00, 15.00),
        ["claude-haiku-4-5"]  = new(1.00,  5.00),
        ["claude-3-5-haiku"]  = new(0.80,  4.00),
        ["claude-3-haiku"]    = new(0.25,  1.25),

        // OpenAI
        ["gpt-5.5"]           = new(5.00, 30.00),
        ["gpt-5.4-mini"]      = new(0.75,  4.50),
        ["gpt-5.4"]           = new(2.50, 15.00),
        ["gpt-5.2-pro"]       = new(21.0, 168.00),
        ["gpt-5.2-codex"]     = new(1.75, 14.00),
        ["gpt-5.2-chat"]      = new(1.75, 14.00),
        ["gpt-5.2"]           = new(1.75, 14.00),
        ["gpt-5.1-codex-max"] = new(1.25, 10.00),
        ["gpt-5.1-codex"]     = new(1.25, 10.00),
        ["gpt-5.1-chat"]      = new(1.25, 10.00),
        ["gpt-5.1"]           = new(1.25, 10.00),
        ["gpt-5-codex"]       = new(1.25, 10.00),
        ["gpt-5-pro"]         = new(15.0, 120.00),
        ["gpt-5-mini"]        = new(0.25,  2.00),
        ["gpt-5-nano"]        = new(0.05,  0.40),
        ["gpt-5-chat"]        = new(1.25, 10.00),
        ["gpt-5"]             = new(1.25, 10.00),
        ["gpt-4.1-mini"]      = new(0.40,  1.60),
        ["gpt-4.1-nano"]      = new(0.10,  0.40),
        ["gpt-4.1"]           = new(2.00,  8.00),
        ["gpt-4o-mini"]       = new(0.15,  0.60),
        ["gpt-4o"]            = new(2.50, 10.00),
        ["gpt-4-turbo"]       = new(10.0, 30.00),
        ["gpt-4"]             = new(30.0, 60.00),
        ["gpt-3.5"]           = new(0.50,  1.50),
        ["o1-mini"]           = new(1.10,  4.40),
        ["o1"]                = new(15.0, 60.00),
        ["text-embedding-3"]  = new(0.02,  0.00),

        // xAI Grok
        ["grok-4.20-reasoning"]         = new(2.00,  6.00),
        ["grok-4.20-non-reasoning"]     = new(2.00,  6.00),
        ["grok-4-1-fast-reasoning"]     = new(0.20,  0.50),
        ["grok-4-1-fast-non-reasoning"] = new(0.20,  0.50),
        ["grok-4-fast-reasoning"]       = new(0.20,  0.50),
        ["grok-4-fast-non-reasoning"]   = new(0.20,  0.50),
        ["grok-4-0709"]                 = new(3.00, 15.00),
        ["grok-4"]                      = new(3.00, 15.00),
        ["grok-3-mini"]                 = new(0.30,  0.50),
        ["grok-3"]                      = new(3.00, 15.00),
        ["grok-code-fast-1"]            = new(0.20,  1.50),
        ["grok-code-fast"]              = new(0.20,  1.50),
        ["grok-2"]                      = new(2.00, 10.00),
        ["grok-beta"]                   = new(5.00, 15.00),
        ["grok"]                        = new(3.00, 15.00),

        // Google Gemini
        ["gemini-2.5-pro"]        = new(1.25, 10.00),
        ["gemini-2.5-flash"]      = new(0.30,  2.50),
        ["gemini-2.5-flash-lite"] = new(0.10,  0.40),
        ["gemini-1.5"]            = new(1.25,  5.00),
    };

    private static readonly string[] OrderedPrefixes = PricesByPrefix.Keys
        .OrderByDescending(k => k.Length)
        .ToArray();

    private static readonly ModelPrice FreePrice = new(0.00, 0.00);

    // Azure deployment names the live apps use, mapped to their base model.
    private static readonly (string Needle, string CanonicalKey)[] ModelAliasRules =
    {
        ("ghostwriter-gpt41",          "azureopenai:gpt-4.1"),
        ("ghostwriter-phi4mini",       "azurefoundry:phi-4-mini-instruct"),
        ("ghostwriter-deepseekv32",    "azurefoundry:deepseek-v3.2"),
        ("ghostwriter-llama33-70b",    "azurefoundry:llama-3.3-70b-instruct"),
        ("ghostwriter-llama4maverick", "azurefoundry:llama-4-maverick-17b-128e-instruct-fp8"),
    };

    public PricingMatch? Lookup(ModelRef model)
    {
        var providerKey = ProviderKeys.Normalize(model.Provider) ?? string.Empty;
        if (LocalProviders.Contains(providerKey))
        {
            return new PricingMatch("local", FreePrice);
        }

        foreach (var lookupKey in BuildLookupKeys(providerKey, model.Model))
        {
            foreach (var prefix in OrderedPrefixes)
            {
                if (lookupKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return new PricingMatch(prefix, PricesByPrefix[prefix]);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Cache multipliers differ by family: Anthropic bills cache writes at
    /// 1.25x and reads at 0.1x of the input rate; OpenAI / Azure OpenAI bill
    /// cached input at 0.25x; everything else defaults to base-rate writes
    /// and 0.1x reads.
    /// </summary>
    public double? EstimateCostUsd(ModelRef model, LlmUsage usage)
    {
        var match = Lookup(model);
        if (match is null) return null;

        var (cacheWrite, cacheRead) = CacheMultipliers(ProviderKeys.Normalize(model.Provider) ?? string.Empty, match.Value.Key);
        var inRate = match.Value.Price.InputPerMTok / 1_000_000.0;
        var outRate = match.Value.Price.OutputPerMTok / 1_000_000.0;

        return usage.Input * inRate
             + (usage.CacheWrite ?? 0) * inRate * cacheWrite
             + (usage.CacheRead ?? 0) * inRate * cacheRead
             + usage.Output * outRate;
    }

    private static IEnumerable<string> BuildLookupKeys(string providerKey, string? model)
    {
        var modelKey = string.IsNullOrWhiteSpace(model) ? string.Empty : model.Trim().ToLowerInvariant();
        if (modelKey.Length == 0) yield break;

        var emitted = new List<string>();
        void Emit(string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !emitted.Contains(value, StringComparer.OrdinalIgnoreCase)) emitted.Add(value);
        }

        if (providerKey.Length > 0) Emit($"{providerKey}:{modelKey}");
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

        if (providerKey == ProviderKeys.AzureOpenAi)
        {
            if (modelKey.StartsWith("gpt-4.1-mini", StringComparison.OrdinalIgnoreCase)) Emit("azureopenai:gpt-4.1-mini");
            else if (modelKey.StartsWith("gpt-4.1-nano", StringComparison.OrdinalIgnoreCase)) Emit("azureopenai:gpt-4.1-nano");
            else if (modelKey.StartsWith("gpt-4.1", StringComparison.OrdinalIgnoreCase)) Emit("azureopenai:gpt-4.1");
        }

        if (providerKey == ProviderKeys.AzureFoundry)
        {
            if (modelKey.StartsWith("phi-4-mini", StringComparison.OrdinalIgnoreCase)) Emit("azurefoundry:phi-4-mini-instruct");
            if (modelKey.StartsWith("deepseek-v3.2", StringComparison.OrdinalIgnoreCase)) Emit("azurefoundry:deepseek-v3.2");
            if (modelKey.StartsWith("llama-3.3-70b", StringComparison.OrdinalIgnoreCase)) Emit("azurefoundry:llama-3.3-70b-instruct");
            if (modelKey.StartsWith("llama-4-maverick-17b", StringComparison.OrdinalIgnoreCase)) Emit("azurefoundry:llama-4-maverick-17b-128e-instruct-fp8");
        }

        foreach (var key in emitted) yield return key;
    }

    private static (double CacheWrite, double CacheRead) CacheMultipliers(string providerKey, string lookupKey)
    {
        if (providerKey is ProviderKeys.OpenAi or ProviderKeys.AzureOpenAi ||
            lookupKey.StartsWith("openai:", StringComparison.OrdinalIgnoreCase) ||
            lookupKey.StartsWith("azureopenai:", StringComparison.OrdinalIgnoreCase))
        {
            return (1.0, 0.25);
        }

        if (providerKey == ProviderKeys.Anthropic || lookupKey.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
        {
            return (1.25, 0.10);
        }

        return (1.0, 0.10);
    }
}
