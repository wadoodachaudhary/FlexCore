namespace Fx.ControlKit.Llm;

/// <summary>
/// The canonical lower-case provider keys understood by <see cref="ModelRef"/>,
/// <see cref="ILlmClient.Resolve(string)"/> and the configuration section names.
/// </summary>
public static class ProviderKeys
{
    public const string OpenAi = "openai";
    public const string AzureOpenAi = "azureopenai";
    public const string AzureFoundry = "azurefoundry";
    public const string Anthropic = "anthropic";
    public const string Gemini = "gemini";
    public const string Ollama = "ollama";
    public const string OllamaCloud = "ollamacloud";
    public const string HuggingFace = "huggingface";
    public const string Groq = "groq";
    public const string XAi = "xai";
    public const string Mistral = "mistral";

    public static readonly IReadOnlyList<string> All = new[]
    {
        OpenAi, AzureOpenAi, AzureFoundry, Anthropic, Gemini, Ollama, OllamaCloud,
        HuggingFace, Groq, XAi, Mistral
    };

    // Spellings other apps (GhostWriter, Mutarjim) already use for the same
    // provider. They normalise to the canonical key so a "claude:" or
    // "cloud-ollama:" prefix keeps working unchanged.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = Anthropic,
        ["azure"] = AzureOpenAi,
        ["azure-openai"] = AzureOpenAi,
        ["azureaifoundry"] = AzureFoundry,
        ["azure-foundry"] = AzureFoundry,
        ["foundry"] = AzureFoundry,
        ["google"] = Gemini,
        ["grok"] = XAi,
        ["x-ai"] = XAi,
        ["mistralai"] = Mistral,
        ["mistralapi"] = Mistral,
        ["mistral-api"] = Mistral,
        ["hf"] = HuggingFace,
        ["hugging-face"] = HuggingFace,
        ["cloud-ollama"] = OllamaCloud,
        ["ollama-cloud"] = OllamaCloud,
    };

    /// <summary>Canonical key for <paramref name="key"/>, or null when it is not a known provider or alias.</summary>
    public static string? Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var trimmed = key.Trim();
        if (Aliases.TryGetValue(trimmed, out var alias)) return alias;
        var lower = trimmed.ToLowerInvariant();
        return All.Contains(lower) ? lower : null;
    }

    public static bool IsKnown(string? key) => Normalize(key) is not null;
}

/// <summary>
/// A provider-qualified model id, written <c>provider:model</c>
/// (<c>anthropic:claude-sonnet-4-6</c>, <c>ollama:qwen2.5-coder:32b</c>).
/// Only the first segment is treated as a provider, and only when it is a
/// known provider key or alias — an Ollama tag such as <c>qwen2.5-coder:32b</c>
/// on its own keeps its colon and parses with an empty provider that
/// <see cref="ILlmClient"/> infers from the model name.
/// </summary>
public readonly record struct ModelRef(string Provider, string Model)
{
    public bool HasProvider => !string.IsNullOrEmpty(Provider);
    public bool HasModel => !string.IsNullOrEmpty(Model);

    public static ModelRef Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        var split = trimmed.IndexOf(':');
        if (split > 0)
        {
            var provider = ProviderKeys.Normalize(trimmed[..split]);
            if (provider is not null)
            {
                return new ModelRef(provider, trimmed[(split + 1)..].Trim());
            }
        }

        return new ModelRef(string.Empty, trimmed);
    }

    public static bool TryParse(string? value, out ModelRef result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = default;
            return false;
        }

        result = Parse(value);
        return true;
    }

    public static ModelRef For(string provider, string model)
        => new(ProviderKeys.Normalize(provider) ?? provider.Trim().ToLowerInvariant(), model?.Trim() ?? string.Empty);

    public ModelRef WithProvider(string provider) => this with { Provider = ProviderKeys.Normalize(provider) ?? provider };
    public ModelRef WithModel(string model) => this with { Model = model };

    public override string ToString() => HasProvider ? $"{Provider}:{Model}" : Model;

    public static implicit operator ModelRef(string value) => Parse(value);
}
