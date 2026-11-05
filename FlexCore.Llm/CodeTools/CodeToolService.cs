using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fx.ControlKit.Llm.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fx.ControlKit.Llm.CodeTools;

/// <summary>
/// Runs the code tools through <see cref="ILlmClient"/>. When no provider is configured
/// the call returns <see cref="CodeToolStatus.NotConfigured"/> and does not throw.
/// Class diagrams can be drawn offline from C# or Python.
/// </summary>
public sealed class CodeToolService : ICodeToolService
{
    private readonly ILlmClient _client;
    private readonly ICredentialResolver? _credentials;
    private readonly CodeToolOptions _options;
    private readonly ILogger? _logger;

    public CodeToolService(ILlmClient client, ICredentialResolver? credentials = null, CodeToolOptions? options = null, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _credentials = credentials;
        _options = options ?? new CodeToolOptions();
        _logger = logger;
    }

    public CodeToolAvailability GetAvailability(string? model = null)
    {
        var requestedText = string.IsNullOrWhiteSpace(model) ? _options.Model : model;
        ModelRef requested = default;
        var hasRequest = !string.IsNullOrWhiteSpace(requestedText) && ModelRef.TryParse(requestedText, out requested);
        var usable = _client.Providers.Where(provider => IsUsable(provider, requestedText)).ToList();
        ILlmProvider? provider = hasRequest && requested.HasProvider
            ? usable.FirstOrDefault(item => item.Key == requested.Provider)
            : usable.FirstOrDefault();
        if (provider is null)
            return new CodeToolAvailability(false, null, null, false, false, CodeToolMessages.SetupMessage);

        var chosen = hasRequest && requested.HasModel && (!requested.HasProvider || requested.Provider == provider.Key)
            ? requested.WithProvider(provider.Key)
            : new ModelRef(provider.Key, FirstModel(provider));
        return new CodeToolAvailability(
            true,
            provider.Key,
            chosen,
            provider.Supports(LlmCapabilities.Streaming),
            SupportsVision(provider, chosen),
            "Ready.");
    }

    public async Task<CodeToolResult> RunAsync(CodeToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var gated = Gate(request);
        if (gated is not null) return gated;
        var availability = GetAvailability(request.Model);
        var model = availability.Model!.Value;
        try
        {
            var chat = await _client.ChatAsync(Build(request, model), cancellationToken).ConfigureAwait(false);
            return Finish(request, chat.Text, model);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning("Code tool {Tool} failed: {Message}", request.Tool, ex.Message);
            return Fail(request, ex.Message);
        }
    }

    public async IAsyncEnumerable<CodeToolDelta> StreamAsync(CodeToolRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var gated = Gate(request);
        if (gated is not null)
        {
            yield return new CodeToolDelta(FirstText(gated), gated);
            yield break;
        }

        var availability = GetAvailability(request.Model);
        var model = availability.Model!.Value;
        var chatRequest = Build(request, model);
        if (!availability.SupportsStreaming)
        {
            CodeToolResult result;
            try
            {
                var chat = await _client.ChatAsync(chatRequest, cancellationToken).ConfigureAwait(false);
                result = Finish(request, chat.Text, model);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result = Fail(request, ex.Message);
            }

            yield return new CodeToolDelta(FirstText(result), result);
            yield break;
        }

        var builder = new StringBuilder();
        var failed = (Exception?)null;
        IAsyncEnumerator<ChatDelta>? stream = null;
        try
        {
            stream = _client.StreamAsync(chatRequest, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool moved;
                try { moved = await stream.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed = ex;
                    break;
                }
                if (!moved) break;
                var delta = stream.Current;
                if (delta.TextDelta is { Length: > 0 } text)
                {
                    builder.Append(text);
                    yield return new CodeToolDelta(text, null);
                }
            }
        }
        finally
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        }

        if (failed is not null)
        {
            var error = Fail(request, failed.Message);
            yield return new CodeToolDelta(null, error);
            yield break;
        }

