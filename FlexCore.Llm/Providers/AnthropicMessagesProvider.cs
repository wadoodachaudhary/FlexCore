using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fx.ControlKit.Llm.Configuration;
using Fx.ControlKit.Llm.Http;
using Microsoft.Extensions.Logging;

namespace Fx.ControlKit.Llm.Providers;

/// <summary>
/// Anthropic <c>/v1/messages</c>: <c>x-api-key</c> + <c>anthropic-version</c>
/// headers, the system prompt as a top-level field, tool use, extended
/// thinking, and SSE streaming (message_start / content_block_delta /
/// message_delta). Usage includes <c>cache_read_input_tokens</c> and
/// <c>cache_creation_input_tokens</c>. Anthropic has no JSON mode switch:
/// <see cref="ResponseFormat.Json"/> adds an instruction to the system
/// prompt, <see cref="ResponseFormat.JsonSchema"/> forces a synthetic tool
/// whose input schema is the caller's schema and returns its input as the
/// result text.
/// </summary>
public sealed class AnthropicMessagesProvider : LlmProviderBase
{
    public const string DefaultVersion = "2023-06-01";
    public const string StructuredOutputToolName = "structured_output";
    private const int DefaultMaxTokens = 4096;
    private const string CacheControlExtra = "cache_control";

    public AnthropicMessagesProvider(
        IHttpClientFactory httpClientFactory,
        ICredentialResolver credentials,
        ITokenProvider? tokenProvider = null,
        IEnumerable<ILlmRequestAuthenticator>? authenticators = null,
        ILogger<AnthropicMessagesProvider>? logger = null)
        : base(ProviderKeys.Anthropic, httpClientFactory, credentials, tokenProvider, authenticators, logger)
    {
    }

    public override LlmCapabilities Capabilities =>
        LlmCapabilities.Chat | LlmCapabilities.Streaming | LlmCapabilities.JsonMode | LlmCapabilities.JsonSchema
        | LlmCapabilities.Vision | LlmCapabilities.Tools | LlmCapabilities.Reasoning | LlmCapabilities.PromptCaching
        | LlmCapabilities.ListModels;

    public override bool IsConfigured => Settings.HasApiKey || Authenticator is not null;

    protected override string? DefaultEndpoint => "https://api.anthropic.com";
    protected override AuthMode DefaultAuthMode => AuthMode.ApiKeyHeader;
    protected override string DefaultHeaderName => "x-api-key";

    private string BaseUrl(ProviderSettings settings) => StripLeaves(ResolveEndpoint(settings), "/v1/messages", "/v1/models", "/v1");

    /// <summary>Anthropic ids use hyphens only; <c>claude_sonnet_4.6</c> and <c>claude-sonnet-4.6</c> become <c>claude-sonnet-4-6</c>.</summary>
    public static string NormalizeModelId(string model)
    {
        if (string.IsNullOrWhiteSpace(model) || !model.Contains("claude", StringComparison.OrdinalIgnoreCase)) return model;
        return model.Replace('_', '-').Replace('.', '-');
    }

    protected override string NormalizeModel(string id) => NormalizeModelId(id);

    private async Task<HttpRequestMessage> PrepareAsync(string url, JsonObject? body, ProviderSettings settings, string? accept, CancellationToken cancellationToken)
    {
        var http = body is null ? new HttpRequestMessage(HttpMethod.Get, url) : JsonPost(url, body, accept);
        await ApplyAuthAsync(http, settings, cancellationToken).ConfigureAwait(false);
        http.Headers.TryAddWithoutValidation("anthropic-version", settings.ApiVersion ?? DefaultVersion);
        return http;
    }

    public JsonObject BuildBody(ChatRequest request, string model, int maxTokens, double? temperature, bool stream)
    {
        var system = CollectSystem(request);
        if (request.ResponseFormat == ResponseFormat.Json)
        {
            const string instruction = "Respond with a single valid JSON value and nothing else — no prose, no code fences.";
            system = system is null ? instruction : system + "\n\n" + instruction;
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["messages"] = BuildMessages(request),
        };

        if (system is not null)
        {
            var cache = request.Extras is not null && request.Extras.TryGetValue(CacheControlExtra, out var flag) && flag is true or "ephemeral";
            body["system"] = cache
                ? new JsonArray(new JsonObject { ["type"] = "text", ["text"] = system, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } })
                : system;
        }

