using System.Text.Json;
using System.Text.Json.Nodes;
using Fx.ControlKit.Llm.Http;

namespace Fx.ControlKit.Llm.Providers;

/// <summary>
/// The OpenAI Chat Completions wire format, shared by every host that
/// speaks it (OpenAI-compatible endpoints and Azure OpenAI deployments).
/// </summary>
public static class OpenAiChatWire
{
    /// <summary>gpt-5*, o-series and codex models take <c>max_completion_tokens</c> and <c>reasoning_effort</c>, and reject <c>temperature</c>.</summary>
    public static bool IsReasoningModel(string model)
    {
        var m = model.ToLowerInvariant();
        var bare = m.Contains('/') ? m[(m.LastIndexOf('/') + 1)..] : m;
        return bare.StartsWith("gpt-5", StringComparison.Ordinal)
            || bare.StartsWith("o1", StringComparison.Ordinal)
            || bare.StartsWith("o3", StringComparison.Ordinal)
            || bare.StartsWith("o4", StringComparison.Ordinal)
            || bare.Contains("codex", StringComparison.Ordinal);
    }

    /// <summary>Builds the request body. <paramref name="openAiDialect"/> is true for OpenAI/Azure OpenAI hosts, where reasoning models need <c>max_completion_tokens</c>; false keeps the widely supported <c>max_tokens</c>.</summary>
    public static JsonObject BuildBody(
        ChatRequest request,
        string model,
        string? system,
        int? maxOutputTokens,
        double? temperature,
        bool stream,
        bool openAiDialect)
    {
        var reasoningModel = openAiDialect && IsReasoningModel(model);
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = BuildMessages(request, system),
        };

        if (temperature is { } t && !reasoningModel) body["temperature"] = t;
        if (maxOutputTokens is { } max)
        {
            body[reasoningModel ? "max_completion_tokens" : "max_tokens"] = max;
        }

        if (request.Reasoning?.Effort is { } effort)
        {
            body["reasoning_effort"] = effort;
        }

        if (request.StopSequences is { Count: > 0 } stops)
        {
            body["stop"] = new JsonArray(stops.Select(s => (JsonNode?)s).ToArray());
        }

