using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fx.ControlKit.Llm.Configuration;
using Fx.ControlKit.Llm.Http;
using Microsoft.Extensions.Logging;

namespace Fx.ControlKit.Llm.Providers;

/// <summary>
/// Ollama's native API: <c>/api/chat</c> (NDJSON when streaming),
/// <c>/api/tags</c> for the model list and <c>/api/embed</c>. Registered
/// twice — as <c>ollama</c> (local daemon, default
/// <c>http://localhost:11434</c>) and <c>ollamacloud</c> (a remote daemon) —
/// each with its own endpoint and auth, so a cloud API key never leaks to the
/// local daemon and vice versa. <c>eval_count</c> / <c>prompt_eval_count</c>
/// become usage; <c>done_reason: length</c> becomes
/// <see cref="FinishReasons.MaxTokens"/>.
/// </summary>
public sealed class OllamaProvider : LlmProviderBase
{
    /// <summary><see cref="ModelInfo.Metadata"/> key set to "true" on tags the local daemon proxies to ollama.com.</summary>
    public const string CloudProxiedMetadata = "cloud";

    private const string LegacyCloudPrefix = "cloud-ollama:";

    // Everything else in Extras is an Ollama model option (num_ctx, top_p,
    // repeat_penalty, seed, …) and goes under "options".
    private static readonly HashSet<string> TopLevelExtras = new(StringComparer.OrdinalIgnoreCase)
    {
        "keep_alive", "think", "format", "raw", "template", "tools", "stream",
    };

    public OllamaProvider(
        string key,
        IHttpClientFactory httpClientFactory,
        ICredentialResolver credentials,
        ITokenProvider? tokenProvider = null,
        IEnumerable<ILlmRequestAuthenticator>? authenticators = null,
        ILogger<OllamaProvider>? logger = null)
        : base(key, httpClientFactory, credentials, tokenProvider, authenticators, logger)
    {
        if (Key is not (ProviderKeys.Ollama or ProviderKeys.OllamaCloud))
        {
            throw new ArgumentException("OllamaProvider serves the 'ollama' and 'ollamacloud' keys only.", nameof(key));
        }
    }

    public override LlmCapabilities Capabilities =>
        LlmCapabilities.Chat | LlmCapabilities.Streaming | LlmCapabilities.JsonMode | LlmCapabilities.JsonSchema
        | LlmCapabilities.Vision | LlmCapabilities.Embeddings | LlmCapabilities.Tools | LlmCapabilities.Reasoning
        | LlmCapabilities.ListModels;

    public override bool IsConfigured => Settings.HasEndpoint || DefaultEndpoint is not null;

    protected override string? DefaultEndpoint => Key == ProviderKeys.Ollama ? "http://localhost:11434" : null;
    protected override AuthMode DefaultAuthMode => AuthMode.None;
    protected override string DefaultHeaderName => "X-API-Key";

    private string BaseUrl(ProviderSettings settings)
        => StripLeaves(ResolveEndpoint(settings), "/api/chat", "/api/tags", "/api/embed", "/api/embeddings", "/api/generate", "/api");

    private Task AuthAsync(HttpRequestMessage http, ProviderSettings settings, CancellationToken cancellationToken)
    {
        // A key with no explicit mode means a bearer token (ollama.com and most
        // proxies); "header" style is chosen by setting AuthMode explicitly.
        var effective = settings.AuthMode is null && settings.HasApiKey ? settings with { AuthMode = AuthMode.Bearer } : settings;
        return ApplyAuthAsync(http, effective, cancellationToken);
    }

    private string ModelFor(ProviderSettings settings, ModelRef model) => ResolveModel(settings, model);

    /// <summary>Drops the legacy <c>cloud-ollama:</c> picker prefix so the daemon sees the raw tag; family aliases (<c>qwen</c> → <c>qwen3:8b</c>) are applied by the base resolver afterwards.</summary>
    protected override string NormalizeModel(string id)
        => id.StartsWith(LegacyCloudPrefix, StringComparison.OrdinalIgnoreCase) ? id[LegacyCloudPrefix.Length..].Trim() : id;

