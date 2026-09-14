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
/// OpenAI's Responses API (<c>/v1/responses</c>) for chat and streaming,
/// plus <c>/v1/images/generations</c>, <c>/v1/embeddings</c> and
/// <c>/v1/models</c>. Usage is read from <c>input_tokens</c> /
/// <c>output_tokens</c> (the Responses names — not the Chat Completions
/// <c>prompt_tokens</c>), with <c>input_tokens_details.cached_tokens</c>
/// moved to <see cref="LlmUsage.CacheRead"/>. gpt-5*, o-series and codex
/// models get <c>reasoning.effort</c> and never <c>temperature</c>.
/// </summary>
public sealed class OpenAiResponsesProvider : LlmProviderBase
{
    private static readonly string[] KnownLeaves = { "/responses", "/chat/completions", "/images/generations", "/embeddings", "/models" };

    public OpenAiResponsesProvider(
        IHttpClientFactory httpClientFactory,
        ICredentialResolver credentials,
        ITokenProvider? tokenProvider = null,
        IEnumerable<ILlmRequestAuthenticator>? authenticators = null,
        ILogger<OpenAiResponsesProvider>? logger = null)
        : base(ProviderKeys.OpenAi, httpClientFactory, credentials, tokenProvider, authenticators, logger)
    {
    }

    public override LlmCapabilities Capabilities =>
        LlmCapabilities.Chat | LlmCapabilities.Streaming | LlmCapabilities.JsonMode | LlmCapabilities.JsonSchema
        | LlmCapabilities.Vision | LlmCapabilities.ImageGeneration | LlmCapabilities.Embeddings | LlmCapabilities.Tools
        | LlmCapabilities.Reasoning | LlmCapabilities.PromptCaching | LlmCapabilities.ListModels;

    public override bool IsConfigured => Settings.HasApiKey || Authenticator is not null;

    protected override string? DefaultEndpoint => "https://api.openai.com/v1";

    private string BaseUrl(ProviderSettings settings)
    {
        var stripped = StripLeaves(ResolveEndpoint(settings), KnownLeaves);
        if (Uri.TryCreate(stripped, UriKind.Absolute, out var uri) && uri.AbsolutePath.TrimEnd('/').Length == 0)
        {
            return new UriBuilder(uri) { Path = "/v1" }.Uri.ToString().TrimEnd('/');
        }

        return stripped;
    }

    public static JsonObject BuildBody(ChatRequest request, string model, string? system, int? maxOutputTokens, double? temperature, bool stream)
    {
        var reasoningModel = OpenAiChatWire.IsReasoningModel(model);
        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = BuildInput(request),
        };
        if (system is not null) body["instructions"] = system;
        if (temperature is { } t && !reasoningModel) body["temperature"] = t;
        if (maxOutputTokens is { } max) body["max_output_tokens"] = max;
        if (request.Reasoning?.Effort is { } effort)
        {
            body["reasoning"] = new JsonObject { ["effort"] = effort };
        }

        switch (request.ResponseFormat)
        {
            case ResponseFormat.Json:
                body["text"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_object" } };
                break;
            case ResponseFormat.JsonSchema:
                body["text"] = new JsonObject
                {
                    ["format"] = new JsonObject
                    {
                        ["type"] = "json_schema",
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
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = Json.ToNode(t.Parameters) ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            }).ToArray());

            if (request.ToolChoice is { } choice)
            {
                body["tool_choice"] = choice.Mode == "function"
                    ? new JsonObject { ["type"] = "function", ["name"] = choice.FunctionName }
                    : choice.Mode;
            }
        }

