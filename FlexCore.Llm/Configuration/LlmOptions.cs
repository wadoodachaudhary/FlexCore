namespace Fx.ControlKit.Llm.Configuration;

/// <summary>
/// Bound from the <c>Llm</c> configuration section. Each provider has its own
/// sub-section (<c>Llm:OpenAi</c>, <c>Llm:Anthropic</c>, <c>Llm:Ollama</c>,
/// <c>Llm:OllamaCloud</c>, …). Environment variables take precedence over
/// every value here — see <see cref="LlmEnvironmentVariables"/> — so
/// configuration files normally carry endpoints, models and which
/// environment variable holds the key (<see cref="LlmProviderOptions.ApiKeyVariable"/>),
/// not the key itself.
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public LlmProviderOptions OpenAi { get; set; } = new();
    public LlmProviderOptions AzureOpenAi { get; set; } = new();
    public LlmProviderOptions AzureFoundry { get; set; } = new();
    public LlmProviderOptions Anthropic { get; set; } = new();
    public LlmProviderOptions Gemini { get; set; } = new();
    public LlmProviderOptions Ollama { get; set; } = new();
    public LlmProviderOptions OllamaCloud { get; set; } = new();
    public LlmProviderOptions HuggingFace { get; set; } = new();
    public LlmProviderOptions Groq { get; set; } = new();
    public LlmProviderOptions XAi { get; set; } = new();
    public LlmProviderOptions Mistral { get; set; } = new();

    public RetryOptions Retry { get; set; } = new();

    /// <summary>Timeout for calls whose request and provider do not set one. Streaming applies it between deltas.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Applied when a request has no <see cref="ChatRequest.MaxOutputTokens"/> and the provider has none. Null omits the cap (Anthropic then uses 4096, which its API requires).</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>Applied when a request has no temperature. Null leaves it to the provider.</summary>
    public double? Temperature { get; set; }

    /// <summary>The sub-section for a canonical provider key or alias; null for unknown keys.</summary>
    public LlmProviderOptions? GetProvider(string providerKey) => ProviderKeys.Normalize(providerKey) switch
    {
        ProviderKeys.OpenAi => OpenAi,
        ProviderKeys.AzureOpenAi => AzureOpenAi,
        ProviderKeys.AzureFoundry => AzureFoundry,
        ProviderKeys.Anthropic => Anthropic,
        ProviderKeys.Gemini => Gemini,
        ProviderKeys.Ollama => Ollama,
        ProviderKeys.OllamaCloud => OllamaCloud,
        ProviderKeys.HuggingFace => HuggingFace,
        ProviderKeys.Groq => Groq,
        ProviderKeys.XAi => XAi,
        ProviderKeys.Mistral => Mistral,
        _ => null,
    };
}

public sealed class LlmProviderOptions
{
    /// <summary>Base URL (or a full endpoint URL — known leaves such as <c>/chat/completions</c>, <c>/responses</c> or <c>/api/chat</c> are recognised and stripped).</summary>
    public string? Endpoint { get; set; }

    /// <summary>Alias for <see cref="Endpoint"/> so either spelling binds.</summary>
    public string? BaseUrl
    {
        get => Endpoint;
        set => Endpoint ??= value;
    }

    /// <summary>The key itself. Prefer <see cref="ApiKeyVariable"/> or the provider's standard environment variable so secrets stay out of files.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Name of an environment variable that holds the key; checked before the provider's standard variables.</summary>
    public string? ApiKeyVariable { get; set; }

    /// <summary>Written as text so legacy spellings bind ("api-key", "managedidentity", "Auto"); parsed by <see cref="AuthModes.Parse"/>.</summary>
    public string? AuthMode { get; set; }

    /// <summary>Header for <see cref="Configuration.AuthMode.ApiKeyHeader"/>; defaults per provider.</summary>
    public string? HeaderName { get; set; }

    /// <summary>Azure <c>api-version</c> query value; Anthropic <c>anthropic-version</c>.</summary>
    public string? ApiVersion { get; set; }

    /// <summary>Azure OpenAI deployment name. Falls back to the model id.</summary>
    public string? Deployment { get; set; }

    /// <summary>Entra token scope for Azure bearer auth.</summary>
    public string? TokenScope { get; set; }

    public string? DefaultModel { get; set; }

    /// <summary>Models to offer in pickers; providers whose API cannot list models return these from <see cref="ILlmProvider.ListModelsAsync"/>.</summary>
    public List<string> Models { get; set; } = new();

    public int? TimeoutSeconds { get; set; }

    public int? MaxOutputTokens { get; set; }

    /// <summary>Basic-auth credentials (Ollama behind a proxy).</summary>
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>Extra headers sent with every request to this provider.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RetryOptions
{
    /// <summary>Total attempts including the first; 1 disables retries.</summary>
    public int MaxAttempts { get; set; } = 3;
    public double BaseDelaySeconds { get; set; } = 1.0;
    public double MaxDelaySeconds { get; set; } = 30.0;
    public bool UseJitter { get; set; } = true;
}
