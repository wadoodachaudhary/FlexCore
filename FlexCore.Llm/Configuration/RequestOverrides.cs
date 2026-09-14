using Fx.ControlKit.Llm.Pricing;
using Microsoft.Extensions.Options;

namespace Fx.ControlKit.Llm.Configuration;

/// <summary>
/// One <c>Llm:RequestOverrides</c> entry. <see cref="Match"/> is compared
/// (equals, starts-with, contains; case-insensitive) against the provider
/// key, the model id, <c>provider:model</c> and the model's context-budget
/// key; the longest matching entry wins.
/// </summary>
public sealed class RequestOverrideOptions
{
    public string Match { get; set; } = string.Empty;

    /// <summary>Cap on output tokens for matching models (applied when the request sets none).</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>Per-call timeout for matching models.</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>Retry once when the call times out (slow small deployments that stall on cold start).</summary>
    public bool RetryOnTimeout { get; set; }

    /// <summary>Chunk size in characters for the <see cref="Chunking.IChunkPlanner"/> when the caller has none.</summary>
    public int? ChunkChars { get; set; }

    /// <summary>
    /// Named character limits for the context an application assembles
    /// around the user's text (<c>"SurroundingContext": 2000</c>,
    /// <c>"StylisticExemplars": 1200</c>, …); read back through
    /// <see cref="RequestOverride.ContextLimit"/> and <see cref="RequestOverride.Trim"/>.
    /// </summary>
    public Dictionary<string, int> ContextLimits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The resolved override for one model; <see cref="None"/> when nothing matched.</summary>
public sealed record RequestOverride(
    string? Match,
    int? MaxOutputTokens,
    TimeSpan? Timeout,
    bool RetryOnTimeout,
    int? ChunkChars,
    IReadOnlyDictionary<string, int> ContextLimits)
{
    public static readonly RequestOverride None = new(null, null, null, false, null, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

    public bool IsEmpty => Match is null;

    public int? ContextLimit(string name)
        => ContextLimits.TryGetValue(name, out var limit) && limit > 0 ? limit : null;

    /// <summary>Trims <paramref name="text"/> to the named limit; unchanged when no limit applies.</summary>
    public string? Trim(string name, string? text, bool keepTail = false)
        => ContextTrimmer.Trim(text, ContextLimit(name), keepTail);
}

public interface ILlmRequestOverrides
{
    RequestOverride Resolve(ModelRef model);
}

public sealed class LlmRequestOverrides : ILlmRequestOverrides
{
    public static readonly LlmRequestOverrides Empty = new(Array.Empty<RequestOverrideOptions>());

    private readonly Func<IReadOnlyList<RequestOverrideOptions>> _entries;
    private readonly ILlmContextBudget _budget;

    public LlmRequestOverrides(IEnumerable<RequestOverrideOptions> entries, ILlmContextBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var snapshot = entries.ToArray();
        _entries = () => snapshot;
        _budget = budget ?? LlmContextBudget.Default;
    }

    public LlmRequestOverrides(IOptionsMonitor<LlmOptions> options, ILlmContextBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _entries = () => options.CurrentValue.RequestOverrides ?? (IReadOnlyList<RequestOverrideOptions>)Array.Empty<RequestOverrideOptions>();
        _budget = budget ?? LlmContextBudget.Default;
    }

    public RequestOverride Resolve(ModelRef model)
    {
        var entries = _entries();
        if (entries.Count == 0 || !model.HasModel) return RequestOverride.None;

        var keys = LookupKeys(model);
        var hit = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Match))
            .OrderByDescending(entry => Normalize(entry.Match).Length)
            .FirstOrDefault(entry =>
            {
                var match = Normalize(entry.Match);
                return keys.Any(candidate =>
                    candidate.Equals(match, StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith(match, StringComparison.OrdinalIgnoreCase) ||
                    candidate.Contains(match, StringComparison.OrdinalIgnoreCase));
            });

        if (hit is null) return RequestOverride.None;
        return new RequestOverride(
            hit.Match,
            hit.MaxOutputTokens is > 0 ? hit.MaxOutputTokens : null,
            hit.TimeoutSeconds is > 0 ? TimeSpan.FromSeconds(hit.TimeoutSeconds.Value) : null,
            hit.RetryOnTimeout,
            hit.ChunkChars is > 0 ? hit.ChunkChars : null,
            new Dictionary<string, int>(hit.ContextLimits ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase));
    }

    private HashSet<string> LookupKeys(ModelRef model)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? value)
        {
            var normalized = Normalize(value);
            if (normalized.Length > 0) keys.Add(normalized);
        }

        Add(model.Provider);
        Add(model.Model);
        if (model.HasProvider) Add($"{model.Provider}:{model.Model}");

        var budgetKey = _budget.Lookup(model).Key;
        Add(budgetKey);
        var separator = budgetKey.IndexOf(':');
        if (separator >= 0 && separator + 1 < budgetKey.Length) Add(budgetKey[(separator + 1)..]);
        return keys;
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}

/// <summary>Cuts a block of context to a character limit, marking the cut with an ellipsis line.</summary>
public static class ContextTrimmer
{
    /// <param name="text">The block to cut; returned unchanged when it is within the limit.</param>
    /// <param name="maxChars">The limit; null or non-positive means no limit.</param>
    /// <param name="keepTail">True keeps the end of the text (context that precedes the passage), false keeps the start.</param>
    public static string? Trim(string? text, int? maxChars, bool keepTail)
    {
        if (string.IsNullOrWhiteSpace(text) || maxChars is null || maxChars <= 0 || text.Length <= maxChars)
        {
            return text;
        }

        return keepTail
            ? "...\n" + text[^maxChars.Value..].TrimStart()
            : text[..maxChars.Value].TrimEnd() + "\n...";
    }
}
