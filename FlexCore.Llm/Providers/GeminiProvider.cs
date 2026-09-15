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
/// Google Gemini (<c>v1beta</c>): <c>generateContent</c>,
/// <c>streamGenerateContent?alt=sse</c>, <c>batchEmbedContents</c> and the
/// model list. The key travels in the <c>x-goog-api-key</c> header, never the
/// query string. The system prompt goes to <c>systemInstruction</c>, the
/// assistant role is <c>model</c>, and JSON mode sets
/// <c>responseMimeType: application/json</c> (plus <c>responseJsonSchema</c>
/// for a schema).
/// </summary>
public sealed class GeminiProvider : LlmProviderBase
{
    public const string DefaultApiVersion = "v1beta";

    public GeminiProvider(
        IHttpClientFactory httpClientFactory,
        ICredentialResolver credentials,
        ITokenProvider? tokenProvider = null,
        IEnumerable<ILlmRequestAuthenticator>? authenticators = null,
        ILogger<GeminiProvider>? logger = null)
        : base(ProviderKeys.Gemini, httpClientFactory, credentials, tokenProvider, authenticators, logger)
    {
    }

    public override LlmCapabilities Capabilities =>
        LlmCapabilities.Chat | LlmCapabilities.Streaming | LlmCapabilities.JsonMode | LlmCapabilities.JsonSchema
        | LlmCapabilities.Vision | LlmCapabilities.Embeddings | LlmCapabilities.Tools | LlmCapabilities.Reasoning
        | LlmCapabilities.ListModels;

    public override bool IsConfigured => Settings.HasApiKey || Authenticator is not null;

    protected override string? DefaultEndpoint => "https://generativelanguage.googleapis.com";
    protected override AuthMode DefaultAuthMode => AuthMode.ApiKeyHeader;
    protected override string DefaultHeaderName => "x-goog-api-key";

    /// <summary>Base "https://host" plus the API version; a configured URL such as GhostWriter's <c>…/v1beta/models/{model}:generateContent</c> template is cut back to its host.</summary>
    private (string Base, string Version) Root(ProviderSettings settings)
    {
        var raw = ResolveEndpoint(settings);
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return (raw.TrimEnd('/'), settings.ApiVersion ?? DefaultApiVersion);

        var path = uri.AbsolutePath;
        var version = settings.ApiVersion;
        var cut = path.IndexOf("/v1", StringComparison.OrdinalIgnoreCase);
        if (cut >= 0)
        {
            var rest = path[(cut + 1)..];
            var slash = rest.IndexOf('/');
            version ??= slash < 0 ? rest : rest[..slash];
            path = path[..cut];
        }

        var builder = new UriBuilder(uri) { Path = path.Length == 0 ? "/" : path, Query = string.Empty };
        return (builder.Uri.ToString().TrimEnd('/'), version ?? DefaultApiVersion);
    }

    private string ModelUrl(ProviderSettings settings, string model, string action, string? query = null)
    {
        var (root, version) = Root(settings);
        var url = $"{root}/{version}/models/{Uri.EscapeDataString(model)}:{action}";
        return query is null ? url : url + "?" + query;
    }