        switch (request.ResponseFormat)
        {
            case ResponseFormat.Json:
                body["response_format"] = new JsonObject { ["type"] = "json_object" };
                break;
            case ResponseFormat.JsonSchema:
                body["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = request.SchemaName ?? "response",
                        ["schema"] = Json.ToNode(request.Schema) ?? new JsonObject { ["type"] = "object" },
                        ["strict"] = true,
                    },
                };
                break;
        }

        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode?)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = Json.ToNode(t.Parameters) ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                },
            }).ToArray());

            if (request.ToolChoice is { } choice)
            {
                body["tool_choice"] = choice.Mode == "function"
                    ? new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = choice.FunctionName } }
                    : choice.Mode;
            }
        }

        if (stream)
        {
            body["stream"] = true;
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        Json.MergeExtras(body, request.Extras, key => !key.StartsWith("api-", StringComparison.OrdinalIgnoreCase));
        return body;
    }

    public static JsonArray BuildMessages(ChatRequest request, string? system)
    {
        var messages = new JsonArray();
        if (system is not null)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        }

        foreach (var message in request.Messages.Where(m => m.Role != ChatRole.System))
        {
            switch (message.Role)
            {
                case ChatRole.User:
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = BuildUserContent(message) });
                    break;

                case ChatRole.Assistant:
                {
                    var text = message.Text;
                    var node = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = text.Length == 0 ? null : text,
                    };
                    var calls = message.Parts.OfType<ToolCallPart>().Select(p => p.Call).ToList();
                    if (calls.Count > 0)
                    {
                        node["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode?)new JsonObject
                        {
                            ["id"] = c.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
                        }).ToArray());
                    }
                    messages.Add(node);
                    break;
                }

                case ChatRole.Tool:
                    foreach (var result in message.Parts.OfType<ToolResultPart>())
                    {
                        messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = result.CallId,
                            ["content"] = result.Content,
                        });
                    }
                    break;
            }
        }

        return messages;
    }

    private static JsonNode BuildUserContent(ChatMessage message)
    {
        if (message.Parts.Count == 1 && message.Parts[0] is TextPart only)
        {
            return only.Text;
        }

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
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = image.ToDataUrl() },
                    });
                    break;
                case ToolResultPart result:
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = result.Content });
                    break;
            }
        }

        return content;
    }

    public static string MapFinishReason(string? raw) => raw switch
    {
        null or "" => FinishReasons.Stop,
        "stop" or "end_turn" => FinishReasons.Stop,
        "length" => FinishReasons.MaxTokens,
        "tool_calls" or "function_call" => FinishReasons.ToolCalls,
        "content_filter" => FinishReasons.ContentFilter,
        _ => raw,
    };

    /// <summary>Reads <c>usage</c>: <c>prompt_tokens</c> includes cached tokens, so the cached count is moved to <see cref="LlmUsage.CacheRead"/>.</summary>
    public static LlmUsage? ParseUsage(JsonElement root)
    {
        if (Json.GetObject(root, "usage") is not { } usage) return null;
        var prompt = Json.GetInt(usage, "prompt_tokens");
        var completion = Json.GetInt(usage, "completion_tokens");
        int? cached = null;
        if (Json.GetObject(usage, "prompt_tokens_details") is { } details && Json.GetNullableInt(details, "cached_tokens") is { } c)
        {
            cached = c;
        }

        return new LlmUsage(Math.Max(0, prompt - (cached ?? 0)), completion, cached, null);
    }

    public static ChatResult ParseResponse(string body, string providerKey, ModelRef requested, TimeSpan elapsed)
    {
        using var doc = Json.ParseDocument(body, providerKey);
        var root = doc.RootElement;
        if (Json.GetObject(root, "error") is { } error)
        {
            throw new LlmResponseException($"{providerKey} returned an error: {Json.GetString(error, "message") ?? error.GetRawText()}", providerKey, requested, body);
        }

        var choices = Json.GetArray(root, "choices");
        var text = string.Empty;
        string? finish = null;
        List<ToolCall>? toolCalls = null;
        if (choices is { } array && array.GetArrayLength() > 0)
        {
            var choice = array[0];
            finish = Json.GetString(choice, "finish_reason");
            if (Json.GetObject(choice, "message") is { } message)
            {
                text = ExtractContentText(message);
                toolCalls = ParseToolCalls(message);
            }
        }

        var served = Json.GetString(root, "model");
        var resolved = requested with { Model = string.IsNullOrEmpty(served) ? requested.Model : served };
        var mapped = MapFinishReason(finish);
        if (toolCalls is { Count: > 0 } && mapped == FinishReasons.Stop) mapped = FinishReasons.ToolCalls;
        return new ChatResult(text, resolved, ParseUsage(root), mapped, toolCalls, elapsed, body);
    }

    public static string ExtractContentText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return string.Empty;
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join("\n", content.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : Json.GetString(item, "text"))
                .Where(s => !string.IsNullOrEmpty(s))),
            _ => string.Empty,
        };
    }

    public static List<ToolCall>? ParseToolCalls(JsonElement message)
    {
        if (Json.GetArray(message, "tool_calls") is not { } calls) return null;
        var list = new List<ToolCall>();
        foreach (var call in calls.EnumerateArray())
        {
            var fn = Json.GetObject(call, "function");
            if (fn is null) continue;
            list.Add(new ToolCall(
                Json.GetString(call, "id") ?? $"call_{list.Count}",
                Json.GetString(fn.Value, "name") ?? string.Empty,
                Json.GetString(fn.Value, "arguments") ?? "{}"));
        }

        return list.Count == 0 ? null : list;
    }

    /// <summary>Consumes a Chat Completions SSE stream and yields deltas; the final delta carries usage, finish reason and any tool calls.</summary>
    public static async IAsyncEnumerable<ChatDelta> ReadStreamAsync(
        Stream stream,
        string providerKey,
        ModelRef requested,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LlmUsage? usage = null;
        string? finish = null;
        string? served = null;
        var toolBuffers = new SortedDictionary<int, (string Id, string Name, System.Text.StringBuilder Args)>();

        await foreach (var evt in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (evt.IsDone) break;
            using var doc = Json.ParseDocument(evt.Data, providerKey);
            var root = doc.RootElement;
            if (Json.GetObject(root, "error") is { } error)
            {
                throw new LlmResponseException($"{providerKey} stream error: {Json.GetString(error, "message") ?? error.GetRawText()}", providerKey, requested, evt.Data);
            }

            served ??= Json.GetString(root, "model");
            if (ParseUsage(root) is { } u) usage = u;

            if (Json.GetArray(root, "choices") is not { } choices) continue;
            foreach (var choice in choices.EnumerateArray())
            {
                if (Json.GetString(choice, "finish_reason") is { } fr) finish = fr;
                if (Json.GetObject(choice, "delta") is not { } delta) continue;

                var text = ExtractContentText(delta);
                if (text.Length > 0)
                {
                    yield return ChatDelta.Text(text);
                }

                if (Json.GetArray(delta, "tool_calls") is { } calls)
                {
                    foreach (var call in calls.EnumerateArray())
                    {
                        var index = Json.GetInt(call, "index");
                        if (!toolBuffers.TryGetValue(index, out var buffer))
                        {
                            buffer = (Json.GetString(call, "id") ?? $"call_{index}", string.Empty, new System.Text.StringBuilder());
                        }

                        if (Json.GetObject(call, "function") is { } fn)
                        {
                            if (Json.GetString(fn, "name") is { Length: > 0 } name) buffer.Name = name;
                            if (Json.GetString(fn, "arguments") is { } args) buffer.Args.Append(args);
                        }

                        if (Json.GetString(call, "id") is { Length: > 0 } id) buffer.Id = id;
                        toolBuffers[index] = buffer;
                    }
                }
            }
        }

        var toolCalls = toolBuffers.Count == 0
            ? null
            : toolBuffers.Values.Select(b => new ToolCall(b.Id, b.Name, b.Args.Length == 0 ? "{}" : b.Args.ToString())).ToList();
        var mapped = MapFinishReason(finish);
        if (toolCalls is not null && mapped == FinishReasons.Stop) mapped = FinishReasons.ToolCalls;
        yield return ChatDelta.Final(usage, mapped, toolCalls, requested with { Model = served ?? requested.Model });
    }

    public static IReadOnlyList<ModelInfo> ParseModelList(string body, string providerKey)
    {
        using var doc = Json.ParseDocument(body, providerKey);
        var list = new List<ModelInfo>();
        if (Json.GetArray(doc.RootElement, "data") is not { } data) return list;
        foreach (var item in data.EnumerateArray())
        {
            var id = Json.GetString(item, "id");
            if (string.IsNullOrEmpty(id)) continue;
            list.Add(new ModelInfo(providerKey, id)
            {
                Created = Json.UnixSeconds(item, "created"),
                OwnedBy = Json.GetString(item, "owned_by"),
                ContextTokens = Json.GetNullableInt(item, "context_window") ?? Json.GetNullableInt(item, "max_context_length"),
            });
        }

        return list.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static JsonObject BuildEmbeddingBody(EmbeddingRequest request, string model)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = new JsonArray(request.Inputs.Select(i => (JsonNode?)i).ToArray()),
            ["encoding_format"] = "float",
        };
        if (request.Dimensions is { } d) body["dimensions"] = d;
        Json.MergeExtras(body, request.Extras);
        return body;
    }

    public static EmbeddingResult ParseEmbeddings(string body, string providerKey, ModelRef requested, TimeSpan elapsed)
    {
        using var doc = Json.ParseDocument(body, providerKey);
        var root = doc.RootElement;
        var vectors = new List<float[]>();
        if (Json.GetArray(root, "data") is { } data)
        {
            foreach (var item in data.EnumerateArray().OrderBy(i => Json.GetInt(i, "index")))
            {
                if (Json.GetArray(item, "embedding") is { } embedding)
                {
                    vectors.Add(embedding.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray());
                }
            }
        }

        LlmUsage? usage = null;
        if (Json.GetObject(root, "usage") is { } u)
        {
            usage = new LlmUsage(Json.GetInt(u, "prompt_tokens"), 0);
        }

        var served = Json.GetString(root, "model");
        return new EmbeddingResult(vectors, requested with { Model = served ?? requested.Model }, usage, elapsed, body);
    }
}
