using Fx.ControlKit.Llm.Pricing;

namespace Fx.ControlKit.Llm;

public enum LlmCallStatus
{
    Completed,
    Failed,
}

/// <summary>
/// One finished call as an audit-log row: who, which provider and model, how
/// many tokens, what it cost, how long it took, and whether it succeeded.
/// Carries sizes only, never prompt or response text.
/// </summary>
public sealed record LlmCallRecord(
    Guid CallId,
    DateTimeOffset StartedAt,
    LlmOperation Operation,
    string Provider,
    string Model,
    string? UserId,
    LlmUsage? Usage,
    double? EstimatedCostUsd,
    TimeSpan Elapsed,
    LlmCallStatus Status,
    string? FinishReason,
    int Attempts,
    int PromptLength,
    int OutputLength,
    string? Error)
{
    public bool Succeeded => Status == LlmCallStatus.Completed;
}

/// <summary>Where <see cref="CallRecordingObserver"/> delivers records: a database table, a file, an in-memory ring.</summary>
public interface ILlmCallRecordSink
{
    void Record(LlmCallRecord record);
}

public sealed class DelegateCallRecordSink : ILlmCallRecordSink
{
    private readonly Action<LlmCallRecord> _record;

    public DelegateCallRecordSink(Action<LlmCallRecord> record)
    {
        _record = record ?? throw new ArgumentNullException(nameof(record));
    }

    public void Record(LlmCallRecord record) => _record(record);
}

/// <summary>Keeps the most recent records in memory for a dashboard or a diagnostics page.</summary>
public sealed class InMemoryCallLog : ILlmCallRecordSink
{
    private readonly object _lock = new();
    private readonly Queue<LlmCallRecord> _records = new();

    public InMemoryCallLog(int capacity = 500)
    {
        Capacity = Math.Max(1, capacity);
    }

    public int Capacity { get; }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<LlmCallRecord> Recent
    {
        get
        {
            lock (_lock) return _records.Reverse().ToList();
        }
    }

    public event Action<LlmCallRecord>? Recorded;

    public void Record(LlmCallRecord record)
    {
        lock (_lock)
        {
            _records.Enqueue(record);
            while (_records.Count > Capacity) _records.Dequeue();
        }

        Recorded?.Invoke(record);
    }

    public void Clear()
    {
        lock (_lock) _records.Clear();
    }
}

/// <summary>Turns observer events into <see cref="LlmCallRecord"/>s, priced through <see cref="ILlmPricing"/>, and hands them to every sink.</summary>
public sealed class CallRecordingObserver : ILlmCallObserver
{
    private readonly ILlmCallRecordSink[] _sinks;
    private readonly ILlmPricing? _pricing;

    public CallRecordingObserver(IEnumerable<ILlmCallRecordSink> sinks, ILlmPricing? pricing = null)
    {
        _sinks = sinks?.ToArray() ?? Array.Empty<ILlmCallRecordSink>();
        _pricing = pricing;
    }

    public CallRecordingObserver(ILlmCallRecordSink sink, ILlmPricing? pricing = null)
        : this(new[] { sink ?? throw new ArgumentNullException(nameof(sink)) }, pricing)
    {
    }

    public void OnCompleted(LlmCallContext call, LlmCallOutcome outcome)
    {
        var cost = outcome.Usage is { } usage ? _pricing?.EstimateCostUsd(outcome.Resolved, usage) : null;
        Deliver(new LlmCallRecord(
            call.CallId, call.StartedAt, call.Operation, outcome.Resolved.Provider, outcome.Resolved.Model, call.UserId,
            outcome.Usage, cost, outcome.Elapsed, LlmCallStatus.Completed, outcome.FinishReason, outcome.Attempts,
            call.PromptLength, outcome.OutputLength, null));
    }

    public void OnFailed(LlmCallContext call, Exception error, TimeSpan elapsed)
    {
        Deliver(new LlmCallRecord(
            call.CallId, call.StartedAt, call.Operation, call.Model.Provider, call.Model.Model, call.UserId,
            null, null, elapsed, LlmCallStatus.Failed, null, call.Attempt,
            call.PromptLength, 0, error.Message));
    }

    private void Deliver(LlmCallRecord record)
    {
        foreach (var sink in _sinks)
        {
            sink.Record(record);
        }
    }
}