    public JsonObject BuildBody(ChatRequest request, string model, int? maxOutputTokens, double? temperature)
    {
        var body = new JsonObject { ["contents"] = BuildContents(request) };
        if (CollectSystem(request) is { } system)
        {
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) };
        }

        var generation = new JsonObject();
        if (temperature is { } t) generation["temperature"] = t;
        if (maxOutputTokens is { } max) generation["maxOutputTokens"] = max;
        if (request.StopSequences is { Count: > 0 } stops)
        {
            generation["stopSequences"] = new JsonArray(stops.Select(s => (JsonNode?)s).ToArray());
        }

        switch (request.ResponseFormat)
        {
            case ResponseFormat.Json:
                generation["responseMimeType"] = "application/json";
                break;
            case ResponseFormat.JsonSchema:
                generation["responseMimeType"] = "application/json";
                if (Json.ToNode(request.Schema) is { } schema) generation["responseJsonSchema"] = schema;
                break;
        }

        if (request.Reasoning is { } reasoning && (reasoning.Enabled is not null || reasoning.BudgetTokens is not null))
        {
            var thinking = new JsonObject();
            if (reasoning.Enabled == false) thinking["thinkingBudget"] = 0;
            else if (reasoning.BudgetTokens is { } budget) thinking["thinkingBudget"] = budget;
            else thinking["thinkingBudget"] = -1;
            generation["thinkingConfig"] = thinking;
        }

        if (generation.Count > 0) body["generationConfig"] = generation;

        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray(new JsonObject
            {
                ["functionDeclarations"] = new JsonArray(tools.Select(tool => (JsonNode?)new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = Json.ToNode(tool.Parameters),
                }).ToArray()),
            });

            if (request.ToolChoice is { } choice)
            {
                var config = new JsonObject
                {
                    ["mode"] = choice.Mode switch
                    {
                        "none" => "NONE",
                        "required" or "function" => "ANY",
                        _ => "AUTO",
                    },
                };
                if (choice.Mode == "function" && choice.FunctionName is { } fn)
                {
                    config["allowedFunctionNames"] = new JsonArray(fn);
                }
                body["toolConfig"] = new JsonObject { ["functionCallingConfig"] = config };
            }
        }

        Json.MergeExtras(body, request.Extras);
        return body;
    }

    private static JsonArray BuildContents(ChatRequest request)
    {
        var contents = new JsonArray();
        foreach (var message in request.Messages.Where(m => m.Role != ChatRole.System))
        {
            var parts = new JsonArray();
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case TextPart text when text.Text.Length > 0:
                        parts.Add(new JsonObject { ["text"] = text.Text });
                        break;
                    case ImagePart image:
                        parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = image.MimeType, ["data"] = image.ToBase64() } });
                        break;
                    case ToolCallPart call:
                        parts.Add(new JsonObject
                        {
                            ["functionCall"] = new JsonObject
                            {
                                ["name"] = call.Call.Name,
                                ["args"] = ParseArguments(call.Call.ArgumentsJson),
                            },
                        });
                        break;
                    case ToolResultPart result:
                        parts.Add(new JsonObject
                        {
                            ["functionResponse"] = new JsonObject
                            {
                                ["name"] = result.Name ?? result.CallId,
                                ["response"] = ResponseObject(result.Content),
                            },
                        });
                        break;
                }
            }

            if (parts.Count == 0) continue;
            contents.Add(new JsonObject
            {
                ["role"] = message.Role == ChatRole.Assistant ? "model" : "user",
                ["parts"] = parts,
            });
        }

        return contents;
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

    private static JsonNode ResponseObject(string content)
    {
        try
        {
            var node = JsonNode.Parse(content);
            if (node is JsonObject obj) return obj;
        }
        catch (JsonException)
        {
        }

        return new JsonObject { ["result"] = content };
    }

    public static string MapFinishReason(string? raw) => raw switch
    {
        null or "" or "STOP" or "FINISH_REASON_UNSPECIFIED" => FinishReasons.Stop,
        "MAX_TOKENS" => FinishReasons.MaxTokens,
        "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" => FinishReasons.ContentFilter,
        _ => raw,
    };

    /// <summary><c>promptTokenCount</c> includes cached tokens; <c>thoughtsTokenCount</c> is billed output and is folded into Output.</summary>
    public static LlmUsage? ParseUsage(JsonElement root)
    {
        if (Json.GetObject(root, "usageMetadata") is not { } usage) return null;
        var prompt = Json.GetInt(usage, "promptTokenCount");
        var cached = Json.GetNullableInt(usage, "cachedContentTokenCount");
        var output = Json.GetInt(usage, "candidatesTokenCount") + Json.GetInt(usage, "thoughtsTokenCount");
        return new LlmUsage(Math.Max(0, prompt - (cached ?? 0)), output, cached, null);
    }

    private static (string Text, List<ToolCall> Calls, string? Finish) ReadCandidate(JsonElement root, int callOffset)
    {
        var text = new StringBuilder();
        var calls = new List<ToolCall>();
        string? finish = null;
        if (Json.GetArray(root, "candidates") is { } candidates && candidates.GetArrayLength() > 0)
        {
            var candidate = candidates[0];
            finish = Json.GetString(candidate, "finishReason");
            if (Json.GetObject(candidate, "content") is { } content && Json.GetArray(content, "parts") is { } parts)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (Json.GetBool(part, "thought")) continue;
                    if (Json.GetString(part, "text") is { } t)
                    {
                        text.Append(t);
                    }
                    else if (Json.GetObject(part, "functionCall") is { } fc)
                    {
                        var args = fc.TryGetProperty("args", out var a) ? a.GetRawText() : "{}";
                        calls.Add(new ToolCall(Json.GetString(fc, "id") ?? $"call_{callOffset + calls.Count}", Json.GetString(fc, "name") ?? string.Empty, args));
                    }
                }
            }
        }

        return (text.ToString(), calls, finish);
    }

    public static ChatResult ParseResponse(string body, ModelRef requested, TimeSpan elapsed)
    {
        using var doc = Json.ParseDocument(body, ProviderKeys.Gemini);
        var root = doc.RootElement;
        if (Json.GetObject(root, "error") is { } error)
        {
            throw new LlmResponseException($"gemini returned an error: {Json.GetString(error, "message") ?? error.GetRawText()}", ProviderKeys.Gemini, requested, body);
        }

        var (text, calls, finish) = ReadCandidate(root, 0);
        var mapped = MapFinishReason(finish);
        if (calls.Count > 0 && mapped == FinishReasons.Stop) mapped = FinishReasons.ToolCalls;
        var served = Json.GetString(root, "modelVersion");
        return new ChatResult(text, requested with { Model = served ?? requested.Model }, ParseUsage(root), mapped, calls.Count == 0 ? null : calls, elapsed, body);
    }

    public override async Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var settings = Settings;
        var model = ResolveModel(settings, request.Model);
        var requested = request.Model with { Provider = Key, Model = model };
        var body = BuildBody(request, model, ResolveMaxOutputTokens(request, settings), ResolveTemperature(request));

        using var http = JsonPost(ModelUrl(settings, model, "generateContent"), body);
        await ApplyAuthAsync(http, settings, cancellationToken).ConfigureAwait(false);

        var watch = Stopwatch.StartNew();
        var response = await SendForBodyAsync(CreateClient(), http, requested, cancellationToken).ConfigureAwait(false);
        return ParseResponse(response, requested, watch.Elapsed);
    }

    public override async IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = Settings;
        var model = ResolveModel(settings, request.Model);
        var requested = request.Model with { Provider = Key, Model = model };
        var body = BuildBody(request, model, ResolveMaxOutputTokens(request, settings), ResolveTemperature(request));

        using var http = JsonPost(ModelUrl(settings, model, "streamGenerateContent", "alt=sse"), body, accept: "text/event-stream");
        await ApplyAuthAsync(http, settings, cancellationToken).ConfigureAwait(false);

        using var response = await SendForStreamAsync(CreateClient(), http, requested, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        LlmUsage? usage = null;
        string? finish = null;
        string? served = null;
        var calls = new List<ToolCall>();
        await foreach (var evt in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (evt.IsDone) break;
            using var doc = Json.ParseDocument(evt.Data, Key);
            var root = doc.RootElement;
            if (Json.GetObject(root, "error") is { } error)
            {
                throw new LlmResponseException($"gemini stream error: {Json.GetString(error, "message") ?? evt.Data}", Key, requested, evt.Data);
            }

            served ??= Json.GetString(root, "modelVersion");
            if (ParseUsage(root) is { } u) usage = u;
            var (text, chunkCalls, chunkFinish) = ReadCandidate(root, calls.Count);
            if (chunkFinish is not null) finish = chunkFinish;
            calls.AddRange(chunkCalls);
            if (text.Length > 0) yield return ChatDelta.Text(text);
        }

        var mapped = MapFinishReason(finish);
        if (calls.Count > 0 && mapped == FinishReasons.Stop) mapped = FinishReasons.ToolCalls;
        yield return ChatDelta.Final(usage, mapped, calls.Count == 0 ? null : calls, requested with { Model = served ?? model });
    }

    public override async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var settings = Settings;
        var (root, version) = Root(settings);
        using var http = new HttpRequestMessage(HttpMethod.Get, $"{root}/{version}/models?pageSize=200");
        await ApplyAuthAsync(http, settings, cancellationToken).ConfigureAwait(false);
        var body = await SendForBodyAsync(CreateClient(), http, null, cancellationToken).ConfigureAwait(false);

        using var doc = Json.ParseDocument(body, Key);
        var list = new List<ModelInfo>();
        if (Json.GetArray(doc.RootElement, "models") is { } models)
        {
            foreach (var item in models.EnumerateArray())
            {
                var name = Json.GetString(item, "name");
                if (string.IsNullOrEmpty(name)) continue;
                var methods = Json.GetArray(item, "supportedGenerationMethods");
                var generates = methods is null || methods.Value.EnumerateArray().Any(m => m.GetString() is "generateContent" or "embedContent");
                if (!generates) continue;
                var id = name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name;
                list.Add(new ModelInfo(Key, id)
                {
                    DisplayName = Json.GetString(item, "displayName"),
                    ContextTokens = Json.GetNullableInt(item, "inputTokenLimit"),
                    MaxOutputTokens = Json.GetNullableInt(item, "outputTokenLimit"),
                });
            }
        }

        return list;
    }

    public override async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
    {
        var settings = Settings;
        var model = request.Model.HasModel ? request.Model.Model : "gemini-embedding-001";
        var requested = request.Model with { Provider = Key, Model = model };

        var body = new JsonObject
        {
            ["requests"] = new JsonArray(request.Inputs.Select(input =>
            {
                var item = new JsonObject
                {
                    ["model"] = "models/" + model,
                    ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = input }) },
                };
                if (request.Dimensions is { } d) item["outputDimensionality"] = d;
                return (JsonNode?)item;
            }).ToArray()),
        };

        using var http = JsonPost(ModelUrl(settings, model, "batchEmbedContents"), body);
        await ApplyAuthAsync(http, settings, cancellationToken).ConfigureAwait(false);

        var watch = Stopwatch.StartNew();
        var response = await SendForBodyAsync(CreateClient(), http, requested, cancellationToken).ConfigureAwait(false);
        using var doc = Json.ParseDocument(response, Key);
        var vectors = new List<float[]>();
        if (Json.GetArray(doc.RootElement, "embeddings") is { } embeddings)
        {
            foreach (var embedding in embeddings.EnumerateArray())
            {
                if (Json.GetArray(embedding, "values") is { } values)
                {
                    vectors.Add(values.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray());
                }
            }
        }

        return new EmbeddingResult(vectors, requested, null, watch.Elapsed, response);
    }
}
