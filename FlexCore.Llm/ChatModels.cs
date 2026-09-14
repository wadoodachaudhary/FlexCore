using System.Text.Json;

namespace Fx.ControlKit.Llm;

public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

public enum ResponseFormat
{
    Text,
    Json,
    JsonSchema,
}

/// <summary>One piece of a message: text, an inline image, a tool call the assistant made, or a tool result.</summary>
public abstract record ContentPart;

public sealed record TextPart(string Text) : ContentPart;

/// <summary>Inline image bytes; sent base64-encoded with <paramref name="MimeType"/> (image/png, image/jpeg, image/webp, image/gif).</summary>
public sealed record ImagePart(ReadOnlyMemory<byte> Bytes, string MimeType) : ContentPart
{
    public string ToBase64() => Convert.ToBase64String(Bytes.Span);
    public string ToDataUrl() => $"data:{MimeType};base64,{ToBase64()}";
}

/// <summary>A tool call previously returned by the assistant, replayed on an <see cref="ChatRole.Assistant"/> message when continuing a tool conversation.</summary>
public sealed record ToolCallPart(ToolCall Call) : ContentPart;

/// <summary>The host's answer to a tool call, sent on a <see cref="ChatRole.Tool"/> message.</summary>
public sealed record ToolResultPart(string CallId, string Content, string? Name = null, bool IsError = false) : ContentPart;

public sealed record ChatMessage(ChatRole Role, IReadOnlyList<ContentPart> Parts)
{
    public static ChatMessage System(string text) => new(ChatRole.System, new ContentPart[] { new TextPart(text) });
    public static ChatMessage User(string text) => new(ChatRole.User, new ContentPart[] { new TextPart(text) });
    public static ChatMessage User(params ContentPart[] parts) => new(ChatRole.User, parts);
    public static ChatMessage Assistant(string text) => new(ChatRole.Assistant, new ContentPart[] { new TextPart(text) });
    public static ChatMessage Assistant(params ContentPart[] parts) => new(ChatRole.Assistant, parts);
    public static ChatMessage ToolResult(string callId, string content, string? name = null, bool isError = false)
        => new(ChatRole.Tool, new ContentPart[] { new ToolResultPart(callId, content, name, isError) });

    /// <summary>All text parts joined with newlines — the plain-text view of the message.</summary>
    public string Text => string.Join("\n", Parts.OfType<TextPart>().Select(p => p.Text));

    /// <summary>Character count of every text part, for observers that log prompt size without the prompt.</summary>
    public int TextLength => Parts.OfType<TextPart>().Sum(p => p.Text.Length);
}

/// <summary>A function the model may call. <paramref name="Parameters"/> is a JSON Schema object.</summary>
public sealed record ToolDefinition(string Name, string? Description, JsonElement? Parameters);

public sealed record ToolChoice(string Mode, string? FunctionName = null)
{
    public static readonly ToolChoice Auto = new("auto");
    public static readonly ToolChoice None = new("none");
    public static readonly ToolChoice Required = new("required");
    public static ToolChoice Function(string name) => new("function", name);
}

/// <summary>A function call the model asked for. <paramref name="ArgumentsJson"/> is the raw JSON object text.</summary>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// Reasoning knobs. <paramref name="Effort"/> is low/medium/high for OpenAI
/// and OpenAI-compatible models; <paramref name="Enabled"/> switches Anthropic
/// extended thinking, Gemini thinking and Ollama <c>think</c> on or off;
/// <paramref name="BudgetTokens"/> caps thinking tokens where the provider
/// takes a budget (Anthropic, Gemini).
/// </summary>
public sealed record ReasoningOptions(string? Effort = null, bool? Enabled = null, int? BudgetTokens = null);

