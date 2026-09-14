namespace Fx.ControlKit.Llm;

public enum LlmOperation
{
    Chat,
    Stream,
    Embed,
    Image,
    ListModels,
}

/// <summary>
/// Identity of one logical call as seen by <see cref="ILlmCallObserver"/>.
/// Carries sizes, never content: <see cref="PromptLength"/> is a character
/// count of the prompt text.
/// </summary>
public sealed record LlmCallContext(
    Guid CallId,
    LlmOperation Operation,
    ModelRef Model,
    int PromptLength,
    int MessageCount,
    DateTimeOffset StartedAt)
{
    /// <summary>1-based attempt number; incremented by the retry policy before each retry.</summary>
    public int Attempt { get; internal set; } = 1;
}

public sealed record LlmCallOutcome(
    ModelRef Resolved,
    LlmUsage? Usage,
    TimeSpan Elapsed,
    string? FinishReason,
    int OutputLength,
    int Attempts);

/// <summary>
/// Hook for logging, pricing and metrics. Every method has a no-op default so
/// an implementation overrides only what it needs. Observers are invoked
/// synchronously on the calling path; keep them cheap and never throw.
/// </summary>
public interface ILlmCallObserver
{
    void OnStarted(LlmCallContext call) { }

    void OnCompleted(LlmCallContext call, LlmCallOutcome outcome) { }

    void OnFailed(LlmCallContext call, Exception error, TimeSpan elapsed) { }

    /// <summary>Raised before each retry sleep; <paramref name="delay"/> is the back-off about to be waited.</summary>
    void OnRetrying(LlmCallContext call, Exception error, int nextAttempt, TimeSpan delay) { }
}
