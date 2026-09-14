using Microsoft.Extensions.Options;

namespace Fx.ControlKit.Llm.Configuration;

/// <summary>
/// The effective settings for one provider after environment variables and
/// configuration have been merged. Adapters resolve this once per call so a
/// key rotated in the environment is picked up without a restart.
/// </summary>
public sealed record ProviderSettings(
    string ProviderKey,
    string? Endpoint,
    string? ApiKey,
    AuthMode? AuthMode,
    string? HeaderName,
    string? ApiVersion,
    string? Deployment,
    string? TokenScope,
    string? DefaultModel,
    IReadOnlyList<string> Models,
    TimeSpan? Timeout,
    int? MaxOutputTokens,
    string? Username,
    string? Password,
    IReadOnlyDictionary<string, string> Headers)
{
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
    public bool HasEndpoint => !string.IsNullOrWhiteSpace(Endpoint);
    public bool HasBasicCredentials => !string.IsNullOrWhiteSpace(Username);
}

/// <summary>Resolves a provider's settings. The default implementation reads environment variables first, then the bound <see cref="LlmOptions"/>.</summary>
public interface ICredentialResolver
{
    ProviderSettings Resolve(string providerKey);
}

public sealed class EnvironmentFirstCredentialResolver : ICredentialResolver
{
    private readonly IOptionsMonitor<LlmOptions> _options;
    private readonly Func<string, string?> _environment;

    public EnvironmentFirstCredentialResolver(IOptionsMonitor<LlmOptions> options)
        : this(options, Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Uses <paramref name="environment"/> in place of the process environment (tests).</summary>
    public EnvironmentFirstCredentialResolver(IOptionsMonitor<LlmOptions> options, Func<string, string?> environment)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public ProviderSettings Resolve(string providerKey)
    {
        var key = ProviderKeys.Normalize(providerKey)
            ?? throw new LlmConfigurationException($"'{providerKey}' is not a known LLM provider.", providerKey);
        var names = LlmEnvironmentVariables.For(key);
        var configured = _options.CurrentValue.GetProvider(key) ?? new LlmProviderOptions();

        string? apiKey = null;
        if (!string.IsNullOrWhiteSpace(configured.ApiKeyVariable))
        {
            apiKey = Clean(_environment(configured.ApiKeyVariable.Trim()));
        }
        apiKey ??= FirstEnv(names?.ApiKey) ?? Clean(configured.ApiKey);

        var timeoutSeconds = configured.TimeoutSeconds;
        var models = new List<string>();
        foreach (var raw in (names?.Models ?? Array.Empty<string>()).Select(_environment))
        {
            models.AddRange(SplitList(raw));
        }
        models.AddRange(configured.Models.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()));

        return new ProviderSettings(
            ProviderKey: key,
            Endpoint: FirstEnv(names?.Endpoint) ?? Clean(configured.Endpoint),
            ApiKey: apiKey,
            AuthMode: AuthModes.Parse(FirstEnv(names?.AuthMode) ?? configured.AuthMode),
            HeaderName: FirstEnv(names?.HeaderName) ?? Clean(configured.HeaderName),
            ApiVersion: FirstEnv(names?.ApiVersion) ?? Clean(configured.ApiVersion),
            Deployment: FirstEnv(names?.Deployment) ?? Clean(configured.Deployment),
            TokenScope: FirstEnv(names?.TokenScope) ?? Clean(configured.TokenScope),
            DefaultModel: FirstEnv(names?.DefaultModel) ?? Clean(configured.DefaultModel),
            Models: models.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Timeout: timeoutSeconds is > 0 ? TimeSpan.FromSeconds(timeoutSeconds.Value) : null,
            MaxOutputTokens: configured.MaxOutputTokens,
            Username: FirstEnv(names?.Username) ?? Clean(configured.Username),
            Password: FirstEnv(names?.Password) ?? Clean(configured.Password),
            Headers: new Dictionary<string, string>(configured.Headers, StringComparer.OrdinalIgnoreCase));
    }

    private string? FirstEnv(string[]? names)
    {
        if (names is null) return null;
        foreach (var name in names)
        {
            var value = Clean(_environment(name));
            if (value is not null) return value;
        }

        return null;
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static IEnumerable<string> SplitList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0);
    }
}
