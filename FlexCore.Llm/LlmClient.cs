using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Fx.ControlKit.Llm.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fx.ControlKit.Llm;

/// <summary>
/// Default <see cref="ILlmClient"/>. Routes by <see cref="ModelRef.Provider"/>
/// (an OpenAI-family request goes to whichever of <c>openai</c> /
/// <c>azureopenai</c> is configured when the named one is not), fills in
/// defaults from the user's model config and the request overrides, retries
/// transient failures per <see cref="RetryPolicy"/>, walks the model-fallback
/// chain on model-access refusals, enforces the per-request timeout through
/// a linked token (surfacing it as <see cref="LlmTimeoutException"/> rather
/// than the caller's own cancellation), and reports each call to every
/// <see cref="ILlmCallObserver"/>.
/// <para>
/// Precedence for the knobs a request leaves unset: per-user model config →
/// request override → provider settings → <see cref="LlmOptions"/> globals.
/// </para>
/// </summary>
public sealed class LlmClient : ILlmClient
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private const string LegacyCloudPrefix = "cloud-ollama:";

    private readonly Dictionary<string, ILlmProvider> _providers;
    private readonly LlmOptions _options;
    private readonly RetryPolicy _retry;
    private readonly ICredentialResolver? _credentials;
    private readonly ILlmRequestOverrides _overrides;
    private readonly IModelConfigStore _modelConfigs;
    private readonly ILlmCallObserver[] _observers;
    private readonly ILogger _logger;

    public LlmClient(
        IEnumerable<ILlmProvider> providers,
        IOptions<LlmOptions>? options = null,
        RetryPolicy? retryPolicy = null,
        ICredentialResolver? credentials = null,
        IEnumerable<ILlmCallObserver>? observers = null,
        ILogger<LlmClient>? logger = null,
        ILlmRequestOverrides? overrides = null,
        IModelConfigStore? modelConfigs = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = new Dictionary<string, ILlmProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            // Later registrations replace earlier ones so a host can swap an adapter.
            _providers[provider.Key] = provider;
        }

        _options = options?.Value ?? new LlmOptions();
        _retry = retryPolicy ?? RetryPolicy.FromOptions(_options.Retry);
        _credentials = credentials;
        _overrides = overrides ?? (_options.RequestOverrides.Count > 0 ? new LlmRequestOverrides(_options.RequestOverrides) : LlmRequestOverrides.Empty);
        _modelConfigs = modelConfigs ?? NullModelConfigStore.Instance;
        _observers = observers?.ToArray() ?? Array.Empty<ILlmCallObserver>();
        _logger = logger ?? NullLogger<LlmClient>.Instance;
        Providers = _providers.Values.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<ILlmProvider> Providers { get; }

    public bool TryResolve(string providerKey, out ILlmProvider? provider)
    {
        var key = ProviderKeys.Normalize(providerKey);
        if (key is not null && _providers.TryGetValue(key, out var found))
        {
            provider = found;
            return true;
        }

        provider = null;
        return false;
    }

    public ILlmProvider Resolve(string providerKey)
    {
        if (TryResolve(providerKey, out var provider)) return provider!;
        throw new LlmConfigurationException(
            ProviderKeys.IsKnown(providerKey)
                ? $"Provider '{providerKey}' is not registered."
                : $"'{providerKey}' is not a known LLM provider. Known keys: {string.Join(", ", ProviderKeys.All)}.",
            providerKey);
    }

    public ILlmProvider Resolve(ModelRef model) => Route(model).Provider;

    private (ILlmProvider Provider, ModelRef Model) Route(ModelRef model)
    {
        var providerKey = model.HasProvider ? ProviderKeys.Normalize(model.Provider) : ModelCatalog.InferProvider(model.Model);
        if (providerKey is null)
        {
            throw new LlmConfigurationException(
                model.HasProvider
                    ? $"'{model.Provider}' is not a known LLM provider."
                    : $"Cannot infer a provider for model '{model.Model}'; use the provider:model form.",
                model.Provider, model);
        }

        providerKey = PreferConfiguredOpenAiFamily(providerKey);
        return (Resolve(providerKey), model with { Provider = providerKey });
    }

    // openai and azureopenai serve the same models; a request for one goes to
    // the other when only the other has an endpoint/key (GhostWriter's
    // PreferAzureOpenAiSettings rule). Both configured: the named one wins.
    private string PreferConfiguredOpenAiFamily(string key)
    {
        var sibling = key switch
        {
            ProviderKeys.OpenAi => ProviderKeys.AzureOpenAi,
            ProviderKeys.AzureOpenAi => ProviderKeys.OpenAi,
            _ => null,
        };
        if (sibling is null) return key;
        if (_providers.TryGetValue(key, out var primary) && primary.IsConfigured) return key;
        if (_providers.TryGetValue(sibling, out var other) && other.IsConfigured) return sibling;
        return key;
    }

    private TimeSpan? ResolveTimeout(TimeSpan? requested, string providerKey)
    {
        if (requested is { } r) return r > TimeSpan.Zero ? r : null;
        var configured = _credentials?.Resolve(providerKey).Timeout;
        if (configured is { } c) return c;
        return _options.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(_options.TimeoutSeconds) : null;
    }

    private LlmModelConfig? FindModelConfig(string? userId, ModelRef model)
    {
        if (!model.HasModel) return null;
        return _modelConfigs.Find(userId, model.ToString())
               ?? _modelConfigs.Find(userId, model.Model)
               ?? (model.Provider == ProviderKeys.OllamaCloud ? _modelConfigs.Find(userId, LegacyCloudPrefix + model.Model) : null);
    }

    /// <summary>Fills the request's unset timeout, output cap and temperature from the user's model config, then the request override.</summary>
    private (ChatRequest Request, RequestOverride Override) Prepare(ChatRequest request, ModelRef model)
    {
        var config = FindModelConfig(request.UserId, model);
        var over = _overrides.Resolve(model);
        var prepared = request with
        {
            Model = model,
            Timeout = request.Timeout ?? config?.Timeout ?? over.Timeout,
            MaxOutputTokens = request.MaxOutputTokens ?? config?.MaxOutputTokensOrNull ?? over.MaxOutputTokens,
            Temperature = request.Temperature ?? config?.DefaultTemperature,
        };
        return (prepared, over);
    }

    private static LlmCallContext NewCall(LlmOperation operation, ModelRef model, int promptLength, int messageCount, string? userId = null)
        => new(Guid.NewGuid(), operation, model, promptLength, messageCount, DateTimeOffset.UtcNow) { UserId = userId };

    public Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (provider, model) = Route(request.Model);
        var (routed, over) = Prepare(request, model);
        var call = NewCall(LlmOperation.Chat, model, routed.PromptLength, routed.Messages.Count, routed.UserId);
        return ExecuteAsync(
            call,
            provider,
            ResolveTimeout(routed.Timeout, provider.Key),
            (current, token) => provider.ChatAsync(routed with { Model = current }, token),
            result => new LlmCallOutcome(result.Resolved, result.Usage, result.Elapsed, result.FinishReason, result.Text.Length, call.Attempt),
            over.RetryOnTimeout,
            cancellationToken);
    }

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        var provider = Resolve(providerKey);
        var call = NewCall(LlmOperation.ListModels, new ModelRef(provider.Key, string.Empty), 0, 0);
        var watch = Stopwatch.StartNew();
        return ExecuteAsync(
            call,
            provider,
            ResolveTimeout(null, provider.Key),
            (_, token) => provider.ListModelsAsync(token),
            result => new LlmCallOutcome(call.Model, null, watch.Elapsed, null, result.Count, call.Attempt),
            retryOnTimeout: false,
            cancellationToken);
    }

    public Task<ImageResult> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (provider, model) = Route(request.Model);
        var routed = request with { Model = model };
        var call = NewCall(LlmOperation.Image, model, routed.Prompt.Length, 1);
        return ExecuteAsync(
            call,
            provider,
            ResolveTimeout(routed.Timeout, provider.Key),
            (current, token) => provider.GenerateImageAsync(routed with { Model = current }, token),
            result => new LlmCallOutcome(result.Resolved, result.Usage, result.Elapsed, null, result.Images.Count, call.Attempt),
            retryOnTimeout: false,
            cancellationToken);
    }

    public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (provider, model) = Route(request.Model);
        var routed = request with { Model = model };
        var call = NewCall(LlmOperation.Embed, model, routed.Inputs.Sum(i => i.Length), routed.Inputs.Count);
        return ExecuteAsync(
            call,
            provider,
            ResolveTimeout(routed.Timeout, provider.Key),
            (current, token) => provider.EmbedAsync(routed with { Model = current }, token),
            result => new LlmCallOutcome(result.Resolved, result.Usage, result.Elapsed, null, result.Vectors.Count, call.Attempt),
            retryOnTimeout: false,
            cancellationToken);
    }

    public async IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (provider, model) = Route(request.Model);
        var (routed, over) = Prepare(request, model);
        var timeout = ResolveTimeout(routed.Timeout, provider.Key);
        var call = NewCall(LlmOperation.Stream, model, routed.PromptLength, routed.Messages.Count, routed.UserId);
        var watch = Stopwatch.StartNew();
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { model.Model };
        var timeoutRetried = false;
        Notify(o => o.OnStarted(call));

        while (true)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout is { } t) cts.CancelAfter(t);

            var enumerator = provider.StreamAsync(routed with { Model = call.Model }, cts.Token).GetAsyncEnumerator(cts.Token);
            var yielded = false;
            var outputLength = 0;
            LlmUsage? usage = null;
            string? finish = null;
            ModelRef? resolved = null;
            Exception? failure = null;
            try
            {
                while (true)
                {
                    var (moved, delta, error) = await StepAsync(enumerator).ConfigureAwait(false);
                    if (error is not null)
                    {
                        failure = error;
                        break;
                    }

                    if (!moved) break;

                    yielded = true;
                    if (timeout is { } idle) cts.CancelAfter(idle);
                    outputLength += delta.TextDelta?.Length ?? 0;
                    if (delta.Usage is { } u) usage = u;
                    if (delta.FinishReason is { } f) finish = f;
                    if (delta.Resolved is { } r) resolved = r;
                    yield return delta;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (failure is null)
            {
                Notify(o => o.OnCompleted(call, new LlmCallOutcome(resolved ?? call.Model, usage, watch.Elapsed, finish, outputLength, call.Attempt)));
                yield break;
            }

            if (failure is OperationCanceledException && !cancellationToken.IsCancellationRequested && cts.IsCancellationRequested && timeout is { } elapsedTimeout)
            {
                failure = new LlmTimeoutException(provider.Key, call.Model, elapsedTimeout, failure);
            }

            if (!yielded && !cancellationToken.IsCancellationRequested)
            {
                if (call.Attempt < _retry.MaxAttempts && _retry.IsTransient(failure))
                {
                    var next = call.Attempt + 1;
                    var delay = _retry.ComputeDelay(next, RetryPolicy.RetryAfterOf(failure));
                    _logger.LogWarning(failure, "{Provider} stream attempt {Attempt} failed; retrying in {Delay}ms", provider.Key, call.Attempt, (int)delay.TotalMilliseconds);
                    Notify(o => o.OnRetrying(call, failure, next, delay));
                    await _retry.Delay(delay, cancellationToken).ConfigureAwait(false);
                    call.Attempt = next;
                    continue;
                }

                if (failure is LlmTimeoutException && over.RetryOnTimeout && !timeoutRetried)
                {
                    timeoutRetried = true;
                    var next = call.Attempt + 1;
                    _logger.LogWarning(failure, "{Provider} stream timed out on attempt {Attempt}; retrying once", provider.Key, call.Attempt);
                    Notify(o => o.OnRetrying(call, failure, next, TimeSpan.Zero));
                    call.Attempt = next;
                    continue;
                }

                if (failure is LlmHttpException http && NextFallbackModel(provider, http, tried) is { } fallback)
                {
                    var next = call.Attempt + 1;
                    _logger.LogWarning("{Provider} refused model {Model}; trying {Fallback}", provider.Key, call.Model.Model, fallback);
                    Notify(o => o.OnRetrying(call, failure, next, TimeSpan.Zero));
                    call.Attempt = next;
                    call.Model = call.Model with { Model = fallback };
                    continue;
                }
            }

            Notify(o => o.OnFailed(call, failure, watch.Elapsed));
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static async Task<(bool Moved, ChatDelta Delta, Exception? Error)> StepAsync(IAsyncEnumerator<ChatDelta> enumerator)
    {
        try
        {
            var moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            return (moved, moved ? enumerator.Current : default!, null);
        }
        catch (Exception ex)
        {
            return (false, default!, ex);
        }
    }

    private async Task<T> ExecuteAsync<T>(
        LlmCallContext call,
        ILlmProvider provider,
        TimeSpan? timeout,
        Func<ModelRef, CancellationToken, Task<T>> operation,
        Func<T, LlmCallOutcome> outcome,
        bool retryOnTimeout,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { call.Model.Model };
        var timeoutRetried = false;
        Notify(o => o.OnStarted(call));

        while (true)
        {
            try
            {
                var model = call.Model;
                var result = await WithTimeoutAsync(provider.Key, model, timeout, token => operation(model, token), cancellationToken).ConfigureAwait(false);
                Notify(o => o.OnCompleted(call, outcome(result)));
                return result;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && call.Attempt < _retry.MaxAttempts && _retry.IsTransient(ex))
            {
                var next = call.Attempt + 1;
                var delay = _retry.ComputeDelay(next, RetryPolicy.RetryAfterOf(ex));
                _logger.LogWarning(ex, "{Provider} attempt {Attempt} failed; retrying in {Delay}ms", provider.Key, call.Attempt, (int)delay.TotalMilliseconds);
                Notify(o => o.OnRetrying(call, ex, next, delay));
                await _retry.Delay(delay, cancellationToken).ConfigureAwait(false);
                call.Attempt = next;
            }
            catch (LlmTimeoutException ex) when (retryOnTimeout && !timeoutRetried && !cancellationToken.IsCancellationRequested)
            {
                timeoutRetried = true;
                var next = call.Attempt + 1;
                _logger.LogWarning(ex, "{Provider} timed out on attempt {Attempt}; retrying once", provider.Key, call.Attempt);
                Notify(o => o.OnRetrying(call, ex, next, TimeSpan.Zero));
                call.Attempt = next;
            }
            catch (LlmHttpException ex) when (!cancellationToken.IsCancellationRequested && NextFallbackModel(provider, ex, tried) is { } fallback)
            {
                var next = call.Attempt + 1;
                _logger.LogWarning("{Provider} refused model {Model}; trying {Fallback}", provider.Key, call.Model.Model, fallback);
                Notify(o => o.OnRetrying(call, ex, next, TimeSpan.Zero));
                call.Attempt = next;
                call.Model = call.Model with { Model = fallback };
            }
            catch (Exception ex)
            {
                Notify(o => o.OnFailed(call, ex, watch.Elapsed));
                throw;
            }
        }
    }

    /// <summary>The next untried candidate from the fallback chain, or null when the error is not a model refusal or the chain is exhausted/disabled.</summary>
    private string? NextFallbackModel(ILlmProvider provider, LlmHttpException error, HashSet<string> tried)
    {
        var policy = _retry.ModelFallback;
        if (policy is null || !error.IsModelAccessError || !policy.AppliesTo(provider.Key)) return null;

        var candidates = new List<string>();
        if (policy.IncludeConfiguredModels)
        {
            var settings = _credentials?.Resolve(provider.Key);
            if (settings?.DefaultModel is { } configured) candidates.Add(configured);
            if (settings is not null) candidates.AddRange(settings.Models);
        }

        candidates.AddRange(policy.Models);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var id = candidate.Trim();
            var parsed = ModelRef.Parse(id);
            if (parsed.HasProvider)
            {
                if (!string.Equals(parsed.Provider, provider.Key, StringComparison.OrdinalIgnoreCase)) continue;
                id = parsed.Model;
            }

            if (id.Length > 0 && tried.Add(id)) return id;
        }

        return null;
    }

    private static async Task<T> WithTimeoutAsync<T>(string providerKey, ModelRef model, TimeSpan? timeout, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        if (timeout is not { } limit)
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(limit);
        try
        {
            return await operation(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && cts.IsCancellationRequested)
        {
            throw new LlmTimeoutException(providerKey, model, limit, ex);
        }
    }

    public async Task<ProviderProbe> ProbeAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var key = ProviderKeys.Normalize(providerKey) ?? providerKey;
        if (!TryResolve(providerKey, out var provider))
        {
            return new ProviderProbe(key, ProbeStatus.NotRegistered, false, watch.Elapsed, Message: $"No provider is registered as '{key}'.");
        }

        if (!provider!.IsConfigured)
        {
            var names = LlmEnvironmentVariables.For(provider.Key)?.ApiKey;
            var hint = names is { Length: > 0 } ? $" Set {string.Join(" or ", names)} or Llm:{provider.Key}." : string.Empty;
            return new ProviderProbe(provider.Key, ProbeStatus.NotConfigured, false, watch.Elapsed, Message: $"{provider.Key} is not configured.{hint}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProbeTimeout);
        try
        {
            if (provider.Supports(LlmCapabilities.ListModels))
            {
                var models = await provider.ListModelsAsync(cts.Token).ConfigureAwait(false);
                return new ProviderProbe(provider.Key, ProbeStatus.Ok, true, watch.Elapsed, ModelCount: models.Count, Message: $"{models.Count} model(s) listed.");
            }

            var request = ChatRequest.FromPrompt(new ModelRef(provider.Key, string.Empty), "ping") with { MaxOutputTokens = 1, Timeout = ProbeTimeout };
            var result = await provider.ChatAsync(request, cts.Token).ConfigureAwait(false);
            return new ProviderProbe(provider.Key, ProbeStatus.Ok, true, watch.Elapsed, Model: result.Resolved, Message: $"{result.Resolved} answered.");
        }
        catch (LlmHttpException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new ProviderProbe(provider.Key, ProbeStatus.AuthFailed, true, watch.Elapsed, Message: ex.Message);
        }
        catch (LlmConfigurationException ex)
        {
            return new ProviderProbe(provider.Key, ProbeStatus.NotConfigured, false, watch.Elapsed, Message: ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProviderProbe(provider.Key, ProbeStatus.Unreachable, true, watch.Elapsed, Message: $"No answer within {ProbeTimeout.TotalSeconds:0}s.");
        }
        catch (HttpRequestException ex)
        {
            return new ProviderProbe(provider.Key, ProbeStatus.Unreachable, true, watch.Elapsed, Message: ex.Message);
        }
        catch (Exception ex) when (ex is LlmException or IOException or NotSupportedException)
        {
            return new ProviderProbe(provider.Key, ProbeStatus.Failed, true, watch.Elapsed, Message: ex.Message);
        }
    }

    private void Notify(Action<ILlmCallObserver> action)
    {
        foreach (var observer in _observers)
        {
            try
            {
                action(observer);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ILlmCallObserver {Observer} threw and was ignored", observer.GetType().Name);
            }
        }
    }
}
