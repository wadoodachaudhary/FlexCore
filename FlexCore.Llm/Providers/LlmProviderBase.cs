using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Fx.ControlKit.Llm.Configuration;
using Fx.ControlKit.Llm.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fx.ControlKit.Llm.Providers;

/// <summary>
/// Shared machinery for the wire adapters: named <see cref="HttpClient"/>
/// creation, auth application, JSON posting with error mapping, and the
/// streaming send. Capability checks and the default
/// <see cref="NotSupportedException"/>s live here so an adapter overrides
/// only what it implements.
/// </summary>
public abstract class LlmProviderBase : ILlmProvider
{
    protected LlmProviderBase(
        string key,
        IHttpClientFactory httpClientFactory,
        ICredentialResolver credentials,
        ITokenProvider? tokenProvider = null,
        IEnumerable<ILlmRequestAuthenticator>? authenticators = null,
        ILogger? logger = null)
    {
        Key = ProviderKeys.Normalize(key) ?? throw new ArgumentException($"'{key}' is not a known provider key.", nameof(key));
        HttpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        Credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        TokenProvider = tokenProvider;
        Authenticator = authenticators?.FirstOrDefault(a => string.Equals(a.ProviderKey, Key, StringComparison.OrdinalIgnoreCase));
        Logger = logger ?? NullLogger.Instance;
    }

    public string Key { get; }
    public abstract LlmCapabilities Capabilities { get; }
    public abstract bool IsConfigured { get; }

    protected IHttpClientFactory HttpClientFactory { get; }
    protected ICredentialResolver Credentials { get; }
    protected ITokenProvider? TokenProvider { get; }
    protected ILlmRequestAuthenticator? Authenticator { get; }
    protected ILogger Logger { get; }

    /// <summary>Name of the <see cref="HttpClient"/> registered for a provider key.</summary>
    public static string HttpClientName(string providerKey) => "FlexCore.Llm." + (ProviderKeys.Normalize(providerKey) ?? providerKey);

    /// <summary>Default base URL when neither environment nor configuration names one; null means the provider must be configured.</summary>
    protected abstract string? DefaultEndpoint { get; }

    protected virtual AuthMode DefaultAuthMode => AuthMode.Bearer;
    protected virtual string DefaultHeaderName => "Authorization";

