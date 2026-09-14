using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Fx.ControlKit.Llm;

/// <summary>Per-session request defaults; every field maps onto the matching <see cref="ChatRequest"/> member.</summary>
public sealed record ChatSessionOptions
{
    public double? Temperature { get; init; }
    public int? MaxOutputTokens { get; init; }
    public ResponseFormat ResponseFormat { get; init; } = ResponseFormat.Text;
    public JsonElement? Schema { get; init; }
    public string? SchemaName { get; init; }
    public IReadOnlyList<ToolDefinition>? Tools { get; init; }
    public ToolChoice? ToolChoice { get; init; }
    public ReasoningOptions? Reasoning { get; init; }
    public IReadOnlyList<string>? StopSequences { get; init; }
    public TimeSpan? Timeout { get; init; }
    public IReadOnlyDictionary<string, object?>? Extras { get; init; }
    public string? UserId { get; init; }

    /// <summary>When set, the oldest turns are dropped once the history holds more user messages than this.</summary>
    public int? MaxTurns { get; init; }
}

/// <summary>
/// A mutable conversation: a system prompt plus the message history, with
/// <see cref="AskAsync(string, CancellationToken)"/> and
/// <see cref="AskStreamingAsync(string, CancellationToken)"/> appending each
/// user turn and the assistant's reply. <see cref="RunToolsAsync"/> loops tool
/// calls through a handler until the model answers in text. Not thread-safe;
/// one session per conversation.
/// </summary>
public sealed class ChatSession
{
    private readonly ILlmClient _client;
    private readonly List<ChatMessage> _messages = new();

    public ChatSession(ILlmClient client, ModelRef model, string? system = null, ChatSessionOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Model = model;
        System = system;
        Options = options ?? new ChatSessionOptions();
    }

    public ModelRef Model { get; set; }

    public string? System { get; set; }

    public ChatSessionOptions Options { get; set; }

    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>Number of user turns in the history.</summary>
    public int Turns => _messages.Count(m => m.Role == ChatRole.User);

    public ChatResult? LastResult { get; private set; }

    /// <summary>Sum of the usage every reply reported.</summary>
    public LlmUsage TotalUsage { get; private set; }

    /// <summary>Text of the last assistant message, or empty.</summary>
    public string LastReply => _messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text ?? string.Empty;

    /// <summary>Clears the history; the system prompt, model and options stay.</summary>
    public void Reset()
    {
        _messages.Clear();
        LastResult = null;
        TotalUsage = default;
    }

    /// <summary>A copy that continues from the same history without touching this one.</summary>
    public ChatSession Fork()
    {
        var copy = new ChatSession(_client, Model, System, Options) { LastResult = LastResult, TotalUsage = TotalUsage };
        copy._messages.AddRange(_messages);
        return copy;
    }

    public void Add(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _messages.Add(message);
        TrimHistory();
    }

    public void AddUser(string text) => Add(ChatMessage.User(text));

    public void AddAssistant(string text) => Add(ChatMessage.Assistant(text));

    public void AddToolResult(string callId, string content, string? name = null, bool isError = false)
        => Add(ChatMessage.ToolResult(callId, content, name, isError));

    /// <summary>The request that <see cref="ContinueAsync"/> would send now.</summary>
    public ChatRequest BuildRequest() => new()
    {
        Model = Model,
        Messages = _messages.ToArray(),
        System = System,
        Temperature = Options.Temperature,
        MaxOutputTokens = Options.MaxOutputTokens,
        ResponseFormat = Options.ResponseFormat,
        Schema = Options.Schema,
        SchemaName = Options.SchemaName,
        Tools = Options.Tools,
        ToolChoice = Options.ToolChoice,
        Reasoning = Options.Reasoning,
        StopSequences = Options.StopSequences,
        Timeout = Options.Timeout,
        Extras = Options.Extras,
        UserId = Options.UserId,
    };

    public Task<ChatResult> AskAsync(string user, CancellationToken cancellationToken = default)
        => AskAsync(ChatMessage.User(user), cancellationToken);

    public Task<ChatResult> AskAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        Add(message);
        return ContinueAsync(cancellationToken);
    }

    /// <summary>Sends the history as it stands (after <see cref="AddToolResult"/>, or to regenerate) and appends the reply.</summary>
    public async Task<ChatResult> ContinueAsync(CancellationToken cancellationToken = default)
    {
        var result = await _client.ChatAsync(BuildRequest(), cancellationToken).ConfigureAwait(false);
        Record(result);
        return result;
    }

    public IAsyncEnumerable<ChatDelta> AskStreamingAsync(string user, CancellationToken cancellationToken = default)
        => AskStreamingAsync(ChatMessage.User(user), cancellationToken);

    public IAsyncEnumerable<ChatDelta> AskStreamingAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        Add(message);
        return ContinueStreamingAsync(cancellationToken);
    }

    /// <summary>Streams the reply; the assistant message is appended when the final delta arrives.</summary>
    public async IAsyncEnumerable<ChatDelta> ContinueStreamingAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        await foreach (var delta in _client.StreamAsync(BuildRequest(), cancellationToken).ConfigureAwait(false))
        {
            if (delta.TextDelta is { Length: > 0 } piece) text.Append(piece);
            if (delta.IsFinal)
            {
                Record(new ChatResult(text.ToString(), delta.Resolved ?? Model, delta.Usage, delta.FinishReason, delta.ToolCalls, TimeSpan.Zero, null));
            }

            yield return delta;
        }
    }

    /// <summary>
    /// Asks, then while the reply carries tool calls answers each through
    /// <paramref name="handler"/> and continues, up to <paramref name="maxRounds"/>
    /// rounds. The final text reply is returned; a handler exception is sent
    /// back to the model as an error result rather than aborting the loop.
    /// </summary>
    public async Task<ChatResult> RunToolsAsync(string user, Func<ToolCall, CancellationToken, Task<string>> handler, int maxRounds = 8, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var result = await AskAsync(user, cancellationToken).ConfigureAwait(false);
        for (var round = 0; round < maxRounds && result.HasToolCalls; round++)
        {
            foreach (var call in result.ToolCalls!)
            {
                string output;
                var isError = false;
                try
                {
                    output = await handler(call, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    output = ex.Message;
                    isError = true;
                }

                AddToolResult(call.Id, output, call.Name, isError);
            }

            result = await ContinueAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private void Record(ChatResult result)
    {
        LastResult = result;
        if (result.Usage is { } usage) TotalUsage += usage;

        var parts = new List<ContentPart>();
        if (result.Text.Length > 0) parts.Add(new TextPart(result.Text));
        foreach (var call in result.ToolCalls ?? Array.Empty<ToolCall>()) parts.Add(new ToolCallPart(call));
        if (parts.Count == 0) parts.Add(new TextPart(string.Empty));
        _messages.Add(new ChatMessage(ChatRole.Assistant, parts));
    }

    // Drops whole turns (a user message and everything up to the next user
    // message) from the front until the user-message count is within MaxTurns.
    private void TrimHistory()
    {
        if (Options.MaxTurns is not { } max || max <= 0) return;
        while (Turns > max)
        {
            var first = _messages.FindIndex(m => m.Role == ChatRole.User);
            if (first < 0) return;
            var next = _messages.FindIndex(first + 1, m => m.Role == ChatRole.User);
            _messages.RemoveRange(first, (next < 0 ? _messages.Count : next) - first);
        }
    }
}