    /// <summary>Ollama 0.4+ lists cloud-proxied models from the local daemon with a <c>:cloud</c> tag (or a <c>-cloud</c> tag suffix); they run on ollama.com but are addressed through the local endpoint.</summary>
    public static bool IsCloudProxied(string? tag)
        => !string.IsNullOrWhiteSpace(tag)
           && (tag.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase) || tag.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A readable label for a tag: <c>deepseek-r1:14b</c> → "DeepSeek R1 14B",
    /// <c>phi3:medium</c> → "Phi 3 Medium", <c>gemma4:e4b</c> → "Gemma 4 Efficient 4B",
    /// <c>deepseek-v4-pro:cloud</c> → "DeepSeek V4 Pro (Cloud)". Unknown families come back unchanged.
    /// </summary>
    public static string DisplayNameFor(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return tag;
        var lower = tag.ToLowerInvariant();

        if (lower.EndsWith(":cloud", StringComparison.Ordinal))
        {
            var baseTag = tag[..^":cloud".Length];
            if (lower.StartsWith("gemma4:", StringComparison.Ordinal)) return DisplayNameFor(baseTag) + " (Cloud)";
            var pretty = string.Join(' ', baseTag
                .Split('-', StringSplitOptions.RemoveEmptyEntries)
                .Select(seg => char.ToUpperInvariant(seg[0]) + seg[1..]));
            pretty = pretty.Replace("Deepseek", "DeepSeek", StringComparison.Ordinal).Replace("Gpt", "GPT", StringComparison.Ordinal);
            return $"{pretty} (Cloud)";
        }

        if (lower.StartsWith("deepseek-r1:", StringComparison.Ordinal)) return $"DeepSeek R1 {Size(tag)}";
        if (lower.StartsWith("qwen3:", StringComparison.Ordinal)) return $"Qwen 3 {Size(tag)}";
        if (lower.StartsWith("qwen2.5-coder:", StringComparison.Ordinal)) return $"Qwen 2.5 Coder {Size(tag)}";
        if (lower.StartsWith("mistral-nemo:", StringComparison.Ordinal)) return $"Mistral Nemo {Size(tag)}";
        if (lower.StartsWith("mistral:", StringComparison.Ordinal)) return $"Mistral {Size(tag)}";
        if (lower.StartsWith("wizardlm2:", StringComparison.Ordinal)) return $"WizardLM 2 {Size(tag)}";
        if (lower.StartsWith("wizard-vicuna-uncensored:", StringComparison.Ordinal)) return $"Wizard Vicuna Uncensored {Size(tag)}";
        if (lower.StartsWith("wizard-vicuna:", StringComparison.Ordinal)) return $"Wizard Vicuna {Size(tag)}";
        if (lower.StartsWith("wizardlm:", StringComparison.Ordinal)) return $"WizardLM {Size(tag)}";
        if (lower.StartsWith("wizardcoder:", StringComparison.Ordinal)) return $"WizardCoder {Size(tag)}";

        if (lower.StartsWith("llama", StringComparison.Ordinal))
        {
            var version = new string(Prefix(tag).Where(ch => char.IsDigit(ch) || ch == '.').ToArray());
            return ($"Llama {version}".TrimEnd() + " " + Size(tag)).Trim();
        }

        if (lower.StartsWith("gemma4:", StringComparison.Ordinal) || lower == "gemma4")
        {
            var colon = tag.IndexOf(':');
            if (colon < 0 || colon == tag.Length - 1) return "Gemma 4";
            var t = tag[(colon + 1)..];
            if (t.Equals("latest", StringComparison.OrdinalIgnoreCase)) return "Gemma 4";
            if (t.StartsWith('e') && t.Length > 1 && char.IsDigit(t[1]))
            {
                var digits = new string(t.Skip(1).TakeWhile(char.IsDigit).ToArray());
                return $"Gemma 4 Efficient {digits}B";
            }

            return t.EndsWith('b') || t.EndsWith('B') ? $"Gemma 4 {t[..^1]}B" : $"Gemma 4 {t}";
        }

        if (lower.StartsWith("gemma", StringComparison.Ordinal))
        {
            var prefix = Prefix(tag);
            var suffix = prefix.Length > 5 ? " " + prefix[5..] : string.Empty;
            return $"Gemma{suffix} {Size(tag)}".Trim();
        }

        if (lower.StartsWith("phi", StringComparison.Ordinal))
        {
            var prefix = Prefix(tag);
            var version = prefix.Length > 3 ? " " + prefix[3..] : string.Empty;
            var colon = tag.IndexOf(':');
            var t = colon > 0 && colon + 1 < tag.Length ? tag[(colon + 1)..] : string.Empty;
            var suffix = t.Equals("medium", StringComparison.OrdinalIgnoreCase) || t.Equals("mini", StringComparison.OrdinalIgnoreCase) || t.Equals("small", StringComparison.OrdinalIgnoreCase)
                ? " " + char.ToUpperInvariant(t[0]) + t[1..].ToLowerInvariant()
                : " " + Size(tag);
            return $"Phi{version}{suffix}".Trim();
        }

        return tag;

        static string Prefix(string id)
        {
            var colon = id.IndexOf(':');
            return colon > 0 ? id[..colon] : id;
        }

        static string Size(string id)
        {
            var idx = id.IndexOf(':');
            if (idx < 0 || idx >= id.Length - 1) return id;
            var suffix = id[(idx + 1)..];
            return suffix.EndsWith('b') || suffix.EndsWith('B') ? $"{suffix[..^1]}B" : suffix;
        }
    }

