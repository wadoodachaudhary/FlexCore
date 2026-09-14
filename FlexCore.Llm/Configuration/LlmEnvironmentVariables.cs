namespace Fx.ControlKit.Llm.Configuration;

/// <summary>Environment-variable names honoured for one provider, in precedence order.</summary>
public sealed record ProviderEnvironmentNames(
    string[] ApiKey,
    string[] Endpoint,
    string[] AuthMode,
    string[] HeaderName,
    string[] ApiVersion,
    string[] DefaultModel,
    string[] Models,
    string[] Username,
    string[] Password,
    string[] Deployment,
    string[] TokenScope)
{
    public static readonly string[] NoNames = Array.Empty<string>();
}

/// <summary>
/// The environment variables each provider reads before falling back to
/// configuration. The list is the one GhostWriter's LlmService honours, so a
/// machine already set up for it needs no new variables; the
/// <c>OLLAMA_CLOUD_*</c> set is new so the cloud daemon can authenticate
/// independently of the local one.
/// </summary>
public static class LlmEnvironmentVariables
{
    private static readonly string[] None = ProviderEnvironmentNames.NoNames;

    public static readonly IReadOnlyDictionary<string, ProviderEnvironmentNames> ByProvider =
        new Dictionary<string, ProviderEnvironmentNames>(StringComparer.OrdinalIgnoreCase)
        {
            [ProviderKeys.OpenAi] = new(
                ApiKey: new[] { "OPENAI_API_KEY", "CODEX_API_KEY" },
                Endpoint: new[] { "OPENAI_BASE_URL", "OPENAI_ENDPOINT", "CODEX_ENDPOINT" },
                AuthMode: new[] { "OPENAI_AUTH_MODE" },
                HeaderName: None,
                ApiVersion: None,
                DefaultModel: new[] { "OPENAI_MODEL", "CODEX_MODEL" },
                Models: new[] { "OPENAI_MODELS" },
                Username: None, Password: None, Deployment: None,
                TokenScope: new[] { "OPENAI_TOKEN_SCOPE" }),

            [ProviderKeys.AzureOpenAi] = new(
                ApiKey: new[] { "AZURE_OPENAI_API_KEY" },
                Endpoint: new[] { "AZURE_OPENAI_ENDPOINT" },
                AuthMode: new[] { "AZURE_OPENAI_AUTH_MODE" },
                HeaderName: None,
                ApiVersion: new[] { "AZURE_OPENAI_API_VERSION", "OPENAI_API_VERSION" },
                DefaultModel: new[] { "AZURE_OPENAI_MODEL", "AZURE_OPENAI_DEPLOYMENT" },
                Models: new[] { "AZURE_OPENAI_MODELS" },
                Username: None, Password: None,
                Deployment: new[] { "AZURE_OPENAI_DEPLOYMENT" },
                TokenScope: new[] { "AZURE_OPENAI_TOKEN_SCOPE" }),

            [ProviderKeys.AzureFoundry] = new(
                ApiKey: new[] { "AZURE_FOUNDRY_API_KEY", "AZURE_INFERENCE_API_KEY" },
                Endpoint: new[] { "AZURE_FOUNDRY_ENDPOINT", "AZURE_AI_FOUNDRY_ENDPOINT", "AZURE_INFERENCE_ENDPOINT" },
                AuthMode: new[] { "AZURE_FOUNDRY_AUTH_MODE" },
                HeaderName: None,
                ApiVersion: new[] { "AZURE_FOUNDRY_API_VERSION" },
                DefaultModel: new[] { "AZURE_FOUNDRY_MODEL" },
                Models: new[] { "AZURE_FOUNDRY_MODELS" },
                Username: None, Password: None, Deployment: None,
                TokenScope: new[] { "AZURE_FOUNDRY_TOKEN_SCOPE" }),

            [ProviderKeys.Anthropic] = new(
                ApiKey: new[] { "ANTHROPIC_API_KEY", "CLAUDE_API_KEY" },
                Endpoint: new[] { "ANTHROPIC_ENDPOINT", "CLAUDE_ENDPOINT", "ANTHROPIC_BASE_URL" },
                AuthMode: None,
                HeaderName: None,
                ApiVersion: new[] { "ANTHROPIC_VERSION" },
                DefaultModel: new[] { "ANTHROPIC_MODEL", "CLAUDE_MODEL" },
                Models: new[] { "ANTHROPIC_MODELS", "CLAUDE_MODELS" },
                Username: None, Password: None, Deployment: None, TokenScope: None),

            [ProviderKeys.Gemini] = new(
                ApiKey: new[] { "GEMINI_API_KEY", "GOOGLE_API_KEY" },
                Endpoint: new[] { "GEMINI_ENDPOINT" },
                AuthMode: None,
                HeaderName: None,
                ApiVersion: new[] { "GEMINI_API_VERSION" },
                DefaultModel: new[] { "GEMINI_MODEL" },
                Models: new[] { "GEMINI_MODELS" },
                Username: None, Password: None, Deployment: None, TokenScope: None),

            [ProviderKeys.Ollama] = new(
                ApiKey: new[] { "OLLAMA_API_KEY" },
                Endpoint: new[] { "OLLAMA_ENDPOINT", "OLLAMA_HOST", "OLLAMA_ONPREM_ENDPOINT", "OLLAMA_LOCAL_ENDPOINT" },
                AuthMode: new[] { "OLLAMA_AUTH_MODE" },
                HeaderName: new[] { "OLLAMA_AUTH_HEADER" },
                ApiVersion: None,
                DefaultModel: new[] { "OLLAMA_MODEL" },
                Models: new[] { "OLLAMA_MODELS" },
                Username: new[] { "OLLAMA_USERNAME" },
                Password: new[] { "OLLAMA_PASSWORD" },
                Deployment: None, TokenScope: None),

            [ProviderKeys.OllamaCloud] = new(
                ApiKey: new[] { "OLLAMA_CLOUD_API_KEY" },
                Endpoint: new[] { "OLLAMA_CLOUD_ENDPOINT", "OLLAMA_REMOTE_ENDPOINT" },
                AuthMode: new[] { "OLLAMA_CLOUD_AUTH_MODE" },
                HeaderName: new[] { "OLLAMA_CLOUD_AUTH_HEADER" },
                ApiVersion: None,
                DefaultModel: new[] { "OLLAMA_CLOUD_MODEL" },
                Models: new[] { "OLLAMA_CLOUD_MODELS" },
                Username: new[] { "OLLAMA_CLOUD_USERNAME" },
                Password: new[] { "OLLAMA_CLOUD_PASSWORD" },
                Deployment: None, TokenScope: None),

            [ProviderKeys.HuggingFace] = new(
                ApiKey: new[] { "HUGGINGFACE_API_KEY", "HF_TOKEN", "HF_API_KEY" },
                Endpoint: new[] { "HUGGINGFACE_ENDPOINT", "HF_INFERENCE_ENDPOINT", "HF_ENDPOINT", "HF_BASE_URL" },
                AuthMode: new[] { "HUGGINGFACE_AUTH_MODE", "HF_AUTH_MODE" },
                HeaderName: None,
                ApiVersion: None,
                DefaultModel: new[] { "HUGGINGFACE_MODEL", "HF_MODEL" },
                Models: new[] { "HUGGINGFACE_MODELS", "HF_MODELS" },
                Username: None, Password: None, Deployment: None, TokenScope: None),

            [ProviderKeys.Groq] = new(
                ApiKey: new[] { "GROQ_API_KEY" },
                Endpoint: new[] { "GROQ_ENDPOINT" },
                AuthMode: None,
                HeaderName: None,
                ApiVersion: None,
                DefaultModel: new[] { "GROQ_MODEL" },
                Models: new[] { "GROQ_MODELS" },
                Username: None, Password: None, Deployment: None, TokenScope: None),

            [ProviderKeys.XAi] = new(
                ApiKey: new[] { "XAI_API_KEY", "GROK_API_KEY" },
                Endpoint: new[] { "XAI_ENDPOINT", "GROK_ENDPOINT" },
                AuthMode: None,
                HeaderName: None,
                ApiVersion: None,
                DefaultModel: new[] { "XAI_MODEL", "GROK_MODEL" },
                Models: new[] { "XAI_MODELS", "GROK_MODELS" },
                Username: None, Password: None, Deployment: None, TokenScope: None),

            [ProviderKeys.Mistral] = new(
                ApiKey: new[] { "MISTRAL_API_KEY" },
                Endpoint: new[] { "MISTRAL_API_ENDPOINT", "MISTRAL_ENDPOINT" },
                AuthMode: None,
                HeaderName: None,
                ApiVersion: None,
                DefaultModel: new[] { "MISTRAL_API_MODEL", "MISTRAL_MODEL" },
                Models: new[] { "MISTRAL_API_MODELS", "MISTRAL_MODELS" },
                Username: None, Password: None, Deployment: None, TokenScope: None),
        };

    public static ProviderEnvironmentNames? For(string providerKey)
        => ProviderKeys.Normalize(providerKey) is { } key && ByProvider.TryGetValue(key, out var names) ? names : null;
}