    public abstract Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken);
    public abstract IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, CancellationToken cancellationToken);

    public virtual Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
        => throw NotSupported(LlmCapabilities.ListModels);

    public virtual Task<ImageResult> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken)
        => throw NotSupported(LlmCapabilities.ImageGeneration);

    public virtual Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
        => throw NotSupported(LlmCapabilities.Embeddings);

    protected NotSupportedException NotSupported(LlmCapabilities capability)
        => new($"{Key} does not support {capability}.");

    protected void Require(LlmCapabilities capability)
    {
        if ((Capabilities & capability) != capability) throw NotSupported(capability);
    }

    protected ProviderSettings Settings => Credentials.Resolve(Key);

    protected string ResolveEndpoint(ProviderSettings settings)
        => settings.Endpoint
           ?? DefaultEndpoint
           ?? throw new LlmConfigurationException($"{Key} has no endpoint configured.", Key);

    protected string ResolveModel(ProviderSettings settings, ModelRef model)
    {
        if (model.HasModel) return model.Model;
        var fallback = settings.DefaultModel ?? ModelCatalog.DefaultModel(Key);
        return fallback ?? throw new LlmConfigurationException($"No model was given and {Key} has no default model configured.", Key, model);
    }

    protected int? ResolveMaxOutputTokens(ChatRequest request, ProviderSettings settings)
        => request.MaxOutputTokens ?? settings.MaxOutputTokens ?? GlobalMaxOutputTokens;

    /// <summary>Set by DI from <see cref="LlmOptions.MaxOutputTokens"/>; providers fall back to it after the request and provider values.</summary>
    public int? GlobalMaxOutputTokens { get; set; }

    /// <summary>Set by DI from <see cref="LlmOptions.Temperature"/>.</summary>
    public double? GlobalTemperature { get; set; }

    protected double? ResolveTemperature(ChatRequest request) => request.Temperature ?? GlobalTemperature;

    /// <summary>System prompt = <see cref="ChatRequest.System"/> plus any System-role messages, in order.</summary>
    protected static string? CollectSystem(ChatRequest request)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.System)) parts.Add(request.System);
        parts.AddRange(request.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text).Where(t => t.Length > 0));
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    protected static IEnumerable<ChatMessage> NonSystem(ChatRequest request)
        => request.Messages.Where(m => m.Role != ChatRole.System);

    protected HttpClient CreateClient() => HttpClientFactory.CreateClient(HttpClientName(Key));

    protected static HttpRequestMessage JsonPost(string url, JsonObject body, string? accept = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (accept is not null)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        }

        return request;
    }

    /// <summary>Attaches the credential according to the effective auth mode. Azure-style providers pass <paramref name="allowTokenProvider"/> so a missing key falls through to the registered <see cref="ITokenProvider"/>.</summary>
    protected async Task ApplyAuthAsync(HttpRequestMessage request, ProviderSettings settings, CancellationToken cancellationToken, bool allowTokenProvider = false)
    {
        foreach (var (name, value) in settings.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        var mode = settings.AuthMode;
        if (mode is null)
        {
            mode = settings.HasApiKey
                ? DefaultAuthMode
                : allowTokenProvider && TokenProvider is not null
                    ? AuthMode.Bearer
                    : settings.HasBasicCredentials
                        ? AuthMode.Basic
                        : Authenticator is not null
                            ? AuthMode.Custom
                            : DefaultAuthMode == AuthMode.None ? AuthMode.None : DefaultAuthMode;
        }

        switch (mode)
        {
            case AuthMode.None:
                return;

            case AuthMode.Bearer:
                if (settings.HasApiKey)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
                    return;
                }

                if (allowTokenProvider && TokenProvider is not null)
                {
                    var token = await TokenProvider.GetTokenAsync(settings.TokenScope, cancellationToken).ConfigureAwait(false);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    return;
                }

                throw MissingKey();

            case AuthMode.ApiKeyHeader:
                if (!settings.HasApiKey) throw MissingKey();
                var header = settings.HeaderName ?? DefaultHeaderName;
                if (string.Equals(header, "Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
                }
                else
                {
                    request.Headers.TryAddWithoutValidation(header, settings.ApiKey);
                }
                return;

            case AuthMode.Basic:
                if (!settings.HasBasicCredentials)
                {
                    throw new LlmConfigurationException($"{Key} basic auth is enabled but no username is configured.", Key);
                }

                var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password ?? string.Empty}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw);
                return;

            case AuthMode.Custom:
                if (Authenticator is null)
                {
                    throw new LlmConfigurationException($"{Key} is set to custom auth but no ILlmRequestAuthenticator is registered for it.", Key);
                }

                await Authenticator.AuthenticateAsync(request, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    protected LlmConfigurationException MissingKey()
    {
        var names = LlmEnvironmentVariables.For(Key)?.ApiKey;
        var hint = names is { Length: > 0 } ? $" Set {string.Join(" or ", names)}, or Llm:{Key}:ApiKey." : string.Empty;
        return new LlmConfigurationException($"{Key} has no API key configured.{hint}", Key);
    }

    /// <summary>Sends and returns the body; maps non-success to <see cref="LlmHttpException"/> with the body and Retry-After.</summary>
    protected async Task<string> SendForBodyAsync(HttpClient client, HttpRequestMessage request, ModelRef? model, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ToHttpException(response, body, model, request.RequestUri);
        }

        return body;
    }

    /// <summary>Sends with headers-only completion and hands back the open response for streaming; non-success is mapped after reading the body.</summary>
    protected async Task<HttpResponseMessage> SendForStreamAsync(HttpClient client, HttpRequestMessage request, ModelRef? model, CancellationToken cancellationToken)
    {
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }

        throw ToHttpException(response, body, model, request.RequestUri);
    }

    private LlmHttpException ToHttpException(HttpResponseMessage response, string body, ModelRef? model, Uri? url)
    {
        TimeSpan? retryAfter = null;
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) retryAfter = delta;
        else if (header?.Date is { } date) retryAfter = date - DateTimeOffset.UtcNow;
        if (retryAfter is { } r && r < TimeSpan.Zero) retryAfter = TimeSpan.Zero;

        Logger.LogWarning("{Provider} HTTP {Status} from {Url}: {Body}", Key, (int)response.StatusCode, url, Truncate(body));
        return new LlmHttpException(Key, response.StatusCode, body, retryAfter, model, url?.ToString());
    }

    private static string Truncate(string body) => body.Length <= 400 ? body : body[..400] + "…";

    /// <summary>Joins a base URL and a relative leaf, tolerating trailing slashes.</summary>
    protected static string Combine(string baseUrl, string leaf)
        => baseUrl.TrimEnd('/') + "/" + leaf.TrimStart('/');

    /// <summary>Rewrites <paramref name="url"/> with <paramref name="key"/>=<paramref name="value"/> in its query string.</summary>
    protected static string WithQuery(string url, string key, string? value)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var builder = new UriBuilder(uri);
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var piece in (builder.Query ?? string.Empty).TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = piece.IndexOf('=');
            var k = Uri.UnescapeDataString(idx < 0 ? piece : piece[..idx]);
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;
            pairs.Add(new(k, idx < 0 ? string.Empty : Uri.UnescapeDataString(piece[(idx + 1)..])));
        }

        if (value is not null) pairs.Add(new(key, value));
        builder.Query = string.Join("&", pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        return builder.Uri.ToString();
    }

    /// <summary>Strips a known trailing leaf (e.g. "/chat/completions") from a configured URL so only the base remains.</summary>
    protected static string StripLeaves(string url, params string[] leaves)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url.TrimEnd('/');
        var builder = new UriBuilder(uri);
        var path = (builder.Path ?? string.Empty).TrimEnd('/');
        foreach (var leaf in leaves)
        {
            if (path.EndsWith(leaf, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^leaf.Length];
                break;
            }
        }

        builder.Path = path.Length == 0 ? "/" : path;
        return builder.Uri.ToString().TrimEnd('/');
    }

    protected static string? ExtrasString(IReadOnlyDictionary<string, object?>? extras, string key)
        => extras is not null && extras.TryGetValue(key, out var value) && value is not null ? Convert.ToString(value) : null;
}
