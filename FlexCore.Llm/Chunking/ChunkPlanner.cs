using System.Text;
using System.Text.RegularExpressions;
using Fx.ControlKit.Llm.Configuration;
using Fx.ControlKit.Llm.Pricing;

namespace Fx.ControlKit.Llm.Chunking;

/// <summary>What to fit into one call: the text plus the size of everything sent around it.</summary>
public sealed record ChunkRequest
{
    public required ModelRef Model { get; init; }
    public required string Text { get; init; }

    /// <summary>Characters of prompt sent alongside the text on every call (system prompt, instructions, context blocks).</summary>
    public int FixedPromptChars { get; init; }

    /// <summary>Exact token count for the fixed prompt when known; otherwise estimated from <see cref="FixedPromptChars"/>.</summary>
    public int? FixedPromptTokens { get; init; }

    /// <summary>Output room to keep free; defaults to the budget's reserve.</summary>
    public int? ReservedOutputTokens { get; init; }

    /// <summary>Overrides the window from the budget table / model config.</summary>
    public int? ContextTokens { get; init; }

    /// <summary>Hard chunk target in characters; implies splitting even when the text fits.</summary>
    public int? TargetChunkChars { get; init; }

    /// <summary>Null defers to the user's model config (default true).</summary>
    public bool? ChunkOnlyWhenTooLarge { get; init; }

    /// <summary>Selects the per-user model config consulted for window and chunk size.</summary>
    public string? UserId { get; init; }
}

/// <summary>One slice of the text, with the separator that joined it to the next slice and short previews of its neighbours.</summary>
public sealed record TextChunk(
    int Index,
    string Text,
    string SeparatorToNext,
    int EstimatedTokens,
    string? PreviousPreview,
    string? NextPreview);

public sealed record ChunkPlan(
    ModelRef Model,
    ContextBudget Budget,
    int TextTokens,
    int AvailableTokens,
    IReadOnlyList<TextChunk> Chunks)
{
    public bool IsSplit => Chunks.Count > 1;
    public int Count => Chunks.Count;
}

/// <summary>The fixed prompt leaves too little room for even the smallest chunk.</summary>
public sealed class ChunkPlanException : LlmException
{
    public ChunkPlanException(string message, ModelRef model) : base(message, model.Provider, model)
    {
    }
}

/// <summary>
/// Decides whether a text fits in one call for a model and, when it does not,
/// splits it on paragraph, then sentence, then word boundaries into chunks
/// that do. The numbers are the ones GhostWriter's chunker uses: a 1,024-token
/// safety margin under the window and a 384-token minimum chunk.
/// </summary>
public interface IChunkPlanner
{
    ChunkPlan Plan(ChunkRequest request);

    /// <summary>True when the text fits in one call with the fixed prompt and output reserve.</summary>
    bool Fits(ChunkRequest request);

    /// <summary>Joins per-chunk outputs back together with the separators the chunks were cut on.</summary>
    string Stitch(IReadOnlyList<TextChunk> chunks, IReadOnlyList<string> outputs);
}

public sealed class ChunkPlanner : IChunkPlanner
{
    public const int SafetyMarginTokens = 1_024;
    public const int MinimumChunkTokens = 384;
    public const int PreviewChars = 320;

    public static readonly ChunkPlanner Default = new(LlmContextBudget.Default);

    private static readonly Regex ParagraphBreak = new(@"(\n\s*\n+)", RegexOptions.Compiled);
    private static readonly Regex SentenceBreak = new(@"(?<=[\.\!\?])\s+", RegexOptions.Compiled);
    private static readonly Regex InlineWhitespace = new(@"[^\S\r\n]+", RegexOptions.Compiled);

    private readonly ILlmContextBudget _budget;
    private readonly IModelConfigStore? _modelConfigs;

    public ChunkPlanner(ILlmContextBudget budget, IModelConfigStore? modelConfigs = null)
    {
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _modelConfigs = modelConfigs;
    }

    public bool Fits(ChunkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (budget, _) = ResolveBudget(request);
        return _budget.EstimateTokens(request.Text) <= Available(request, budget);
    }