/// <summary>Token counts as reported by the provider. Cache counters are null when the provider has no such concept.</summary>
public readonly record struct LlmUsage(int Input, int Output, int? CacheRead = null, int? CacheWrite = null)
{
    public int Total => Input + Output + (CacheRead ?? 0) + (CacheWrite ?? 0);

    public static LlmUsage operator +(LlmUsage a, LlmUsage b) => new(
        a.Input + b.Input,
        a.Output + b.Output,
        a.CacheRead is null && b.CacheRead is null ? null : (a.CacheRead ?? 0) + (b.CacheRead ?? 0),
        a.CacheWrite is null && b.CacheWrite is null ? null : (a.CacheWrite ?? 0) + (b.CacheWrite ?? 0));
}

/// <summary>
/// Provider-neutral finish reasons. Providers map their own vocabulary onto
/// these; anything unmapped is passed through verbatim.
/// </summary>
public static class FinishReasons
{
    public const string Stop = "stop";
    /// <summary>Output was cut off at <see cref="ChatRequest.MaxOutputTokens"/> (or the provider's own cap).</summary>
    public const string MaxTokens = "max_tokens";
    public const string ToolCalls = "tool_calls";
    public const string ContentFilter = "content_filter";
    public const string Error = "error";
}

public sealed record ChatRequest
{
    public required ModelRef Model { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>System prompt. Sent through each provider's native system channel; System-role messages in <see cref="Messages"/> are folded in too.</summary>
    public string? System { get; init; }
    public double? Temperature { get; init; }
    public int? MaxOutputTokens { get; init; }
    public ResponseFormat ResponseFormat { get; init; } = ResponseFormat.Text;
    /// <summary>JSON Schema for <see cref="ResponseFormat.JsonSchema"/>.</summary>
    public JsonElement? Schema { get; init; }
    /// <summary>Name given to the schema where the provider wants one (OpenAI); defaults to "response".</summary>
    public string? SchemaName { get; init; }
    public IReadOnlyList<ToolDefinition>? Tools { get; init; }
    public ToolChoice? ToolChoice { get; init; }
    public ReasoningOptions? Reasoning { get; init; }
    public IReadOnlyList<string>? StopSequences { get; init; }
    /// <summary>Overrides the provider and global timeout for this call. Streaming treats it as an idle timeout between deltas.</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>
    /// Provider-specific knobs merged into the wire payload: Ollama option
    /// names such as <c>num_ctx</c> land in <c>options</c>, <c>api-version</c>
    /// overrides the Azure query string, anything else is written at the top
    /// level of the request body.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Extras { get; init; }

    public static ChatRequest FromPrompt(ModelRef model, string user, string? system = null)
        => new() { Model = model, Messages = new[] { ChatMessage.User(user) }, System = system };

    /// <summary>Total characters of prompt text (system + every text part). Never the text itself.</summary>
    public int PromptLength => (System?.Length ?? 0) + Messages.Sum(m => m.TextLength);
}

public sealed record ChatResult(
    string Text,
    ModelRef Resolved,
    LlmUsage? Usage,
    string? FinishReason,
    IReadOnlyList<ToolCall>? ToolCalls,
    TimeSpan Elapsed,
    string? RawJson)
{
    /// <summary>True when the provider stopped at the output-token cap; the text is incomplete.</summary>
    public bool IsTruncated => string.Equals(FinishReason, FinishReasons.MaxTokens, StringComparison.Ordinal);
    public bool HasToolCalls => ToolCalls is { Count: > 0 };
}

public sealed record ChatDelta(string? TextDelta, LlmUsage? Usage, string? FinishReason, bool IsFinal)
{
    /// <summary>Tool calls accumulated over the stream; populated on the final delta only.</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
    /// <summary>Model the provider actually served, when it reports one; populated on the final delta.</summary>
    public ModelRef? Resolved { get; init; }

    public static ChatDelta Text(string text) => new(text, null, null, false);
    public static ChatDelta Final(LlmUsage? usage, string? finishReason, IReadOnlyList<ToolCall>? toolCalls = null, ModelRef? resolved = null)
        => new(null, usage, finishReason, true) { ToolCalls = toolCalls, Resolved = resolved };
}
