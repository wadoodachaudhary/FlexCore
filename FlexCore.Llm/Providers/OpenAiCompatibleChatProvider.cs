using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Fx.ControlKit.Llm.Configuration;
using Microsoft.Extensions.Logging;

namespace Fx.ControlKit.Llm.Providers;

/// <summary>
/// Chat Completions adapter parameterised by provider key. One class serves
/// Groq, xAI, Mistral, the Hugging Face router and Azure AI Foundry — they
/// differ only in base URL, auth header and (for Foundry) an
/// <c>api-version</c> query. Construct with <see cref="ProviderKeys.OpenAi"/>
/// to reach OpenAI's own Chat Completions endpoint instead of the Responses
/// API that <see cref="OpenAiResponsesProvider"/> uses.
/// <para>
/// Hugging Face has two shapes: the router (<c>router.huggingface.co</c>),
/// which offers the configured <c>Models</c> and lists models live, and a
/// dedicated Inference Endpoint, which serves the <c>DedicatedModel</c> /
/// <c>DedicatedModels</c> set and speaks <c>/v1/embeddings</c> too.
/// </para>
/// </summary>
public class OpenAiCompatibleChatProvider : LlmProviderBase
{
    public const string HuggingFaceRouterHost = "router.huggingface.co";

    private static readonly Dictionary<string, string> DefaultEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        [ProviderKeys.OpenAi] = "https://api.openai.com/v1",
        [ProviderKeys.Groq] = "https://api.groq.com/openai/v1",
        [ProviderKeys.XAi] = "https://api.x.ai/v1",
        [ProviderKeys.Mistral] = "https://api.mistral.ai/v1",
        [ProviderKeys.HuggingFace] = "https://" + HuggingFaceRouterHost + "/v1",
    };

    private const string FoundryDefaultApiVersion = "2024-05-01-preview";
    private const LlmCapabilities ChatBasics = LlmCapabilities.Chat | LlmCapabilities.Streaming | LlmCapabilities.JsonMode | LlmCapabilities.Vision | LlmCapabilities.Tools;

    public OpenAiCompatibleChatProvider(
        string key,
        IHttpClientFactory httpClientFactory,
        ICredentialResolver credentials,
        ITokenProvider? tokenProvider = null,
        IEnumerable<ILlmRequestAuthenticator>? authenticators = null,
        ILogger<OpenAiCompatibleChatProvider>? logger = null)
        : base(key, httpClientFactory, credentials, tokenProvider, authenticators, logger)
    {
    }

    private bool IsFoundry => Key == ProviderKeys.AzureFoundry;
    private bool IsHuggingFace => Key == ProviderKeys.HuggingFace;

    public override LlmCapabilities Capabilities => Key switch
    {
        ProviderKeys.Groq => ChatBasics | LlmCapabilities.JsonSchema | LlmCapabilities.Reasoning | LlmCapabilities.ListModels,
        ProviderKeys.XAi => ChatBasics | LlmCapabilities.JsonSchema | LlmCapabilities.Reasoning | LlmCapabilities.ListModels,
        ProviderKeys.Mistral => ChatBasics | LlmCapabilities.JsonSchema | LlmCapabilities.Embeddings | LlmCapabilities.ListModels,
        // The router has no /v1/embeddings; a dedicated (TEI/TGI) endpoint does. The router lists models, a dedicated endpoint serves one.
        ProviderKeys.HuggingFace => IsRouterEndpoint(Settings)
            ? ChatBasics | LlmCapabilities.ListModels
            : ChatBasics | LlmCapabilities.Embeddings,
        ProviderKeys.AzureFoundry => ChatBasics | LlmCapabilities.JsonSchema | LlmCapabilities.Embeddings,
        _ => ChatBasics | LlmCapabilities.JsonSchema | LlmCapabilities.Reasoning | LlmCapabilities.Embeddings | LlmCapabilities.PromptCaching | LlmCapabilities.ListModels,
    };

    public override bool IsConfigured
    {
        get
        {
            var settings = Settings;
            if (IsFoundry) return settings.HasEndpoint && (settings.HasApiKey || TokenProvider is not null || Authenticator is not null);
            return settings.HasApiKey || Authenticator is not null || settings.AuthMode == AuthMode.None;
        }
    }

    protected override string? DefaultEndpoint => DefaultEndpoints.TryGetValue(Key, out var url) ? url : null;
    protected override AuthMode DefaultAuthMode => IsFoundry ? AuthMode.ApiKeyHeader : AuthMode.Bearer;
    protected override string DefaultHeaderName => IsFoundry ? "api-key" : "Authorization";

    /// <summary>True when Hugging Face traffic goes to the public router (the default) rather than a dedicated endpoint.</summary>
    public bool IsRouterEndpoint(ProviderSettings settings)
    {
        if (!IsHuggingFace) return false;
        var endpoint = settings.Endpoint ?? DefaultEndpoint;
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
               && string.Equals(uri.Host, HuggingFaceRouterHost, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDedicatedCatalogEntry(ModelInfo model)
        => model.Metadata is { } metadata && metadata.ContainsKey(ModelCatalog.DedicatedEndpointMetadata);

    private static bool IsRouterDefaultModel(string? model)
        => string.Equals(model, ModelCatalog.DefaultModel(ProviderKeys.HuggingFace), StringComparison.OrdinalIgnoreCase);

    /// <summary>Hugging Face: the router list or the dedicated list, depending on the endpoint; every other key uses the base rule.</summary>
    public override IReadOnlyList<ModelInfo> ConfiguredModels
    {
        get
        {
            if (!IsHuggingFace) return base.ConfiguredModels;
            var settings = Settings;
            if (IsRouterEndpoint(settings))
            {
                var ids = new List<string>();
                if (settings.DefaultModel is { } d) ids.Add(d);
                ids.AddRange(settings.Models);
                return Describe(ids.Count == 0 ? ModelCatalog.ForProvider(Key).Where(m => !IsDedicatedCatalogEntry(m)).Select(m => m.Id) : ids);
            }

            var dedicated = new List<string>();
            if (settings.DedicatedDefaultModel is { } dd) dedicated.Add(dd);
            if (settings.DefaultModel is { } generic && !IsRouterDefaultModel(generic)) dedicated.Add(generic);
            dedicated.AddRange(settings.DedicatedModels);
            return Describe(dedicated.Count == 0 ? ModelCatalog.ForProvider(Key).Where(IsDedicatedCatalogEntry).Select(m => m.Id) : dedicated);
        }
    }

    protected override string? DefaultModelFor(ProviderSettings settings)
    {
        if (!IsHuggingFace || IsRouterEndpoint(settings)) return null;
        // A dedicated endpoint serves its own model; the generic HUGGINGFACE_MODEL
        // counts only when it is not the router's stock default.
        return settings.DedicatedDefaultModel
               ?? (IsRouterDefaultModel(settings.DefaultModel) ? null : settings.DefaultModel)
               ?? settings.DedicatedModels.FirstOrDefault()
               ?? ModelCatalog.ForProvider(Key).FirstOrDefault(IsDedicatedCatalogEntry)?.Id;
    }

    /// <summary>Base URL with any known leaf removed; an empty path becomes <c>/v1</c> (or the Foundry path for its hosts).</summary>
    protected string BaseUrl(ProviderSettings settings)
    {
        var raw = ResolveEndpoint(settings);
        var stripped = StripLeaves(raw, "/chat/completions", "/embeddings", "/models", "/completions");
        if (!Uri.TryCreate(stripped, UriKind.Absolute, out var uri)) return stripped;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length > 0) return stripped;

        var builder = new UriBuilder(uri);
        if (IsFoundry)
        {
            builder.Path = IsAzureHost(raw) ? "/openai/v1" : "/models";
        }
        else
        {
            builder.Path = "/v1";
        }

        return builder.Uri.ToString().TrimEnd('/');
    }

    private string ChatUrl(ProviderSettings settings, IReadOnlyDictionary<string, object?>? extras)
        => Finish(Combine(BaseUrl(settings), "chat/completions"), settings, extras);

    private string EmbeddingsUrl(ProviderSettings settings, IReadOnlyDictionary<string, object?>? extras)
        => Finish(Combine(BaseUrl(settings), "embeddings"), settings, extras);

    private string ModelsUrl(ProviderSettings settings)
        => Finish(Combine(BaseUrl(settings), "models"), settings, null);

    /// <summary>Foundry's <c>/models</c> surface needs <c>api-version</c>; its <c>/openai/v1</c> surface does not.</summary>
    private string Finish(string url, ProviderSettings settings, IReadOnlyDictionary<string, object?>? extras)
    {
        if (!IsFoundry) return url;
        if (url.Contains("/openai/v1", StringComparison.OrdinalIgnoreCase)) return WithQuery(url, "api-version", null);
        var version = ExtrasString(extras, "api-version") ?? settings.ApiVersion ?? FoundryDefaultApiVersion;
        return WithQuery(url, "api-version", version);
    }

    /// <summary>
    /// Kimi K2.6 on the Hugging Face router spends the completion budget on
    /// reasoning and returns an empty assistant message unless instant mode
    /// is forced; the <c>-thinking</c> variants are left alone.
    /// </summary>
    public static bool DisablesThinking(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var normalized = model.Trim().ToLowerInvariant();
        return normalized.StartsWith("moonshotai/kimi-k2.6", StringComparison.Ordinal)
               && !normalized.Contains("thinking", StringComparison.Ordinal);
    }

    private JsonObject BuildBody(ChatRequest request, string model, ProviderSettings settings, bool stream)
    {
        var body = OpenAiChatWire.BuildBody(request, model, CollectSystem(request), ResolveMaxOutputTokens(request, settings), ResolveTemperature(request), stream, openAiDialect: Key == ProviderKeys.OpenAi);
        if (IsHuggingFace && DisablesThinking(model) && body["thinking"] is null && request.Reasoning?.Enabled != true)
        {
            body["thinking"] = new JsonObject { ["type"] = "disabled" };
        }

        return body;
    }

    public override async Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        Require(LlmCapabilities.Chat);
        var settings = Settings;
        var model = ResolveModel(settings, request.Model);
        var requested = request.Model with { Provider = Key, Model = model };
        var body = BuildBody(request, model, settings, stream: false);

        using var http = JsonPost(ChatUrl(settings, request.Extras), body);
        await ApplyAuthAsync(http, settings, cancellationToken, allowTokenProvider: IsFoundry).ConfigureAwait(false);

        var watch = Stopwatch.StartNew();
        var response = await SendForBodyAsync(CreateClient(), http, requested, cancellationToken).ConfigureAwait(false);
        return OpenAiChatWire.ParseResponse(response, Key, requested, watch.Elapsed);
    }

    public override async IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Require(LlmCapabilities.Streaming);
        var settings = Settings;
        var model = ResolveModel(settings, request.Model);
        var requested = request.Model with { Provider = Key, Model = model };
        var body = BuildBody(request, model, settings, stream: true);

        using var http = JsonPost(ChatUrl(settings, request.Extras), body, accept: "text/event-stream");
        await ApplyAuthAsync(http, settings, cancellationToken, allowTokenProvider: IsFoundry).ConfigureAwait(false);

        using var response = await SendForStreamAsync(CreateClient(), http, requested, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var delta in OpenAiChatWire.ReadStreamAsync(stream, Key, requested, cancellationToken).ConfigureAwait(false))
        {
            yield return delta;
        }
    }

    public override async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        if (!this.Supports(LlmCapabilities.ListModels))
        {
            return ConfiguredModels;
        }

        var settings = Settings;
        using var http = new HttpRequestMessage(HttpMethod.Get, ModelsUrl(settings));
        await ApplyAuthAsync(http, settings, cancellationToken).ConfigureAwait(false);
        var body = await SendForBodyAsync(CreateClient(), http, null, cancellationToken).ConfigureAwait(false);
        var listed = OpenAiChatWire.ParseModelList(body, Key);
        return listed.Count > 0 ? listed : ConfiguredModels;
    }

    public override async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
    {
        Require(LlmCapabilities.Embeddings);
        var settings = Settings;
        var model = request.Model.HasModel ? request.Model.Model : throw new LlmConfigurationException($"{Key} embeddings need an explicit model.", Key);
        var requested = request.Model with { Provider = Key, Model = model };

        using var http = JsonPost(EmbeddingsUrl(settings, request.Extras), OpenAiChatWire.BuildEmbeddingBody(request, model));
        await ApplyAuthAsync(http, settings, cancellationToken, allowTokenProvider: IsFoundry).ConfigureAwait(false);

        var watch = Stopwatch.StartNew();
        var body = await SendForBodyAsync(CreateClient(), http, requested, cancellationToken).ConfigureAwait(false);
        return OpenAiChatWire.ParseEmbeddings(body, Key, requested, watch.Elapsed);
    }
}
