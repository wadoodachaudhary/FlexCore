namespace Fx.ControlKit.Llm.Configuration;

/// <summary>
/// How the API key (or token) is attached to a request. When a provider's
/// configured mode is null the adapter picks its native default: Bearer for
/// the OpenAI family, an <c>x-api-key</c> header for Anthropic,
/// <c>x-goog-api-key</c> for Gemini, <c>api-key</c> for Azure, none for Ollama.
/// </summary>
public enum AuthMode
{
    /// <summary>No credential is sent (local Ollama).</summary>
    None,
    /// <summary><c>Authorization: Bearer {key}</c>. Azure providers with no key use the registered <see cref="ITokenProvider"/> instead.</summary>
    Bearer,
    /// <summary>The key is sent verbatim in the header named by <c>HeaderName</c>.</summary>
    ApiKeyHeader,
    /// <summary><c>Authorization: Basic</c> from Username/Password (reverse-proxied Ollama).</summary>
    Basic,
    /// <summary>The host supplied an <see cref="ILlmRequestAuthenticator"/> for this provider.</summary>
    Custom,
}

public static class AuthModes
{
    /// <summary>
    /// Parses the spellings found in existing configuration and environment
    /// variables: "api-key", "apikey", "header" → <see cref="AuthMode.ApiKeyHeader"/>;
    /// "managedidentity", "managed-identity", "entra", "aad" → <see cref="AuthMode.Bearer"/>;
    /// "auto" or empty → null (provider default).
    /// </summary>
    public static AuthMode? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var key = value.Trim().ToLowerInvariant().Replace("-", string.Empty).Replace("_", string.Empty);
        return key switch
        {
            "auto" or "default" => null,
            "none" or "anonymous" => AuthMode.None,
            "bearer" or "token" or "managedidentity" or "entra" or "aad" or "oauth" => AuthMode.Bearer,
            "apikey" or "apikeyheader" or "header" or "key" => AuthMode.ApiKeyHeader,
            "basic" => AuthMode.Basic,
            "custom" => AuthMode.Custom,
            _ => Enum.TryParse<AuthMode>(value.Trim(), ignoreCase: true, out var parsed) ? parsed : null,
        };
    }
}

/// <summary>
/// Supplies a bearer token for providers that authenticate with Entra ID or
/// another OAuth flow. The library never references Azure.Identity: the host
/// registers whatever produces the token (a <c>DefaultAzureCredential</c>
/// wrapped in a lambda, typically) through
/// <c>LlmBuilder.UseTokenProvider</c>.
/// </summary>
public interface ITokenProvider
{
    /// <summary>Returns a bearer token; <paramref name="scope"/> is the provider's configured token scope (e.g. <c>https://cognitiveservices.azure.com/.default</c>) and may be null.</summary>
    Task<string> GetTokenAsync(string? scope, CancellationToken cancellationToken);
}

/// <summary>Adapts a <c>Func&lt;CancellationToken, Task&lt;string&gt;&gt;</c> (or a scope-aware variant) to <see cref="ITokenProvider"/>.</summary>
public sealed class DelegateTokenProvider : ITokenProvider
{
    private readonly Func<string?, CancellationToken, Task<string>> _factory;

    public DelegateTokenProvider(Func<CancellationToken, Task<string>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = (_, ct) => factory(ct);
    }

    public DelegateTokenProvider(Func<string?, CancellationToken, Task<string>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public Task<string> GetTokenAsync(string? scope, CancellationToken cancellationToken) => _factory(scope, cancellationToken);
}

/// <summary>Host-supplied request signing for <see cref="AuthMode.Custom"/>. Registered per provider key through <c>LlmBuilder.UseAuthenticator</c>.</summary>
public interface ILlmRequestAuthenticator
{
    string ProviderKey { get; }
    Task AuthenticateAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}

public sealed class DelegateRequestAuthenticator : ILlmRequestAuthenticator
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task> _apply;

    public DelegateRequestAuthenticator(string providerKey, Func<HttpRequestMessage, CancellationToken, Task> apply)
    {
        ProviderKey = ProviderKeys.Normalize(providerKey) ?? providerKey;
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
    }

    public string ProviderKey { get; }
    public Task AuthenticateAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _apply(request, cancellationToken);
}