        if (stream) body["stream"] = true;
        Json.MergeExtras(body, request.Extras);
        return body;
    }

    private static JsonArray BuildInput(ChatRequest request)
    {
        var input = new JsonArray();
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
                                content.Add(new JsonObject { ["type"] = "input_text", ["text"] = text.Text });
                                break;
                            case ImagePart image:
                                content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = image.ToDataUrl() });
                                break;
                            case ToolResultPart result:
                                input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = result.CallId, ["output"] = result.Content });
                                break;
                        }
                    }

                    if (content.Count > 0) input.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                    break;
                }

                case ChatRole.Assistant:
                {
                    var text = message.Text;
                    if (text.Length > 0)
                    {
                        input.Add(new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }),
                        });
                    }

                    foreach (var call in message.Parts.OfType<ToolCallPart>())
                    {
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = call.Call.Id,
                            ["name"] = call.Call.Name,
                            ["arguments"] = call.Call.ArgumentsJson,
                        });
                    }
                    break;
                }

                case ChatRole.Tool:
                    foreach (var result in message.Parts.OfType<ToolResultPart>())
                    {
                        input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = result.CallId, ["output"] = result.Content });
                    }
                    break;
            }
        }

        return input;
    }

    /// <summary>Responses usage: <c>input_tokens</c> (includes cached), <c>output_tokens</c>, <c>input_tokens_details.cached_tokens</c>.</summary>
    public static LlmUsage? ParseUsage(JsonElement container)
    {
        if (Json.GetObject(container, "usage") is not { } usage) return null;
        var input = Json.GetInt(usage, "input_tokens");
        var output = Json.GetInt(usage, "output_tokens");
        int? cached = Json.GetObject(usage, "input_tokens_details") is { } details ? Json.GetNullableInt(details, "cached_tokens") : null;
        return new LlmUsage(Math.Max(0, input - (cached ?? 0)), output, cached, null);
    }

    private static string FinishReasonOf(JsonElement response, bool hasToolCalls)
    {
        var status = Json.GetString(response, "status");
        if (status == "incomplete")
        {
            var reason = Json.GetObject(response, "incomplete_details") is { } details ? Json.GetString(details, "reason") : null;
            return reason switch
            {
                "max_output_tokens" => FinishReasons.MaxTokens,
                "content_filter" => FinishReasons.ContentFilter,
                null => FinishReasons.MaxTokens,
                _ => reason,
            };
        }

        if (status == "failed") return FinishReasons.Error;
        return hasToolCalls ? FinishReasons.ToolCalls : FinishReasons.Stop;
    }

    public static ChatResult ParseResponse(string body, ModelRef requested, TimeSpan elapsed)
    {
        using var doc = Json.ParseDocument(body, ProviderKeys.OpenAi);
        var root = doc.RootElement;
        if (Json.GetObject(root, "error") is { ValueKind: JsonValueKind.Object } error)
        {
            throw new LlmResponseException($"openai returned an error: {Json.GetString(error, "message") ?? error.GetRawText()}", ProviderKeys.OpenAi, requested, body);
        }

        var text = new StringBuilder();
        var toolCalls = new List<ToolCall>();
        if (Json.GetString(root, "output_text") is { } convenience)
        {
            text.Append(convenience);
        }
        else if (Json.GetArray(root, "output") is { } output)
        {
            foreach (var item in output.EnumerateArray())
            {
                switch (Json.GetString(item, "type"))
                {
                    case "message":
                        if (Json.GetArray(item, "content") is { } content)
                        {
                            foreach (var block in content.EnumerateArray())
                            {
                                if (Json.GetString(block, "type") == "output_text" && Json.GetString(block, "text") is { } t)
                                {
                                    if (text.Length > 0) text.Append('\n');
                                    text.Append(t);
                                }
                            }
                        }
                        break;
                    case "function_call":
                        toolCalls.Add(new ToolCall(
                            Json.GetString(item, "call_id") ?? Json.GetString(item, "id") ?? $"call_{toolCalls.Count}",
                            Json.GetString(item, "name") ?? string.Empty,
                            Json.GetString(item, "arguments") ?? "{}"));
                        break;
                }
            }
        }

        var served = Json.GetString(root, "model");
        var resolved = requested with { Model = served ?? requested.Model };
        return new ChatResult(text.ToString(), resolved, ParseUsage(root), FinishReasonOf(root, toolCalls.Count > 0), toolCalls.Count == 0 ? null : toolCalls, elapsed, body);
    }

    public override async Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var settings = Settings;
        var model = ResolveModel(settings, request.Model);
        var requested = request.Model with { Provider = Key, Model = model };
        var body = BuildBody(request, model, CollectSystem(request), ResolveMaxOutputTokens(request, settings), ResolveTemperature(request), stream: false);

        using var http = JsonPost(Combine(BaseUrl(settings), "responses"), body);
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
        var body = BuildBody(request, model, CollectSystem(request), ResolveMaxOutputTokens(request, settings), ResolveTemperature(request), stream: true);

        using var http = JsonPost(Combine(BaseUrl(settings), "responses"), body, accept: "text/event-stream");
        await ApplyAuthAsync(http, settings, cancellationToken).ConfigureAwait(false);

        using var response = await SendForStreamAsync(CreateClient(), http, requested, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        LlmUsage? usage = null;
        string? finish = null;
        string? served = null;
        var toolCalls = new List<ToolCall>();
        await foreach (var evt in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (evt.IsDone) break;
            using var doc = Json.ParseDocument(evt.Data, Key);
            var root = doc.RootElement;
            var type = Json.GetString(root, "type") ?? evt.Event;
            switch (type)
            {
                case "response.output_text.delta":
                    if (Json.GetString(root, "delta") is { Length: > 0 } delta) yield return ChatDelta.Text(delta);
                    break;

                case "response.output_item.done":
                    if (Json.GetObject(root, "item") is { } item && Json.GetString(item, "type") == "function_call")
                    {
                        toolCalls.Add(new ToolCall(
                            Json.GetString(item, "call_id") ?? Json.GetString(item, "id") ?? $"call_{toolCalls.Count}",
                            Json.GetString(item, "name") ?? string.Empty,
                            Json.GetString(item, "arguments") ?? "{}"));
                    }
                    break;

                case "response.completed":
                case "response.incomplete":
                    if (Json.GetObject(root, "response") is { } completed)
                    {
                        usage = ParseUsage(completed) ?? usage;
                        served ??= Json.GetString(completed, "model");
                        finish = FinishReasonOf(completed, toolCalls.Count > 0);
                    }
                    break;

                case "response.failed":
                {
                    var message = Json.GetObject(root, "response") is { } failed && Json.GetObject(failed, "error") is { } err
                        ? Json.GetString(err, "message")
                        : null;
                    throw new LlmResponseException($"openai response failed: {message ?? evt.Data}", Key, requested, evt.Data);
                }

                case "error":
                    throw new LlmResponseException($"openai stream error: {Json.GetString(root, "message") ?? evt.Data}", Key, requested, evt.Data);
            }
        }

        yield return ChatDelta.Final(
            usage,
            finish ?? (toolCalls.Count > 0 ? FinishReasons.ToolCalls : FinishReasons.Stop),
            toolCalls.Count == 0 ? null : toolCalls,
            requested with { Model = served ?? model });
    }

    public override async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var settings = Settings;
        using var http = new HttpRequestMessage(HttpMethod.Get, Combine(BaseUrl(settings), "models"));
        await ApplyAuthAsync(http, settings, cancellationToken).ConfigureAwait(false);
        var body = await SendForBodyAsync(CreateClient(), http, null, cancellationToken).ConfigureAwait(false);
        return OpenAiChatWire.ParseModelList(body, Key);
    }

    public static JsonObject BuildImageBody(ImageRequest request, string model)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["prompt"] = request.Prompt,
            ["n"] = Math.Max(1, request.Count),
            ["size"] = request.Size,
        };
        if (request.Quality is { } quality) body["quality"] = quality;
        if (model.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase))
        {
            body["output_format"] = request.OutputFormat;
        }
        else
        {
            body["response_format"] = "b64_json";
        }

        Json.MergeExtras(body, request.Extras);
        return body;
    }

    public override async Task<ImageResult> GenerateImageAsync(ImageRequest request, CancellationToken cancellationToken)
    {
        var settings = Settings;
        var model = request.Model.HasModel ? request.Model.Model : "gpt-image-1";
        var requested = request.Model with { Provider = Key, Model = model };

        using var http = JsonPost(Combine(BaseUrl(settings), "images/generations"), BuildImageBody(request, model));
        await ApplyAuthAsync(http, settings, cancellationToken).ConfigureAwait(false);

        var client = CreateClient();
        var watch = Stopwatch.StartNew();
        var body = await SendForBodyAsync(client, http, requested, cancellationToken).ConfigureAwait(false);
        return await ParseImagesAsync(body, requested, request.OutputFormat, watch, client, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ImageResult> ParseImagesAsync(string body, ModelRef requested, string outputFormat, Stopwatch watch, HttpClient client, CancellationToken cancellationToken)
    {
        using var doc = Json.ParseDocument(body, requested.Provider);
        var root = doc.RootElement;
        var mime = outputFormat.ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => "image/jpeg",
            "webp" => "image/webp",
            _ => "image/png",
        };

        var images = new List<GeneratedImage>();
        if (Json.GetArray(root, "data") is { } data)
        {
            foreach (var entry in data.EnumerateArray())
            {
                var revised = Json.GetString(entry, "revised_prompt");
                if (Json.GetString(entry, "b64_json") is { Length: > 0 } b64)
                {
                    images.Add(new GeneratedImage(Convert.FromBase64String(b64), mime, revised));
                }
                else if (Json.GetString(entry, "url") is { Length: > 0 } url)
                {
                    var bytes = await client.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
                    images.Add(new GeneratedImage(bytes, mime, revised));
                }
            }
        }

        if (images.Count == 0)
        {
            throw new LlmResponseException($"{requested.Provider} image API returned no image data.", requested.Provider, requested, body);
        }

        return new ImageResult(images, requested, ParseUsage(root), watch.Elapsed, body);
    }

    public override async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
    {
        var settings = Settings;
        var model = request.Model.HasModel ? request.Model.Model : "text-embedding-3-small";
        var requested = request.Model with { Provider = Key, Model = model };

        using var http = JsonPost(Combine(BaseUrl(settings), "embeddings"), OpenAiChatWire.BuildEmbeddingBody(request, model));
        await ApplyAuthAsync(http, settings, cancellationToken).ConfigureAwait(false);

        var watch = Stopwatch.StartNew();
        var body = await SendForBodyAsync(CreateClient(), http, requested, cancellationToken).ConfigureAwait(false);
        return OpenAiChatWire.ParseEmbeddings(body, Key, requested, watch.Elapsed);
    }
}