    public ChunkPlan Plan(ChunkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (budget, config) = ResolveBudget(request);
        var text = request.Text ?? string.Empty;
        var textTokens = _budget.EstimateTokens(text);
        var available = Available(request, budget);

        var onlyWhenTooLarge = request.ChunkOnlyWhenTooLarge ?? config?.ChunkOnlyWhenTooLarge ?? true;
        var targetChars = request.TargetChunkChars ?? (onlyWhenTooLarge ? null : config?.ChunkChars);
        var fits = textTokens <= available;

        if (fits && targetChars is null)
        {
            return new ChunkPlan(request.Model, budget, textTokens, available, new[] { new TextChunk(1, text, string.Empty, textTokens, null, null) });
        }

        if (available < MinimumChunkTokens)
        {
            throw new ChunkPlanException(
                $"The fixed prompt around this text already consumes most of {request.Model}'s ~{budget.EstimatedContextTokens:N0}-token budget ({available} tokens left, {MinimumChunkTokens} needed). Use a larger-window model or shorten the instructions/context.",
                request.Model);
        }

        var maxTokens = available;
        if (targetChars is > 0)
        {
            maxTokens = Math.Min(maxTokens, Math.Max(MinimumChunkTokens, (targetChars.Value + 3) / 4));
        }

        var chunks = Split(text, maxTokens);
        if (chunks.Count == 0)
        {
            throw new ChunkPlanException("The text could not be split into chunkable passages.", request.Model);
        }

        return new ChunkPlan(request.Model, budget, textTokens, available, chunks);
    }

    public string Stitch(IReadOnlyList<TextChunk> chunks, IReadOnlyList<string> outputs)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(outputs);
        var sb = new StringBuilder();
        var hasWritten = false;
        for (var i = 0; i < outputs.Count; i++)
        {
            var text = outputs[i]?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (hasWritten && i > 0)
            {
                var separator = i - 1 < chunks.Count ? chunks[i - 1].SeparatorToNext : string.Empty;
                sb.Append(string.IsNullOrEmpty(separator) ? "\n\n" : separator);
            }

            sb.Append(text);
            hasWritten = true;
        }

