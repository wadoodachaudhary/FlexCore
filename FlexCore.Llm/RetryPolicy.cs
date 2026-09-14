using Fx.ControlKit.Llm.Configuration;

namespace Fx.ControlKit.Llm;

/// <summary>
/// Exponential back-off for transient provider failures: HTTP 429 (honouring
/// <c>Retry-After</c>), 502, 503, 504, 529, bodies reporting an overloaded
/// upstream, and connection-level <see cref="HttpRequestException"/>s.
/// Timeouts and the caller's own cancellation are never retried. A streaming
/// call is retried only while nothing has been yielded yet.
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

    public static RetryPolicy FromOptions(RetryOptions options) => new()
    {
        MaxAttempts = Math.Max(1, options.MaxAttempts),
        BaseDelay = TimeSpan.FromSeconds(Math.Max(0, options.BaseDelaySeconds)),
        MaxDelay = TimeSpan.FromSeconds(Math.Max(0, options.MaxDelaySeconds)),
        UseJitter = options.UseJitter,
    };

    public bool IsTransient(Exception error)
    {
        if (ShouldRetry?.Invoke(error) is { } decided) return decided;
        return error switch
        {
            LlmHttpException http => http.IsTransient,
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
