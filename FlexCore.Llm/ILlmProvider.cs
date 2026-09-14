namespace Fx.ControlKit.Llm;

/// <summary>
/// One wire adapter. Adapters are thin: they build the provider's request
/// body, apply auth, parse the response and stream. Retry, timeout and
/// observation live in <see cref="ILlmClient"/>, so a provider call is a
/// single HTTP round trip.
/// <para>
/// A provider throws <see cref="NotSupportedException"/> from any member whose
/// flag is missing from <see cref="Capabilities"/> (for example
/// <see cref="GenerateImageAsync"/> on Anthropic). Check the flag before
/// calling when the model is user-selected. The one exception is
/// <see cref="ListModelsAsync"/>: a provider without
/// <see cref="LlmCapabilities.ListModels"/> (no listing API — Azure OpenAI,
/// Azure AI Foundry) returns <see cref="ConfiguredModels"/> instead of throwing.
/// </para>
/// </summary>
public interface ILlmProvider
{
    /// <summary>Canonical lower-case key (<see cref="ProviderKeys"/>).</summary>
    string Key { get; }

    LlmCapabilities Capabilities { get; }

    /// <summary>True when the key/endpoint this provider needs are present (environment first, then configuration).</summary>
    bool IsConfigured { get; }

    /// <summary>The models named in configuration (default model + <c>Models</c>), or the <see cref="ModelCatalog"/> entries for this provider when none are; never a network call.</summary>
    IReadOnlyList<ModelInfo> ConfiguredModels => ModelCatalog.ForProvider(Key).ToList();

    Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken);

    IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, CancellationToken cancellationToken);

    /// <summary>Live listing when the provider has <see cref="LlmCapabilities.ListModels"/>; otherwise <see cref="ConfiguredModels"/>.</summary>
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken);

    Task<ImageResult> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken);

    Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken);
}

public static class LlmProviderExtensions
{
    public static bool Supports(this ILlmProvider provider, LlmCapabilities capability)
        => (provider.Capabilities & capability) == capability;
}