        return sb.ToString().Trim();
    }

    private (ContextBudget Budget, LlmModelConfig? Config) ResolveBudget(ChunkRequest request)
    {
        var budget = _budget.Lookup(request.Model);
        var config = _modelConfigs?.Find(request.UserId, request.Model.Model);
        var window = request.ContextTokens ?? config?.ContextTokens;
        if (window is > 0) budget = budget with { EstimatedContextTokens = window.Value };
        return (budget, config);
    }

    private int Available(ChunkRequest request, ContextBudget budget)
    {
        var fixedTokens = request.FixedPromptTokens ?? (request.FixedPromptChars + 3) / 4;
        var reserved = request.ReservedOutputTokens ?? budget.ReservedOutputTokens;
        return budget.EstimatedContextTokens - fixedTokens - reserved - SafetyMarginTokens;
    }

    private IReadOnlyList<TextChunk> Split(string text, int maxTokens)
    {
        var units = SplitIntoUnits(text, maxTokens);
        var chunks = new List<TextChunk>();
        var currentUnits = new List<ChunkUnit>();
        var currentText = new StringBuilder();

        void Flush()
        {
            if (currentUnits.Count == 0) return;
            var chunkText = currentText.ToString().Trim();
            if (chunkText.Length > 0)
            {
                chunks.Add(new TextChunk(chunks.Count + 1, chunkText, currentUnits[^1].SeparatorAfter, _budget.EstimateTokens(chunkText), null, null));
            }

            currentUnits.Clear();
            currentText.Clear();
        }

        foreach (var unit in units)
        {
            var candidate = currentUnits.Count == 0 ? unit.Text : currentText + currentUnits[^1].SeparatorAfter + unit.Text;
            if (currentUnits.Count > 0 && _budget.EstimateTokens(candidate) > maxTokens)
            {
                Flush();
                currentText.Append(unit.Text);
                currentUnits.Add(unit);
                continue;
            }

            if (currentUnits.Count > 0) currentText.Append(currentUnits[^1].SeparatorAfter);
            currentText.Append(unit.Text);
            currentUnits.Add(unit);
        }

        Flush();

        return chunks
            .Select((chunk, idx) => chunk with
            {
                PreviousPreview = idx > 0 ? Preview(chunks[idx - 1].Text, takeStart: false) : null,
                NextPreview = idx + 1 < chunks.Count ? Preview(chunks[idx + 1].Text, takeStart: true) : null,
            })
            .ToList();
    }

    private sealed record ChunkUnit(string Text, string SeparatorAfter);

    private List<ChunkUnit> SplitIntoUnits(string text, int maxUnitTokens)
    {
        var normalized = text.Replace("\r\n", "\n");
        var parts = ParagraphBreak.Split(normalized);
        var units = new List<ChunkUnit>();

        for (var i = 0; i < parts.Length; i += 2)
        {
            var paragraph = parts[i].Trim();
            var separator = i + 1 < parts.Length ? "\n\n" : string.Empty;
            if (paragraph.Length == 0) continue;

            if (_budget.EstimateTokens(paragraph) <= maxUnitTokens)
            {
                units.Add(new ChunkUnit(paragraph, separator));
                continue;
            }

            units.AddRange(SplitParagraph(paragraph, separator, maxUnitTokens));
        }

        return units;
    }

    private IEnumerable<ChunkUnit> SplitParagraph(string paragraph, string paragraphSeparator, int maxUnitTokens)
    {
        var sentences = SentenceBreak.Split(paragraph);
        if (sentences.Length <= 1)
        {
            return SplitSentence(paragraph, paragraphSeparator, maxUnitTokens);
        }

        var units = new List<ChunkUnit>();
        foreach (var part in sentences)
        {
            var sentence = part.Trim();
            if (sentence.Length == 0) continue;
            if (_budget.EstimateTokens(sentence) <= maxUnitTokens)
            {
                units.Add(new ChunkUnit(sentence, " "));
            }
            else
            {
                units.AddRange(SplitSentence(sentence, " ", maxUnitTokens));
            }
        }

        if (units.Count == 0)
        {
            return SplitSentence(paragraph, paragraphSeparator, maxUnitTokens);
        }

        units[^1] = units[^1] with { SeparatorAfter = paragraphSeparator };
        return units;
    }

    private IEnumerable<ChunkUnit> SplitSentence(string sentence, string finalSeparator, int maxUnitTokens)
    {
        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return Array.Empty<ChunkUnit>();

        var units = new List<ChunkUnit>();
        var current = new StringBuilder();

        void Flush(string separator)
        {
            if (current.Length == 0) return;
            units.Add(new ChunkUnit(current.ToString(), separator));
            current.Clear();
        }

        foreach (var word in words)
        {
            if (_budget.EstimateTokens(word) > maxUnitTokens)
            {
                Flush(" ");
                units.AddRange(SplitHard(word, " ", maxUnitTokens));
                continue;
            }

            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && _budget.EstimateTokens(candidate) > maxUnitTokens)
            {
                Flush(" ");
            }

            if (current.Length > 0) current.Append(' ');
            current.Append(word);
        }

        Flush(finalSeparator);
        if (units.Count > 0)
        {
            units[^1] = units[^1] with { SeparatorAfter = finalSeparator };
        }

        return units;
    }

    private static IEnumerable<ChunkUnit> SplitHard(string text, string finalSeparator, int maxUnitTokens)
    {
        var maxChars = Math.Max(32, maxUnitTokens * 4);
        var units = new List<ChunkUnit>();
        for (var start = 0; start < text.Length; start += maxChars)
        {
            var length = Math.Min(maxChars, text.Length - start);
            units.Add(new ChunkUnit(text.Substring(start, length), " "));
        }

        if (units.Count > 0)
        {
            units[^1] = units[^1] with { SeparatorAfter = finalSeparator };
        }

        return units;
    }

    private static string? Preview(string text, bool takeStart)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = InlineWhitespace.Replace(text.Replace("\r\n", "\n").Trim(), " ");
        if (normalized.Length <= PreviewChars) return normalized;
        return takeStart
            ? normalized[..PreviewChars].TrimEnd() + "..."
            : "..." + normalized[^PreviewChars..].TrimStart();
    }
}
