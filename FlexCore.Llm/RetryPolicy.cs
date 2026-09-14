using Fx.ControlKit.Llm.Configuration;

namespace Fx.ControlKit.Llm;

/// <summary>
/// Exponential back-off for transient provider failures: HTTP 429 (honouring
/// <c>Retry-After</c>), 502, 503, 504, 529, bodies reporting an overloaded
/// upstream, in-band error events that report the same conditions after an
/// HTTP 200 (<see cref="LlmResponseException.IsTransient"/> — Anthropic's
/// streamed <c>overloaded_error</c> / <c>rate_limit_error</c>), and
/// connection-level <see cref="HttpRequestException"/>s.
/// Timeouts and the caller's own cancellation are never retried. A streaming
/// call is retried only while nothing has been yielded yet.
/// <see cref="ModelFallback"/> adds an opt-in second axis: when a provider
/// refuses the model itself (<see cref="LlmHttpException.IsModelAccessError"/>)
/// the call is repeated with the next candidate model.
/// </summary>
public sealed class RetryPolicy
{
    public static readonly RetryPolicy None = new() { MaxAttempts = 1 };

    /// <summary>Total attempts including the first. 1 disables retries.</summary>
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
    public bool UseJitter { get; init; } = true;

    /// <summary>Overrides the transient test; return null to fall back to the built-in classification.</summary>
    public Func<Exception, bool?>? ShouldRetry { get; init; }

    /// <summary>Test seam; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>Null (the default) disables model fallback.</summary>
    public ModelFallbackPolicy? ModelFallback { get; init; }

    public static RetryPolicy FromOptions(RetryOptions options) => new()
    {
        MaxAttempts = Math.Max(1, options.MaxAttempts),
        BaseDelay = TimeSpan.FromSeconds(Math.Max(0, options.BaseDelaySeconds)),
        MaxDelay = TimeSpan.FromSeconds(Math.Max(0, options.MaxDelaySeconds)),
        UseJitter = options.UseJitter,
        ModelFallback = options.FallbackModels is { Count: > 0 } models
            ? new ModelFallbackPolicy
            {
                Models = models.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).ToArray(),
                Providers = options.FallbackProviders.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray(),
                IncludeConfiguredModels = options.FallbackIncludesConfiguredModels,
            }
            : null,
    };

    /// <summary>A copy of this policy with a different model-fallback chain (null removes it).</summary>
    public RetryPolicy WithModelFallback(ModelFallbackPolicy? fallback) => new()
    {
        MaxAttempts = MaxAttempts,
        BaseDelay = BaseDelay,
        MaxDelay = MaxDelay,
        UseJitter = UseJitter,
        ShouldRetry = ShouldRetry,
        Delay = Delay,
        ModelFallback = fallback,
    };

    public bool IsTransient(Exception error)
    {
        if (ShouldRetry?.Invoke(error) is { } decided) return decided;
        return error switch
        {
            LlmHttpException http => http.IsTransient,
            LlmResponseException response => response.IsTransient,
            LlmTimeoutException => false,
            OperationCanceledException => false,
            HttpRequestException => true,
            IOException => true,
            _ => false,
        };
    }

    /// <summary>Delay before <paramref name="nextAttempt"/> (2-based). A server-supplied <c>Retry-After</c> wins over the back-off curve, capped at <see cref="MaxDelay"/>.</summary>
    public TimeSpan ComputeDelay(int nextAttempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } fromServer && fromServer > TimeSpan.Zero)
        {
            return fromServer <= MaxDelay ? fromServer : MaxDelay;
        }

        var exponent = Math.Max(0, nextAttempt - 2);
        var delay = BaseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        if (UseJitter)
        {
            delay *= 0.8 + Random.Shared.NextDouble() * 0.4;
        }

        var capped = Math.Min(delay, MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(capped);
    }

    internal static TimeSpan? RetryAfterOf(Exception error)
        => error is LlmHttpException http ? http.RetryAfter : null;
}

/// <summary>
/// Which models to try, in order, when a provider answers 403/404 saying the
/// requested model is not available to the API key. Candidates are the
/// provider's configured <c>Models</c> (when <see cref="IncludeConfiguredModels"/>)
/// followed by <see cref="Models"/>; each is tried once per call.
/// </summary>
public sealed class ModelFallbackPolicy
{
    /// <summary>The chain GhostWriter used for public OpenAI: gpt-5.4 → gpt-5.2 → gpt-5 → gpt-4.1 → gpt-4.1-mini → gpt-4o-mini.</summary>
    public static readonly IReadOnlyList<string> OpenAiChain = new[] { "gpt-5.4", "gpt-5.2", "gpt-5", "gpt-4.1", "gpt-4.1-mini", "gpt-4o-mini" };

    /// <summary>Fallback models, in order. A <c>provider:model</c> entry applies to that provider only.</summary>
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();

    /// <summary>Also try the provider's configured default model and <c>Models</c> list before <see cref="Models"/>.</summary>
    public bool IncludeConfiguredModels { get; init; } = true;

    /// <summary>Provider keys the chain applies to. Empty means every provider; the default is OpenAI only, where refusals by model are common.</summary>
    public IReadOnlyList<string> Providers { get; init; } = new[] { ProviderKeys.OpenAi };

    public static ModelFallbackPolicy OpenAiDefaults => new() { Models = OpenAiChain };

    public bool AppliesTo(string providerKey)
        => Providers.Count == 0 || Providers.Any(p => string.Equals(ProviderKeys.Normalize(p) ?? p, providerKey, StringComparison.OrdinalIgnoreCase));
}
