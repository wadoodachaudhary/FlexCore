using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fx.ControlKit.Llm.Configuration;
using Microsoft.Extensions.Logging;

namespace Fx.ControlKit.Llm.Providers;

/// <summary>
/// Azure OpenAI: the Chat Completions wire format addressed as
/// <c>{resource}/openai/deployments/{deployment}/chat/completions?api-version=…</c>,
/// with the key in an <c>api-key</c> header or — when no key is configured —
/// a bearer token from the host's <see cref="ITokenProvider"/>. The deployment
/// is <c>Llm:AzureOpenAi:Deployment</c> when set, otherwise the model id.
/// Embeddings and image generation use the same deployment path. Azure has
/// no data-plane listing that matches deployments, so
/// <see cref="ILlmProvider.ListModelsAsync"/> answers with the configured
/// deployment and models (no <see cref="LlmCapabilities.ListModels"/> flag).
/// </summary>
public sealed class AzureOpenAiProvider : LlmProviderBase
{
    public const string DefaultApiVersion = "2024-10-21";
    public const string DefaultTokenScope = "https://cognitiveservices.azure.com/.default";

    public AzureOpenAiProvider(
        IHttpClientFactory httpClientFactory,
        ICredentialResolver credentials,
        ITokenProvider? tokenProvider = null,
        IEnumerable<ILlmRequestAuthenticator>? authenticators = null,
        ILogger<AzureOpenAiProvider>? logger = null)
        : base(ProviderKeys.AzureOpenAi, httpClientFactory, credentials, tokenProvider, authenticators, logger)
    {
    }

    public override LlmCapabilities Capabilities =>
        LlmCapabilities.Chat | LlmCapabilities.Streaming | LlmCapabilities.JsonMode | LlmCapabilities.JsonSchema
        | LlmCapabilities.Vision | LlmCapabilities.ImageGeneration | LlmCapabilities.Embeddings | LlmCapabilities.Tools
        | LlmCapabilities.Reasoning | LlmCapabilities.PromptCaching;

    public override bool IsConfigured
    {
        get
        {
            var settings = Settings;
            return settings.HasEndpoint && (settings.HasApiKey || TokenProvider is not null || Authenticator is not null);
        }
    }

    protected override string? DefaultEndpoint => null;
    protected override AuthMode DefaultAuthMode => AuthMode.ApiKeyHeader;
    protected override string DefaultHeaderName => "api-key";

    /// <summary>The configured default model, else the deployment name (a deployment is addressable as a model id).</summary>
    protected override string? DefaultModelFor(ProviderSettings settings) => settings.DefaultModel ?? settings.Deployment;

    public override IReadOnlyList<ModelInfo> ConfiguredModels
    {
        get
        {
            var settings = Settings;
            var ids = new List<string>();
            if (settings.DefaultModel is { } d) ids.Add(d);
            if (settings.Deployment is { } dep) ids.Add(dep);
            ids.AddRange(settings.Models);
            return Describe(ids);
        }
    }

    /// <summary>The resource root (<c>https://name.openai.azure.com</c>) regardless of what path the configured URL carried.</summary>
    internal static string ResourceRoot(string endpoint)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)) return endpoint.TrimEnd('/');
        var path = uri.AbsolutePath;
        var cut = path.IndexOf("/openai", StringComparison.OrdinalIgnoreCase);
        var builder = new UriBuilder(uri) { Query = string.Empty, Path = cut >= 0 ? path[..cut] : path };
        return builder.Uri.ToString().TrimEnd('/');
    }

    private string DeploymentUrl(ProviderSettings settings, string deployment, string leaf, IReadOnlyDictionary<string, object?>? extras)
    {
        var url = $"{ResourceRoot(ResolveEndpoint(settings))}/openai/deployments/{Uri.EscapeDataString(deployment)}/{leaf}";
        var version = ExtrasString(extras, "api-version") ?? settings.ApiVersion ?? DefaultApiVersion;
        return WithQuery(url, "api-version", version);
    }

    private static string DeploymentFor(ProviderSettings settings, string model) => settings.Deployment ?? model;

    private Task AuthAsync(HttpRequestMessage http, ProviderSettings settings, CancellationToken cancellationToken)
        => ApplyAuthAsync(http, settings with { TokenScope = settings.TokenScope ?? DefaultTokenScope }, cancellationToken, allowTokenProvider: true);

    public override async Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var settings = Settings;
        var model = ResolveModel(settings, request.Model);
        var requested = request.Model with { Provider = Key, Model = model };
        var body = OpenAiChatWire.BuildBody(request, model, CollectSystem(request), ResolveMaxOutputTokens(request, settings), ResolveTemperature(request), stream: false, openAiDialect: true);

        using var http = JsonPost(DeploymentUrl(settings, DeploymentFor(settings, model), "chat/completions", request.Extras), body);
        await AuthAsync(http, settings, cancellationToken).ConfigureAwait(false);

        var watch = Stopwatch.StartNew();
        var response = await SendForBodyAsync(CreateClient(), http, requested, cancellationToken).ConfigureAwait(false);
        return OpenAiChatWire.ParseResponse(response, Key, requested, watch.Elapsed);
    }

    public override async IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = Settings;
        var model = ResolveModel(settings, request.Model);
        var requested = request.Model with { Provider = Key, Model = model };
        var body = OpenAiChatWire.BuildBody(request, model, CollectSystem(request), ResolveMaxOutputTokens(request, settings), ResolveTemperature(request), stream: true, openAiDialect: true);

        using var http = JsonPost(DeploymentUrl(settings, DeploymentFor(settings, model), "chat/completions", request.Extras), body, accept: "text/event-stream");
        await AuthAsync(http, settings, cancellationToken).ConfigureAwait(false);

        using var response = await SendForStreamAsync(CreateClient(), http, requested, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var delta in OpenAiChatWire.ReadStreamAsync(stream, Key, requested, cancellationToken).ConfigureAwait(false))
        {
            yield return delta;
        }
    }

    public override async Task<ImageResult> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken)
    {
        var settings = Settings;
        var model = request.Model.HasModel ? request.Model.Model : "gpt-image-1";
        var requested = request.Model with { Provider = Key, Model = model };

        using var http = JsonPost(DeploymentUrl(settings, model, "images/generations", request.Extras), OpenAiResponsesProvider.BuildImageBody(request, model));
        await AuthAsync(http, settings, cancellationToken).ConfigureAwait(false);

        var client = CreateClient();
        var watch = Stopwatch.StartNew();
        var body = await SendForBodyAsync(client, http, requested, cancellationToken).ConfigureAwait(false);
        return await OpenAiResponsesProvider.ParseImagesAsync(body, requested, request.OutputFormat, watch, client, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
    {
        var settings = Settings;
        var model = request.Model.HasModel ? request.Model.Model : throw new LlmConfigurationException("azureopenai embeddings need an explicit deployment/model.", Key);
        var requested = request.Model with { Provider = Key, Model = model };

        using var http = JsonPost(DeploymentUrl(settings, model, "embeddings", request.Extras), OpenAiChatWire.BuildEmbeddingBody(request, model));
        await AuthAsync(http, settings, cancellationToken).ConfigureAwait(false);

        var watch = Stopwatch.StartNew();
        var body = await SendForBodyAsync(CreateClient(), http, requested, cancellationToken).ConfigureAwait(false);
        return OpenAiChatWire.ParseEmbeddings(body, Key, requested, watch.Elapsed);
    }
}