    public JsonObject BuildBody(ChatRequest request, string model, int? maxOutputTokens, double? temperature, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = BuildMessages(request),
            ["stream"] = stream,
        };

        var options = new JsonObject();
        if (temperature is { } t) options["temperature"] = t;
        if (maxOutputTokens is { } max) options["num_predict"] = max;
        if (request.StopSequences is { Count: > 0 } stops)
        {
            options["stop"] = new JsonArray(stops.Select(s => (JsonNode?)s).ToArray());
        }

        switch (request.ResponseFormat)
        {
            case ResponseFormat.Json:
                body["format"] = "json";
                break;
            case ResponseFormat.JsonSchema:
                body["format"] = Json.ToNode(request.Schema) ?? "json";
                break;
        }

        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray(tools.Select(tool => (JsonNode?)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = Json.ToNode(tool.Parameters) ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                },
            }).ToArray());
        }

        if (request.Reasoning?.Enabled is { } think) body["think"] = think;

        if (request.Extras is not null)
        {
            foreach (var (key, value) in request.Extras)
            {
                var target = TopLevelExtras.Contains(key) ? body : options;
                if (value is null) target.Remove(key);
                else target[key] = Json.ToNode(value);
            }
        }

        if (options.Count > 0) body["options"] = options;
        return body;
    }

    private static JsonArray BuildMessages(ChatRequest request)
    {
        var messages = new JsonArray();
        if (CollectSystem(request) is { } system)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        }

        foreach (var message in request.Messages.Where(m => m.Role != ChatRole.System))
        {
            switch (message.Role)
            {
                case ChatRole.User:
                {
                    var node = new JsonObject { ["role"] = "user", ["content"] = message.Text };
                    var images = message.Parts.OfType<ImagePart>().Select(i => (JsonNode?)i.ToBase64()).ToArray();
                    if (images.Length > 0) node["images"] = new JsonArray(images);
                    messages.Add(node);
                    foreach (var result in message.Parts.OfType<ToolResultPart>())
                    {
                        messages.Add(ToolMessage(result));
                    }
                    break;
                }

                case ChatRole.Assistant:
                {
                    var node = new JsonObject { ["role"] = "assistant", ["content"] = message.Text };
                    var calls = message.Parts.OfType<ToolCallPart>().Select(p => p.Call).ToList();
                    if (calls.Count > 0)
                    {
                        node["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode?)new JsonObject
                        {
                            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = ParseArguments(c.ArgumentsJson) },
                        }).ToArray());
                    }
                    messages.Add(node);
                    break;
                }

                case ChatRole.Tool:
                    foreach (var result in message.Parts.OfType<ToolResultPart>())
                    {
                        messages.Add(ToolMessage(result));
                    }
                    break;
            }
        }

        return messages;
    }

    private static JsonObject ToolMessage(ToolResultPart result)
    {
        var node = new JsonObject { ["role"] = "tool", ["content"] = result.Content };
        if (result.Name is { } name) node["tool_name"] = name;
        return node;
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

    public static string MapDoneReason(string? raw) => raw switch
    {
        null or "" or "stop" => FinishReasons.Stop,
        "length" => FinishReasons.MaxTokens,
        _ => raw,
    };

    public static LlmUsage? ParseUsage(JsonElement root)
    {
        var prompt = Json.GetNullableInt(root, "prompt_eval_count");
        var eval = Json.GetNullableInt(root, "eval_count");
        return prompt is null && eval is null ? null : new LlmUsage(prompt ?? 0, eval ?? 0);
    }

    private static List<ToolCall>? ParseToolCalls(JsonElement message, int offset)
    {
        if (Json.GetArray(message, "tool_calls") is not { } calls) return null;
        var list = new List<ToolCall>();
        foreach (var call in calls.EnumerateArray())
        {
            if (Json.GetObject(call, "function") is not { } fn) continue;
            var args = fn.TryGetProperty("arguments", out var a)
                ? a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : a.GetRawText()
                : "{}";
            list.Add(new ToolCall(Json.GetString(call, "id") ?? $"call_{offset + list.Count}", Json.GetString(fn, "name") ?? string.Empty, args));
        }

        return list.Count == 0 ? null : list;
    }

    public ChatResult ParseResponse(string body, ModelRef requested, TimeSpan elapsed)
    {
        using var doc = Json.ParseDocument(body, Key);
        var root = doc.RootElement;
        if (Json.GetString(root, "error") is { } error)
        {
            throw new LlmResponseException($"{Key} returned an error: {error}", Key, requested, body);
        }

        var text = string.Empty;
        List<ToolCall>? calls = null;
        if (Json.GetObject(root, "message") is { } message)
        {
            text = Json.GetString(message, "content") ?? string.Empty;
            calls = ParseToolCalls(message, 0);
        }
        else if (Json.GetString(root, "response") is { } generate)
        {
            text = generate;
        }

        var finish = MapDoneReason(Json.GetString(root, "done_reason"));
        if (calls is not null && finish == FinishReasons.Stop) finish = FinishReasons.ToolCalls;
        var served = Json.GetString(root, "model");
        return new ChatResult(text, requested with { Model = served ?? requested.Model }, ParseUsage(root), finish, calls, elapsed, body);
    }

    public override async Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var settings = Settings;
        var model = ModelFor(settings, request.Model);
        var requested = request.Model with { Provider = Key, Model = model };
        var body = BuildBody(request, model, ResolveMaxOutputTokens(request, settings), ResolveTemperature(request), stream: false);

        using var http = JsonPost(Combine(BaseUrl(settings), "api/chat"), body);
        await AuthAsync(http, settings, cancellationToken).ConfigureAwait(false);

        var watch = Stopwatch.StartNew();
        var response = await SendForBodyAsync(CreateClient(), http, requested, cancellationToken).ConfigureAwait(false);
        return ParseResponse(response, requested, watch.Elapsed);
    }

    public override async IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = Settings;
        var model = ModelFor(settings, request.Model);
        var requested = request.Model with { Provider = Key, Model = model };
        var body = BuildBody(request, model, ResolveMaxOutputTokens(request, settings), ResolveTemperature(request), stream: true);

        using var http = JsonPost(Combine(BaseUrl(settings), "api/chat"), body, accept: "application/x-ndjson");
        await AuthAsync(http, settings, cancellationToken).ConfigureAwait(false);

        using var response = await SendForStreamAsync(CreateClient(), http, requested, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        LlmUsage? usage = null;
        string? finish = null;
        string? served = null;
        var calls = new List<ToolCall>();
        await foreach (var line in NdjsonReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            using var doc = Json.ParseDocument(line, Key);
            var root = doc.RootElement;
            if (Json.GetString(root, "error") is { } error)
            {
                throw new LlmResponseException($"{Key} stream error: {error}", Key, requested, line);
            }

            served ??= Json.GetString(root, "model");
            if (Json.GetObject(root, "message") is { } message)
            {
                if (Json.GetString(message, "content") is { Length: > 0 } text) yield return ChatDelta.Text(text);
                if (ParseToolCalls(message, calls.Count) is { } chunkCalls) calls.AddRange(chunkCalls);
            }

            if (Json.GetBool(root, "done"))
            {
                usage = ParseUsage(root);
                finish = Json.GetString(root, "done_reason");
                break;
            }
        }

        var mapped = MapDoneReason(finish);
        if (calls.Count > 0 && mapped == FinishReasons.Stop) mapped = FinishReasons.ToolCalls;
        yield return ChatDelta.Final(usage, mapped, calls.Count == 0 ? null : calls, requested with { Model = served ?? model });
    }

    public override async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var settings = Settings;
        using var http = new HttpRequestMessage(HttpMethod.Get, Combine(BaseUrl(settings), "api/tags"));
        await AuthAsync(http, settings, cancellationToken).ConfigureAwait(false);
        var body = await SendForBodyAsync(CreateClient(), http, null, cancellationToken).ConfigureAwait(false);

        using var doc = Json.ParseDocument(body, Key);
        var list = new List<ModelInfo>();
        if (Json.GetArray(doc.RootElement, "models") is { } models)
        {
            foreach (var item in models.EnumerateArray())
            {
                var name = Json.GetString(item, "name") ?? Json.GetString(item, "model");
                if (string.IsNullOrEmpty(name)) continue;
                var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (Json.GetObject(item, "details") is { } details)
                {
                    foreach (var field in new[] { "family", "parameter_size", "quantization_level", "format" })
                    {
                        if (Json.GetString(details, field) is { } value) metadata[field] = value;
                    }
                }

                if (item.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number) metadata["size"] = size.GetRawText();
                if (IsCloudProxied(name)) metadata[CloudProxiedMetadata] = "true";
                var known = ModelCatalog.Find(new ModelRef(ProviderKeys.Ollama, name));
                DateTimeOffset? modified = DateTimeOffset.TryParse(Json.GetString(item, "modified_at"), out var when) ? when : null;
                list.Add(new ModelInfo(Key, name)
                {
                    DisplayName = known?.DisplayName ?? DisplayNameFor(name),
                    ContextTokens = known?.ContextTokens,
                    MaxOutputTokens = known?.MaxOutputTokens,
                    Created = modified,
                    Metadata = metadata,
                });
            }
        }

        return list.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public override async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
    {
        var settings = Settings;
        var model = request.Model.HasModel ? ModelFor(settings, request.Model) : throw new LlmConfigurationException($"{Key} embeddings need an explicit model.", Key);
        var requested = request.Model with { Provider = Key, Model = model };
        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = new JsonArray(request.Inputs.Select(i => (JsonNode?)i).ToArray()),
        };
        Json.MergeExtras(body, request.Extras);

        using var http = JsonPost(Combine(BaseUrl(settings), "api/embed"), body);
        await AuthAsync(http, settings, cancellationToken).ConfigureAwait(false);

        var watch = Stopwatch.StartNew();
        var response = await SendForBodyAsync(CreateClient(), http, requested, cancellationToken).ConfigureAwait(false);
        using var doc = Json.ParseDocument(response, Key);
        var root = doc.RootElement;
        var vectors = new List<float[]>();
        if (Json.GetArray(root, "embeddings") is { } embeddings)
        {
            foreach (var vector in embeddings.EnumerateArray())
            {
                vectors.Add(vector.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray());
            }
        }

        var served = Json.GetString(root, "model");
        return new EmbeddingResult(vectors, requested with { Model = served ?? model }, ParseUsage(root), watch.Elapsed, response);
    }
}