        yield return new CodeToolDelta(null, Finish(request, builder.ToString(), model));
    }

    public ICodeChat OpenChat(string? model = null)
    {
        var availability = GetAvailability(model);
        ChatSession? session = null;
        if (availability.IsConfigured && availability.Model is { } chosen)
        {
            session = _client.StartSession(chosen, CodeToolPrompts.SystemPrompt(new CodeToolRequest { Tool = CodeToolKind.Chat }), new ChatSessionOptions
            {
                Temperature = 0.2,
                MaxTurns = 40
            });
        }

        return new CodeChat(availability, session);
    }

    private CodeToolResult? Gate(CodeToolRequest request)
    {
        var offline = TryOffline(request);
        var availability = GetAvailability(request.Model);
        if (offline is not null && (request.PreferOffline || !availability.IsConfigured)) return offline;
        var problem = Validate(request);
        if (problem is not null) return Fail(request, problem);
        if (!availability.IsConfigured) return NotConfigured(request);
        if (request.Image is { Bytes.Length: > 0 } && !availability.SupportsVision) return NeedsVision(request);
        return null;
    }

    private CodeToolResult Finish(CodeToolRequest request, string raw, ModelRef model)
    {
        var parsed = Parse(request, raw, model);
        if (request.Tool == CodeToolKind.DiagramGenerator && string.IsNullOrWhiteSpace(parsed.Mermaid))
        {
            var offline = TryOffline(request);
            if (offline is not null) return offline;
        }

        return parsed;
    }

    private static CodeToolResult? TryOffline(CodeToolRequest request)
    {
        if (request.Tool != CodeToolKind.DiagramGenerator || request.Diagram != DiagramKind.Class) return null;
        if (!StructuralClassDiagram.TryCreate(request.Language, request.Code, out var mermaid)) return null;
        return new CodeToolResult
        {
            Tool = request.Tool,
            Status = CodeToolStatus.OfflineFallback,
            Text = mermaid,
            Mermaid = mermaid,
            Summary = "Drawn from the code structure. A model was not used.",
            Language = CodeLanguages.FindOrDefault(request.Language).Id,
            FileName = "diagram.mmd",
            UsedOfflineFallback = true
        };
    }

    private bool IsUsable(ILlmProvider provider, string? requestedModel)
    {
        if (!provider.IsConfigured) return false;
        if (!string.Equals(provider.Key, ProviderKeys.Ollama, StringComparison.Ordinal)) return true;
        if (_options.AllowImplicitOllama || Names(requestedModel, ProviderKeys.Ollama) || Names(_options.Model, ProviderKeys.Ollama))
            return true;
        try
        {
            var settings = _credentials?.Resolve(provider.Key);
            if (settings is null) return false;
            return settings.HasEndpoint || settings.HasApiKey || settings.HasBasicCredentials
                || !string.IsNullOrWhiteSpace(settings.DefaultModel) || settings.Models.Count > 0;
        }
        catch (LlmException)
        {
            return false;
        }
    }

    private static bool Names(string? model, string provider)
    {
        if (string.IsNullOrWhiteSpace(model) || !ModelRef.TryParse(model, out var parsed)) return false;
        return string.Equals(parsed.Provider, provider, StringComparison.Ordinal);
    }

    private static string FirstModel(ILlmProvider provider) =>
        provider.ConfiguredModels.FirstOrDefault()?.Id ?? "";

    private static bool SupportsVision(ILlmProvider provider, ModelRef model)
    {
        var known = ModelCatalog.Find(model);
        if (known?.Capabilities is { } caps) return (caps & LlmCapabilities.Vision) == LlmCapabilities.Vision;
        return provider.Supports(LlmCapabilities.Vision);
    }

    private static string? Validate(CodeToolRequest request)
    {
        var hasCode = !string.IsNullOrWhiteSpace(request.Code) || request.Files is { Count: > 0 };
        return request.Tool switch
        {
            CodeToolKind.Generator when string.IsNullOrWhiteSpace(request.Instruction) => "Describe the code to generate.",
            CodeToolKind.Assistant when !hasCode => "Add the code to change.",
            CodeToolKind.Assistant when string.IsNullOrWhiteSpace(request.Instruction) => "Add an instruction for the assistant.",
            CodeToolKind.DocumentationGenerator when !hasCode => "Add the project files to document.",
            CodeToolKind.DiagramToCode when string.IsNullOrWhiteSpace(request.Mermaid) && request.Image is not { Bytes.Length: > 0 } => "Paste a Mermaid diagram or upload an image.",
            CodeToolKind.Generator or CodeToolKind.DiagramToCode or CodeToolKind.Chat => null,
            _ when !hasCode => "Add the code to work on.",
            _ => null
        };
    }

    private static ChatRequest Build(CodeToolRequest request, ModelRef model)
    {
        var parts = new List<ContentPart> { new TextPart(CodeToolPrompts.UserPrompt(request)) };
        if (request.Image is { Bytes.Length: > 0 } image)
        {
            var mime = string.IsNullOrWhiteSpace(image.MimeType) ? "image/png" : image.MimeType;
            parts.Add(new ImagePart(image.Bytes, mime));
        }

        return new ChatRequest
        {
            Model = model,
            System = CodeToolPrompts.SystemPrompt(request),
            Messages = [new ChatMessage(ChatRole.User, parts)],
            Temperature = request.Tool is CodeToolKind.Explainer or CodeToolKind.Chat ? 0.2 : 0,
            ResponseFormat = WantsJson(request.Tool) ? ResponseFormat.Json : ResponseFormat.Text
        };
    }

    private static bool WantsJson(CodeToolKind tool) =>
        tool is CodeToolKind.Enhancer or CodeToolKind.DiagramGenerator or CodeToolKind.Reviewer or CodeToolKind.Optimizer;

    private static CodeToolResult Parse(CodeToolRequest request, string raw, ModelRef model)
    {
        var language = CodeLanguages.FindOrDefault(request.TargetLanguage ?? request.Language);
        var result = new CodeToolResult
        {
            Tool = request.Tool,
            Status = CodeToolStatus.Succeeded,
            Text = (raw ?? "").Trim(),
            Language = language.Id,
            Model = model,
            FileName = FileName(request, language)
        };
        return request.Tool switch
        {
            CodeToolKind.Enhancer => ParseSuggestions(result),
            CodeToolKind.Reviewer => ParseFindings(result),
            CodeToolKind.Optimizer => ParseOptimizer(result),
            CodeToolKind.DiagramGenerator => ParseDiagram(result),
            CodeToolKind.DocumentationGenerator => result,
            _ => ParseCode(result)
        };
    }

    private static CodeToolResult ParseSuggestions(CodeToolResult result)
    {
        var suggestions = new List<CodeSuggestion>();
        string? summary = null;
        if (StructuredJson.TryParse(result.Text, out var document) && document is not null)
        {
            using (document)
            {
                summary = StringProp(document.RootElement, "summary");
                if (document.RootElement.TryGetProperty("suggestions", out var list) || TryProperty(document.RootElement, "suggestions", out list))
                {
                    if (list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in list.EnumerateArray())
                        {
                            suggestions.Add(new CodeSuggestion(
                                StringProp(item, "title") ?? "Suggestion",
                                StringProp(item, "before") ?? "",
                                StringProp(item, "after") ?? "",
                                StringProp(item, "rationale")));
                        }
                    }
                }
            }
        }

        return Copy(result, summary: summary, suggestions: suggestions, text: summary ?? result.Text);
    }

    private static CodeToolResult ParseFindings(CodeToolResult result)
    {
        var findings = new List<CodeFinding>();
        string? summary = null;
        if (StructuredJson.TryParse(result.Text, out var document) && document is not null)
        {
            using (document)
            {
                summary = StringProp(document.RootElement, "summary");
                if (TryProperty(document.RootElement, "findings", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        int? line = null;
                        if (TryProperty(item, "line", out var lineValue) && lineValue.TryGetInt32(out var parsed) && parsed > 0)
                            line = parsed;
                        findings.Add(new CodeFinding(
                            (StringProp(item, "severity") ?? "medium").Trim().ToLowerInvariant(),
                            line,
                            StringProp(item, "message") ?? "",
                            StringProp(item, "recommendation")));
                    }
                }
            }
        }

        findings.Sort(static (left, right) =>
        {
            var rank = Rank(left.Severity).CompareTo(Rank(right.Severity));
            return rank != 0 ? rank : (left.Line ?? int.MaxValue).CompareTo(right.Line ?? int.MaxValue);
        });
        return Copy(result, summary: summary, findings: findings, text: summary ?? result.Text);
    }

    private static CodeToolResult ParseOptimizer(CodeToolResult result)
    {
        string? explanation = null;
        string? code = null;
        if (StructuredJson.TryParse(result.Text, out var document) && document is not null)
        {
            using (document)
            {
                explanation = StringProp(document.RootElement, "explanation") ?? StringProp(document.RootElement, "summary");
                code = StringProp(document.RootElement, "code");
            }
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            var split = CodeToolText.SplitFence(result.Text);
            code = split.Code;
            explanation ??= split.Notes;
        }

        return Copy(result, code: code, explanation: explanation, text: explanation ?? result.Text);
    }

    private static CodeToolResult ParseDiagram(CodeToolResult result)
    {
        string? summary = null;
        string? mermaid = null;
        if (StructuredJson.TryParse(result.Text, out var document) && document is not null)
        {
            using (document)
            {
                summary = StringProp(document.RootElement, "summary");
                mermaid = StringProp(document.RootElement, "mermaid");
            }
        }

        mermaid = string.IsNullOrWhiteSpace(mermaid) ? CodeToolText.ExtractMermaid(result.Text) : mermaid.Trim();
        return Copy(result, summary: summary, mermaid: mermaid, text: string.IsNullOrWhiteSpace(summary) ? mermaid ?? result.Text : summary);
    }

    private static CodeToolResult ParseCode(CodeToolResult result)
    {
        if (result.Tool == CodeToolKind.Explainer) return result;
        var split = CodeToolText.SplitFence(result.Text);
        if (split.Code is null) return result;
        return Copy(result, code: split.Code, text: string.IsNullOrWhiteSpace(split.Notes) ? split.Code : result.Text, explanation: split.Notes);
    }

    private static CodeToolResult Copy(
        CodeToolResult result,
        string? text = null,
        string? code = null,
        string? mermaid = null,
        string? summary = null,
        string? explanation = null,
        IReadOnlyList<CodeSuggestion>? suggestions = null,
        IReadOnlyList<CodeFinding>? findings = null) => new()
    {
        Tool = result.Tool,
        Status = result.Status,
        Text = text ?? result.Text,
        Code = code ?? result.Code,
        Mermaid = mermaid ?? result.Mermaid,
        Summary = summary ?? result.Summary,
        Explanation = explanation ?? result.Explanation,
        Suggestions = suggestions ?? result.Suggestions,
        Findings = findings ?? result.Findings,
        Language = result.Language,
        FileName = result.FileName,
        Model = result.Model
    };

    private static int Rank(string severity) => severity switch
    {
        "high" or "critical" or "error" => 0,
        "medium" or "warning" => 1,
        "low" or "info" or "note" => 2,
        _ => 3
    };

    private static string FileName(CodeToolRequest request, CodeLanguage language) => request.Tool switch
    {
        CodeToolKind.Explainer => "explanation.md",
        CodeToolKind.Reviewer => "review.md",
        CodeToolKind.Enhancer => "suggestions.md",
        CodeToolKind.DocumentationGenerator => "README.md",
        CodeToolKind.DiagramGenerator => "diagram.mmd",
        CodeToolKind.UnitTestGenerator => "Tests" + language.FileExtension,
        CodeToolKind.Optimizer => "Optimized" + language.FileExtension,
        CodeToolKind.CommentGenerator => "Commented" + language.FileExtension,
        CodeToolKind.Assistant => "Updated" + language.FileExtension,
        _ => "Program" + language.FileExtension
    };

    private static CodeToolResult NotConfigured(CodeToolRequest request) => new()
    {
        Tool = request.Tool,
        Status = CodeToolStatus.NotConfigured,
        SetupMessage = CodeToolMessages.SetupMessage,
        Text = CodeToolMessages.SetupMessage
    };

    private static CodeToolResult NeedsVision(CodeToolRequest request) => new()
    {
        Tool = request.Tool,
        Status = CodeToolStatus.NeedsVision,
        Error = CodeToolMessages.VisionMessage,
        Text = CodeToolMessages.VisionMessage
    };

    private static CodeToolResult Fail(CodeToolRequest request, string message) => new()
    {
        Tool = request.Tool,
        Status = CodeToolStatus.Failed,
        Error = message,
        Text = message
    };

    private static string FirstText(CodeToolResult result) =>
        result.Text.Length > 0 ? result.Text : result.SetupMessage ?? result.Error ?? "";

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? StringProp(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private sealed class CodeChat : ICodeChat
    {
        private readonly CodeToolAvailability _availability;
        private readonly ChatSession? _session;
        private readonly List<CodeChatTurn> _turns = [];

        public CodeChat(CodeToolAvailability availability, ChatSession? session)
        {
            _availability = availability;
            _session = session;
        }

        public IReadOnlyList<CodeChatTurn> Turns => _turns;

        public void Reset()
        {
            _turns.Clear();
            _session?.Reset();
        }

        public async Task<CodeToolResult> SendAsync(string message, CancellationToken cancellationToken = default)
        {
            var text = message?.Trim() ?? "";
            if (text.Length == 0) return Fail(new CodeToolRequest { Tool = CodeToolKind.Chat }, "Write a message.");
            _turns.Add(new CodeChatTurn("user", text));
            if (_session is null)
            {
                var missing = NotConfigured(new CodeToolRequest { Tool = CodeToolKind.Chat });
                _turns.Add(new CodeChatTurn("assistant", missing.SetupMessage ?? ""));
                return missing;
            }

            try
            {
                var reply = await _session.AskAsync(text, cancellationToken).ConfigureAwait(false);
                _turns.Add(new CodeChatTurn("assistant", reply.Text));
                return new CodeToolResult
                {
                    Tool = CodeToolKind.Chat,
                    Status = CodeToolStatus.Succeeded,
                    Text = reply.Text,
                    Model = reply.Resolved
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var failed = Fail(new CodeToolRequest { Tool = CodeToolKind.Chat }, ex.Message);
                _turns.Add(new CodeChatTurn("assistant", failed.Error ?? ""));
                return failed;
            }
        }

        public async IAsyncEnumerable<CodeToolDelta> SendStreamingAsync(string message, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var text = message?.Trim() ?? "";
            if (text.Length == 0)
            {
                var failed = Fail(new CodeToolRequest { Tool = CodeToolKind.Chat }, "Write a message.");
                yield return new CodeToolDelta(failed.Text, failed);
                yield break;
            }

            _turns.Add(new CodeChatTurn("user", text));
            if (_session is null || !_availability.SupportsStreaming)
            {
                var result = await SendWithoutDuplicateUser(text, cancellationToken).ConfigureAwait(false);
                yield return new CodeToolDelta(result.Text, result);
                yield break;
            }

            var builder = new StringBuilder();
            await foreach (var delta in _session.AskStreamingAsync(text, cancellationToken).ConfigureAwait(false))
            {
                if (delta.TextDelta is { Length: > 0 } piece)
                {
                    builder.Append(piece);
                    yield return new CodeToolDelta(piece, null);
                }
            }

            var finalText = builder.ToString();
            _turns.Add(new CodeChatTurn("assistant", finalText));
            yield return new CodeToolDelta(null, new CodeToolResult
            {
                Tool = CodeToolKind.Chat,
                Status = CodeToolStatus.Succeeded,
                Text = finalText,
                Model = _availability.Model
            });
        }

        private async Task<CodeToolResult> SendWithoutDuplicateUser(string text, CancellationToken cancellationToken)
        {
            _turns.RemoveAt(_turns.Count - 1);
            return await SendAsync(text, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Pulls fenced code and Mermaid source out of a model reply.</summary>
public static class CodeToolText
{
    public static (string? Code, string? Notes) SplitFence(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        var match = Regex.Match(raw, @"```[^\n`]*\n([\s\S]*?)```");
        if (!match.Success) return (null, raw.Trim());
        var code = match.Groups[1].Value.Trim();
        var notes = (raw[..match.Index] + raw[(match.Index + match.Length)..]).Trim();
        return (code, notes);
    }

    public static string? ExtractMermaid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var fenced = Regex.Match(raw, @"```(?:mermaid)?\s*\n([\s\S]*?)```", RegexOptions.IgnoreCase);
        if (fenced.Success && LooksLikeMermaid(fenced.Groups[1].Value)) return fenced.Groups[1].Value.Trim();
        var start = Regex.Match(raw, @"(?m)^(classDiagram|sequenceDiagram|flowchart|graph)\b");
        return start.Success ? raw[start.Index..].Trim() : null;
    }

    private static bool LooksLikeMermaid(string text) =>
        text.Contains("classDiagram", StringComparison.OrdinalIgnoreCase)
        || text.Contains("sequenceDiagram", StringComparison.OrdinalIgnoreCase)
        || text.Contains("flowchart", StringComparison.OrdinalIgnoreCase)
        || text.Contains("graph ", StringComparison.OrdinalIgnoreCase);
}
