using System.Diagnostics;
using System.Runtime.CompilerServices;
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
/// </summary>
public class OpenAiCompatibleChatProvider : LlmProviderBase
{
    private static readonly Dictionary<string, string> DefaultEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        [ProviderKeys.OpenAi] = "https://api.openai.com/v1",
        [ProviderKeys.Groq] = "https://api.groq.com/openai/v1",
        [ProviderKeys.XAi] = "https://api.x.ai/v1",
        [ProviderKeys.Mistral] = "https://api.mistral.ai/v1",
        [ProviderKeys.HuggingFace] = "https://router.huggingface.co/v1",
    };

    private const string FoundryDefaultApiVersion = "2024-05-01-preview";

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

    public override LlmCapabilities Capabilities => Key switch
    {
        ProviderKeys.Groq => LlmCapabilities.Chat | LlmCapabilities.Streaming | LlmCapabilities.JsonMode | LlmCapabilities.JsonSchema
                             | LlmCapabilities.Vision | LlmCapabilities.Tools | LlmCapabilities.Reasoning | LlmCapabilities.ListModels,
        ProviderKeys.XAi => LlmCapabilities.Chat | LlmCapabilities.Streaming | LlmCapabilities.JsonMode | LlmCapabilities.JsonSchema
                            | LlmCapabilities.Vision | LlmCapabilities.Tools | LlmCapabilities.Reasoning | LlmCapabilities.ListModels,
        ProviderKeys.Mistral => LlmCapabilities.Chat | LlmCapabilities.Streaming | LlmCapabilities.JsonMode | LlmCapabilities.JsonSchema
                                | LlmCapabilities.Vision | LlmCapabilities.Tools | LlmCapabilities.Embeddings | LlmCapabilities.ListModels,
        ProviderKeys.HuggingFace => LlmCapabilities.Chat | LlmCapabilities.Streaming | LlmCapabilities.JsonMode | LlmCapabilities.Vision
                                    | LlmCapabilities.Tools | LlmCapabilities.Embeddings | LlmCapabilities.ListModels,
        ProviderKeys.AzureFoundry => LlmCapabilities.Chat | LlmCapabilities.Streaming | LlmCapabilities.JsonMode | LlmCapabilities.JsonSchema
                                     | LlmCapabilities.Vision | LlmCapabilities.Tools | LlmCapabilities.Embeddings,
        _ => LlmCapabilities.Chat | LlmCapabilities.Streaming | LlmCapabilities.JsonMode | LlmCapabilities.JsonSchema | LlmCapabilities.Vision
             | LlmCapabilities.Tools | LlmCapabilities.Reasoning | LlmCapabilities.Embeddings | LlmCapabilities.PromptCaching | LlmCapabilities.ListModels,
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
            builder.Path = IsAzureOpenAiHost(uri) ? "/openai/v1" : "/models";
        }
        else
        {
            builder.Path = "/v1";
        }

        return builder.Uri.ToString().TrimEnd('/');
    }

    private static bool IsAzureOpenAiHost(Uri uri)
        => uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
           || uri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase);

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

    public override async Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        Require(LlmCapabilities.Chat);
        var settings = Settings;
        var model = ResolveModel(settings, request.Model);
        var requested = request.Model with { Provider = Key, Model = model };
        var body = OpenAiChatWire.BuildBody(request, model, CollectSystem(request), ResolveMaxOutputTokens(request, settings), ResolveTemperature(request), stream: false, openAiDialect: Key == ProviderKeys.OpenAi);

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
        var body = OpenAiChatWire.BuildBody(request, model, CollectSystem(request), ResolveMaxOutputTokens(request, settings), ResolveTemperature(request), stream: true, openAiDialect: Key == ProviderKeys.OpenAi);

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
        var settings = Settings;
        if (IsFoundry || !this.Supports(LlmCapabilities.ListModels))
        {
            return ConfiguredModels(settings);
        }

        using var http = new HttpRequestMessage(HttpMethod.Get, ModelsUrl(settings));
        await ApplyAuthAsync(http, settings, cancellationToken).ConfigureAwait(false);
        var body = await SendForBodyAsync(CreateClient(), http, null, cancellationToken).ConfigureAwait(false);
        var listed = OpenAiChatWire.ParseModelList(body, Key);
        return listed.Count > 0 ? listed : ConfiguredModels(settings);
    }

    protected IReadOnlyList<ModelInfo> ConfiguredModels(ProviderSettings settings)
    {
        var ids = new List<string>();
        if (settings.DefaultModel is { } d) ids.Add(d);
        ids.AddRange(settings.Models);
        if (ids.Count == 0) ids.AddRange(ModelCatalog.ForProvider(Key).Select(m => m.Id));
        return ids.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => ModelCatalog.Find(new ModelRef(Key, id)) ?? new ModelInfo(Key, id))
            .ToList();
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
