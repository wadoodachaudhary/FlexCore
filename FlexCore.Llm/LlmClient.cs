using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Fx.ControlKit.Llm.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fx.ControlKit.Llm;

/// <summary>
/// Default <see cref="ILlmClient"/>. Routes by <see cref="ModelRef.Provider"/>,
/// retries transient failures per <see cref="RetryPolicy"/>, enforces the
/// per-request timeout through a linked token (surfacing it as
/// <see cref="LlmTimeoutException"/> rather than the caller's own
/// cancellation), and reports each call to every <see cref="ILlmCallObserver"/>.
/// </summary>
public sealed class LlmClient : ILlmClient
{
    private readonly Dictionary<string, ILlmProvider> _providers;
    private readonly LlmOptions _options;
    private readonly RetryPolicy _retry;
    private readonly ICredentialResolver? _credentials;
    private readonly ILlmCallObserver[] _observers;
    private readonly ILogger _logger;

    public LlmClient(
        IEnumerable<ILlmProvider> providers,
        IOptions<LlmOptions>? options = null,
        RetryPolicy? retryPolicy = null,
        ICredentialResolver? credentials = null,
        IEnumerable<ILlmCallObserver>? observers = null,
        ILogger<LlmClient>? logger = null)
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

        return (Resolve(providerKey), model with { Provider = providerKey });
    }

    private TimeSpan? ResolveTimeout(TimeSpan? requested, string providerKey)
    {
        if (requested is { } r) return r > TimeSpan.Zero ? r : null;
        var configured = _credentials?.Resolve(providerKey).Timeout;
        if (configured is { } c) return c;
        return _options.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(_options.TimeoutSeconds) : null;
    }

    public Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (provider, model) = Route(request.Model);
        var routed = request with { Model = model };
        var call = new LlmCallContext(Guid.NewGuid(), LlmOperation.Chat, model, routed.PromptLength, routed.Messages.Count, DateTimeOffset.UtcNow);
        return ExecuteAsync(
            call,
            provider,
            ResolveTimeout(routed.Timeout, provider.Key),
            token => provider.ChatAsync(routed, token),
            result => new LlmCallOutcome(result.Resolved, result.Usage, result.Elapsed, result.FinishReason, result.Text.Length, call.Attempt),
            cancellationToken);
    }

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        var provider = Resolve(providerKey);
        var call = new LlmCallContext(Guid.NewGuid(), LlmOperation.ListModels, new ModelRef(provider.Key, string.Empty), 0, 0, DateTimeOffset.UtcNow);
        var watch = Stopwatch.StartNew();
        return ExecuteAsync(
            call,
            provider,
            ResolveTimeout(null, provider.Key),
            provider.ListModelsAsync,
            result => new LlmCallOutcome(call.Model, null, watch.Elapsed, null, result.Count, call.Attempt),
            cancellationToken);
    }

    public Task<ImageResult> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (provider, model) = Route(request.Model);
        var routed = request with { Model = model };
        var call = new LlmCallContext(Guid.NewGuid(), LlmOperation.Image, model, routed.Prompt.Length, 1, DateTimeOffset.UtcNow);
        return ExecuteAsync(
            call,
            provider,
            ResolveTimeout(routed.Timeout, provider.Key),
            token => provider.GenerateImageAsync(routed, token),
            result => new LlmCallOutcome(result.Resolved, result.Usage, result.Elapsed, null, result.Images.Count, call.Attempt),
            cancellationToken);
    }

    public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (provider, model) = Route(request.Model);
        var routed = request with { Model = model };
        var call = new LlmCallContext(Guid.NewGuid(), LlmOperation.Embed, model, routed.Inputs.Sum(i => i.Length), routed.Inputs.Count, DateTimeOffset.UtcNow);
        return ExecuteAsync(
            call,
            provider,
            ResolveTimeout(routed.Timeout, provider.Key),
            token => provider.EmbedAsync(routed, token),
            result => new LlmCallOutcome(result.Resolved, result.Usage, result.Elapsed, null, result.Vectors.Count, call.Attempt),
            cancellationToken);
    }

    public async IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (provider, model) = Route(request.Model);
        var routed = request with { Model = model };
        var timeout = ResolveTimeout(routed.Timeout, provider.Key);
        var call = new LlmCallContext(Guid.NewGuid(), LlmOperation.Stream, model, routed.PromptLength, routed.Messages.Count, DateTimeOffset.UtcNow);
        var watch = Stopwatch.StartNew();
        Notify(o => o.OnStarted(call));

        while (true)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout is { } t) cts.CancelAfter(t);

            var enumerator = provider.StreamAsync(routed, cts.Token).GetAsyncEnumerator(cts.Token);
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
                Notify(o => o.OnCompleted(call, new LlmCallOutcome(resolved ?? model, usage, watch.Elapsed, finish, outputLength, call.Attempt)));
                yield break;
            }

            if (failure is OperationCanceledException && !cancellationToken.IsCancellationRequested && cts.IsCancellationRequested && timeout is { } elapsedTimeout)
            {
                failure = new LlmTimeoutException(provider.Key, model, elapsedTimeout, failure);
            }

            if (!yielded && !cancellationToken.IsCancellationRequested && call.Attempt < _retry.MaxAttempts && _retry.IsTransient(failure))
            {
                var next = call.Attempt + 1;
                var delay = _retry.ComputeDelay(next, RetryPolicy.RetryAfterOf(failure));
                _logger.LogWarning(failure, "{Provider} stream attempt {Attempt} failed; retrying in {Delay}ms", provider.Key, call.Attempt, (int)delay.TotalMilliseconds);
                Notify(o => o.OnRetrying(call, failure, next, delay));
                await _retry.Delay(delay, cancellationToken).ConfigureAwait(false);
                call.Attempt = next;
                continue;
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
        Func<CancellationToken, Task<T>> operation,
        Func<T, LlmCallOutcome> outcome,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        Notify(o => o.OnStarted(call));

        while (true)
        {
            try
            {
                var result = await WithTimeoutAsync(provider.Key, call.Model, timeout, operation, cancellationToken).ConfigureAwait(false);
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
            catch (Exception ex)
            {
                Notify(o => o.OnFailed(call, ex, watch.Elapsed));
                throw;
            }
        }
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
