namespace Fx.ControlKit.Llm;

/// <summary>
/// What applications inject. Routes each call to the provider named by the
/// request's <see cref="ModelRef.Provider"/> (inferring it from the model id
/// when absent), fills in per-model defaults (user model config, request
/// overrides), applies the <see cref="RetryPolicy"/> and per-request timeout,
/// and reports every call to the registered <see cref="ILlmCallObserver"/>s.
/// </summary>
public interface ILlmClient
{
    IReadOnlyList<ILlmProvider> Providers { get; }

    /// <summary>Provider for a canonical key or alias. Throws <see cref="LlmConfigurationException"/> when no such provider is registered.</summary>
    ILlmProvider Resolve(string providerKey);

    /// <summary>Provider for a model reference; infers the provider from the model id when the reference has none.</summary>
    ILlmProvider Resolve(ModelRef model);

    bool TryResolve(string providerKey, out ILlmProvider? provider);

    Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string providerKey, CancellationToken cancellationToken = default);

    Task<ImageResult> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken = default);

    Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// One cheap authenticated call — the model list where the provider has
    /// one, otherwise a one-token chat with its default model — bypassing
    /// retries, so a settings page can show configured / reachable / rejected.
    /// Never throws for provider failures; they come back as the probe status.
    /// </summary>
    Task<ProviderProbe> ProbeAsync(string providerKey, CancellationToken cancellationToken = default);
}

public static class LlmClientExtensions
{
    /// <summary>Sends <paramref name="request"/> to <paramref name="model"/>, overriding the model the request carries.</summary>
    public static Task<ChatResult> ChatAsync(this ILlmClient client, ModelRef model, ChatRequest request, CancellationToken cancellationToken = default)
        => client.ChatAsync(request with { Model = model }, cancellationToken);

    public static IAsyncEnumerable<ChatDelta> StreamAsync(this ILlmClient client, ModelRef model, ChatRequest request, CancellationToken cancellationToken = default)
        => client.StreamAsync(request with { Model = model }, cancellationToken);

    /// <summary>One-shot prompt: optional system prompt plus a single user message.</summary>
    public static Task<ChatResult> ChatAsync(this ILlmClient client, ModelRef model, string user, string? system = null, CancellationToken cancellationToken = default)
        => client.ChatAsync(ChatRequest.FromPrompt(model, user, system), cancellationToken);

    public static IAsyncEnumerable<ChatDelta> StreamAsync(this ILlmClient client, ModelRef model, string user, string? system = null, CancellationToken cancellationToken = default)
        => client.StreamAsync(ChatRequest.FromPrompt(model, user, system), cancellationToken);

    /// <summary>Every registered provider whose configuration is present.</summary>
    public static IEnumerable<ILlmProvider> ConfiguredProviders(this ILlmClient client)
        => client.Providers.Where(p => p.IsConfigured);

    /// <summary>Probes every provider (only the configured ones by default) in parallel.</summary>
    public static async Task<IReadOnlyList<ProviderProbe>> ProbeAllAsync(this ILlmClient client, bool configuredOnly = true, CancellationToken cancellationToken = default)
    {
        var providers = configuredOnly ? client.ConfiguredProviders() : client.Providers;
        var probes = await Task.WhenAll(providers.Select(p => client.ProbeAsync(p.Key, cancellationToken))).ConfigureAwait(false);
        return probes;
    }

    /// <summary>Starts a conversation on <paramref name="model"/>; each <see cref="ChatSession.AskAsync(string, CancellationToken)"/> appends to it.</summary>
    public static ChatSession StartSession(this ILlmClient client, ModelRef model, string? system = null, ChatSessionOptions? options = null)
        => new(client, model, system, options);
}
