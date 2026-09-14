namespace Fx.ControlKit.Llm.Configuration;

/// <summary>
/// User-editable settings for one model, keyed by the model id as it appears
/// in pickers (<c>qwen3:32b</c>, <c>claude-sonnet-4-6</c>,
/// <c>cloud-ollama:deepseek-v4-pro:cloud</c>). <see cref="LlmClient"/> applies
/// <see cref="TimeoutSeconds"/>, <see cref="MaxOutputTokens"/> and
/// <see cref="DefaultTemperature"/> to every call whose request leaves them
/// unset; the <see cref="Chunking.IChunkPlanner"/> reads
/// <see cref="ContextTokens"/>, <see cref="ChunkChars"/> and
/// <see cref="ChunkOnlyWhenTooLarge"/>. Serialised with camel-case names, so
/// files written by GhostWriter's store load unchanged.
/// </summary>
/// <param name="Id">Model id; the store's primary key.</param>
/// <param name="DisplayName">Label for pickers; null falls back to the catalog name.</param>
/// <param name="ContextTokens">Estimated window (input + output) in tokens; null uses the <see cref="Pricing.ILlmContextBudget"/> table.</param>
/// <param name="ChunkChars">Target chunk size in characters when the text must be split (or when <see cref="ChunkOnlyWhenTooLarge"/> is false).</param>
/// <param name="TimeoutSeconds">Wall-clock cap per call. <see cref="Sanitized"/> (applied on every save and load) replaces a non-positive value with <see cref="DefaultTimeoutSeconds"/>, so a stored entry always carries a timeout.</param>
/// <param name="MaxOutputTokens">Output cap; 0 means no cap from this config.</param>
/// <param name="DefaultTemperature">Temperature when the request sets none; clamped to 0–2.</param>
/// <param name="ChunkOnlyWhenTooLarge">True splits only when the text exceeds the window; false always splits to <see cref="ChunkChars"/>.</param>
/// <param name="Display">Whether the model appears in the user's picker.</param>
public sealed record LlmModelConfig(
    string Id,
    string? DisplayName = null,
    int? ContextTokens = null,
    int ChunkChars = 6_000,
    int TimeoutSeconds = 120,
    int MaxOutputTokens = 0,
    double DefaultTemperature = 0.7,
    bool ChunkOnlyWhenTooLarge = true,
    bool Display = true)
{
    public const int DefaultChunkChars = 6_000;
    public const int DefaultTimeoutSeconds = 120;
    public const double DefaultTemperatureValue = 0.7;

    public TimeSpan? Timeout => TimeoutSeconds > 0 ? TimeSpan.FromSeconds(TimeoutSeconds) : null;

    public int? MaxOutputTokensOrNull => MaxOutputTokens > 0 ? MaxOutputTokens : null;

    /// <summary>Baseline for a model nobody has described: 24K window, 6K chunks, 120 s, no output cap.</summary>
    public static LlmModelConfig Unknown(string id) => new(id, null, 24_000, DefaultChunkChars, DefaultTimeoutSeconds, 0, DefaultTemperatureValue, true, true);

    /// <summary>Clamps every field to a safe range so a hand-edited file cannot wedge the pipeline.</summary>
    public LlmModelConfig Sanitized()
    {
        var temperature = DefaultTemperature;
        if (double.IsNaN(temperature) || double.IsInfinity(temperature)) temperature = DefaultTemperatureValue;
        temperature = Math.Clamp(temperature, 0, 2);

        return this with
        {
            Id = (Id ?? string.Empty).Trim(),
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim(),
            ContextTokens = ContextTokens is > 0 ? ContextTokens : null,
            ChunkChars = ChunkChars > 0 ? ChunkChars : DefaultChunkChars,
            TimeoutSeconds = TimeoutSeconds > 0 ? TimeoutSeconds : DefaultTimeoutSeconds,
            MaxOutputTokens = MaxOutputTokens >= 0 ? MaxOutputTokens : 0,
            DefaultTemperature = temperature,
        };
    }
}