        var thinking = request.Reasoning?.Enabled == true;
        if (thinking)
        {
            body["thinking"] = new JsonObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = Math.Max(1024, Math.Min(request.Reasoning!.BudgetTokens ?? 1024, maxTokens - 1)),
            };
        }
        else if (temperature is { } t)
        {
            body["temperature"] = t;
        }

        if (request.StopSequences is { Count: > 0 } stops)
        {
            body["stop_sequences"] = new JsonArray(stops.Select(s => (JsonNode?)s).ToArray());
        }

        var tools = new JsonArray();
        foreach (var tool in request.Tools ?? Array.Empty<ToolDefinition>())
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = Json.ToNode(tool.Parameters) ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            });
        }

        if (request.ResponseFormat == ResponseFormat.JsonSchema)
        {
            tools.Add(new JsonObject
            {
                ["name"] = StructuredOutputToolName,
                ["description"] = "Record the answer in the required structure.",
                ["input_schema"] = Json.ToNode(request.Schema) ?? new JsonObject { ["type"] = "object" },
            });
            body["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = StructuredOutputToolName };
        }
        else if (request.Tools is { Count: > 0 } && request.ToolChoice is { } choice)
        {
            body["tool_choice"] = choice.Mode switch
            {
                "none" => new JsonObject { ["type"] = "none" },
                "required" => new JsonObject { ["type"] = "any" },
                "function" => new JsonObject { ["type"] = "tool", ["name"] = choice.FunctionName },
                _ => new JsonObject { ["type"] = "auto" },
            };
        }

        if (tools.Count > 0) body["tools"] = tools;
        if (stream) body["stream"] = true;

        Json.MergeExtras(body, request.Extras, key => key != CacheControlExtra);
        return body;
    }

    private static JsonArray BuildMessages(ChatRequest request)
    {
        var messages = new JsonArray();
        foreach (var message in request.Messages.Where(m => m.Role != ChatRole.System))
        {
            switch (message.Role)
            {
                case ChatRole.User:
                {
                    var content = new JsonArray();
                    foreach (var part in message.Parts)
                    {
                        switch (part)
                        {
                            case TextPart text:
                                content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                                break;
                            case ImagePart image:
                                content.Add(new JsonObject
                                {
                                    ["type"] = "image",
                                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.MimeType, ["data"] = image.ToBase64() },
                                });
                                break;
                            case ToolResultPart result:
                                content.Add(ToolResultBlock(result));
                                break;
                        }
                    }

                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = content.Count == 1 && content[0] is JsonObject { } only && only["type"]?.GetValue<string>() == "text"
                            ? only["text"]!.GetValue<string>()
                            : content,
                    });
                    break;
                }

                case ChatRole.Assistant:
                {
                    var content = new JsonArray();
                    foreach (var part in message.Parts)
                    {
                        switch (part)
                        {
                            case TextPart text when text.Text.Length > 0:
                                content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                                break;
                            case ToolCallPart call:
                                content.Add(new JsonObject
                                {
                                    ["type"] = "tool_use",
                                    ["id"] = call.Call.Id,
                                    ["name"] = call.Call.Name,
                                    ["input"] = ParseArguments(call.Call.ArgumentsJson),
                                });
                                break;
                        }
                    }

                    if (content.Count > 0) messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
                    break;
                }

                case ChatRole.Tool:
                {
                    var content = new JsonArray(message.Parts.OfType<ToolResultPart>().Select(r => (JsonNode?)ToolResultBlock(r)).ToArray());
                    if (content.Count > 0) messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                    break;
                }
            }
        }

        return messages;
    }

    private static JsonObject ToolResultBlock(ToolResultPart result)
    {
        var block = new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = result.CallId,
            ["content"] = result.Content,
        };
        if (result.IsError) block["is_error"] = true;
        return block;
    }

    private static JsonNode ParseArguments(string json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject { ["raw"] = json };
        }
    }

    public static string MapStopReason(string? raw) => raw switch
    {
        null or "" or "end_turn" or "stop_sequence" => FinishReasons.Stop,
        "max_tokens" => FinishReasons.MaxTokens,
        "tool_use" => FinishReasons.ToolCalls,
        "refusal" => FinishReasons.ContentFilter,
        _ => raw,
    };

    public static LlmUsage? ParseUsage(JsonElement container)
    {
        if (Json.GetObject(container, "usage") is not { } usage) return null;
        return new LlmUsage(
            Json.GetInt(usage, "input_tokens"),
            Json.GetInt(usage, "output_tokens"),
            Json.GetNullableInt(usage, "cache_read_input_tokens"),
            Json.GetNullableInt(usage, "cache_creation_input_tokens"));
    }

    public static ChatResult ParseResponse(string body, ModelRef requested, TimeSpan elapsed, bool structuredOutput)
    {
        using var doc = Json.ParseDocument(body, ProviderKeys.Anthropic);
        var root = doc.RootElement;
        if (Json.GetString(root, "type") == "error")
        {
            var error = Json.GetObject(root, "error");
            throw new LlmResponseException($"anthropic returned an error: {(error is { } e ? Json.GetString(e, "message") : null) ?? body}", ProviderKeys.Anthropic, requested, body);
        }

        var text = new StringBuilder();
        var toolCalls = new List<ToolCall>();
        string? structured = null;
        if (Json.GetArray(root, "content") is { } content)
        {
            foreach (var block in content.EnumerateArray())
            {
                switch (Json.GetString(block, "type"))
                {
                    case "text":
                        if (Json.GetString(block, "text") is { } t)
                        {
                            if (text.Length > 0) text.Append('\n');
                            text.Append(t);
                        }
                        break;
                    case "tool_use":
                    {
                        var name = Json.GetString(block, "name") ?? string.Empty;
                        var input = block.TryGetProperty("input", out var inputEl) ? inputEl.GetRawText() : "{}";
                        if (structuredOutput && name == StructuredOutputToolName)
                        {
                            structured = input;
                        }
                        else
                        {
                            toolCalls.Add(new ToolCall(Json.GetString(block, "id") ?? $"toolu_{toolCalls.Count}", name, input));
                        }
                        break;
                    }
                }
            }
        }

        var finish = MapStopReason(Json.GetString(root, "stop_reason"));
        if (structured is not null)
        {
            text.Clear().Append(structured);
            if (finish == FinishReasons.ToolCalls && toolCalls.Count == 0) finish = FinishReasons.Stop;
        }

        var served = Json.GetString(root, "model");
        return new ChatResult(text.ToString(), requested with { Model = served ?? requested.Model }, ParseUsage(root), finish, toolCalls.Count == 0 ? null : toolCalls, elapsed, body);
    }

    private int MaxTokensFor(ChatRequest request, ProviderSettings settings)
        => ResolveMaxOutputTokens(request, settings) ?? DefaultMaxTokens;

    public override async Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var settings = Settings;
        var model = ResolveModel(settings, request.Model);
        var requested = request.Model with { Provider = Key, Model = model };
        var body = BuildBody(request, model, MaxTokensFor(request, settings), ResolveTemperature(request), stream: false);

        using var http = await PrepareAsync(Combine(BaseUrl(settings), "v1/messages"), body, settings, null, cancellationToken).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        var response = await SendForBodyAsync(CreateClient(), http, requested, cancellationToken).ConfigureAwait(false);
        return ParseResponse(response, requested, watch.Elapsed, request.ResponseFormat == ResponseFormat.JsonSchema);
    }

    public override async IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = Settings;
        var model = ResolveModel(settings, request.Model);
        var requested = request.Model with { Provider = Key, Model = model };
        var structuredOutput = request.ResponseFormat == ResponseFormat.JsonSchema;
        var body = BuildBody(request, model, MaxTokensFor(request, settings), ResolveTemperature(request), stream: true);

        using var http = await PrepareAsync(Combine(BaseUrl(settings), "v1/messages"), body, settings, "text/event-stream", cancellationToken).ConfigureAwait(false);
        using var response = await SendForStreamAsync(CreateClient(), http, requested, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var usage = default(LlmUsage);
        var haveUsage = false;
        string? stopReason = null;
        string? served = null;
        var blocks = new Dictionary<int, (string Id, string Name, StringBuilder Input)>();
        var toolCalls = new List<ToolCall>();
        string? structured = null;

        await foreach (var evt in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            using var doc = Json.ParseDocument(evt.Data, Key);
            var root = doc.RootElement;
            var type = Json.GetString(root, "type") ?? evt.Event;
            switch (type)
            {
                case "message_start":
                    if (Json.GetObject(root, "message") is { } message)
                    {
                        served ??= Json.GetString(message, "model");
                        if (ParseUsage(message) is { } start)
                        {
                            usage = start;
                            haveUsage = true;
                        }
                    }
                    break;

                case "content_block_start":
                {
                    var index = Json.GetInt(root, "index");
                    if (Json.GetObject(root, "content_block") is { } block && Json.GetString(block, "type") == "tool_use")
                    {
                        blocks[index] = (Json.GetString(block, "id") ?? $"toolu_{index}", Json.GetString(block, "name") ?? string.Empty, new StringBuilder());
                    }
                    break;
                }

                case "content_block_delta":
                {
                    var index = Json.GetInt(root, "index");
                    if (Json.GetObject(root, "delta") is not { } delta) break;
                    switch (Json.GetString(delta, "type"))
                    {
                        case "text_delta":
                            if (Json.GetString(delta, "text") is { Length: > 0 } text) yield return ChatDelta.Text(text);
                            break;
                        case "input_json_delta":
                            if (blocks.TryGetValue(index, out var buffer) && Json.GetString(delta, "partial_json") is { } partial)
                            {
                                buffer.Input.Append(partial);
                            }
                            break;
                    }
                    break;
                }

                case "content_block_stop":
                {
                    var index = Json.GetInt(root, "index");
                    if (blocks.Remove(index, out var done))
                    {
                        var input = done.Input.Length == 0 ? "{}" : done.Input.ToString();
                        if (structuredOutput && done.Name == StructuredOutputToolName)
                        {
                            structured = input;
                        }
                        else
                        {
                            toolCalls.Add(new ToolCall(done.Id, done.Name, input));
                        }
                    }
                    break;
                }

                case "message_delta":
                    if (Json.GetObject(root, "delta") is { } md && Json.GetString(md, "stop_reason") is { } sr) stopReason = sr;
                    if (Json.GetObject(root, "usage") is { } mu)
                    {
                        usage = usage with
                        {
                            Output = Json.GetInt(mu, "output_tokens", usage.Output),
                            Input = Json.GetNullableInt(mu, "input_tokens") ?? usage.Input,
                            CacheRead = Json.GetNullableInt(mu, "cache_read_input_tokens") ?? usage.CacheRead,
                            CacheWrite = Json.GetNullableInt(mu, "cache_creation_input_tokens") ?? usage.CacheWrite,
                        };
                        haveUsage = true;
                    }
                    break;

                case "error":
                {
                    var error = Json.GetObject(root, "error");
                    throw new LlmResponseException($"anthropic stream error: {(error is { } e ? Json.GetString(e, "message") : null) ?? evt.Data}", Key, requested, evt.Data);
                }
            }
        }

        if (structured is not null)
        {
            yield return ChatDelta.Text(structured);
        }

        var finish = MapStopReason(stopReason);
        if (structured is not null && finish == FinishReasons.ToolCalls && toolCalls.Count == 0) finish = FinishReasons.Stop;
        yield return ChatDelta.Final(haveUsage ? usage : null, finish, toolCalls.Count == 0 ? null : toolCalls, requested with { Model = served ?? model });
    }

    public override async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var settings = Settings;
        using var http = await PrepareAsync(Combine(BaseUrl(settings), "v1/models?limit=100"), null, settings, null, cancellationToken).ConfigureAwait(false);
        var body = await SendForBodyAsync(CreateClient(), http, null, cancellationToken).ConfigureAwait(false);

        using var doc = Json.ParseDocument(body, Key);
        var list = new List<ModelInfo>();
        if (Json.GetArray(doc.RootElement, "data") is { } data)
        {
            foreach (var item in data.EnumerateArray())
            {
                var id = Json.GetString(item, "id");
                if (string.IsNullOrEmpty(id) || !id.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) continue;
                DateTimeOffset? created = DateTimeOffset.TryParse(Json.GetString(item, "created_at"), out var when) ? when : null;
                var known = ModelCatalog.Find(new ModelRef(Key, id));
                list.Add(new ModelInfo(Key, id)
                {
                    DisplayName = Json.GetString(item, "display_name"),
                    Created = created,
                    ContextTokens = known?.ContextTokens,
                    MaxOutputTokens = known?.MaxOutputTokens,
                });
            }
        }

        // An empty listing (a key scoped to no models) falls back to the configured list, as GhostWriter's picker does.
        return list.Count > 0 ? list : ConfiguredModels;
    }
}
