namespace Fx.ControlKit.Llm.CodeTools;

/// <summary>One AI code tool. The shell catalog uses the same ids.</summary>
public enum CodeToolKind
{
    Explainer,
    Generator,
    Assistant,
    Enhancer,
    CommentGenerator,
    DocumentationGenerator,
    UnitTestGenerator,
    DiagramGenerator,
    DiagramToCode,
    Reviewer,
    Optimizer,
    Chat
}

/// <summary>Which Mermaid diagram to ask for, or to draw offline.</summary>
public enum DiagramKind
{
    Class,
    Sequence,
    Flow
}

public enum CodeToolStatus
{
    Succeeded,
    NotConfigured,
    Failed,
    /// <summary>A structural class diagram was drawn without a model.</summary>
    OfflineFallback,
    /// <summary>An image was sent to a model that does not accept images.</summary>
    NeedsVision
}

/// <summary>How a tool page should present <see cref="CodeToolResult"/>.</summary>
public enum CodeToolPresentation
{
    Markdown,
    Code,
    Diagram,
    Suggestions,
    Findings,
    Documentation,
    Optimized,
    Chat
}

public sealed record CodeToolFile(string Name, string Content);

/// <summary>An uploaded diagram image. <see cref="MimeType"/> is image/png, image/jpeg, image/webp, or image/gif.</summary>
public sealed record CodeToolImage(byte[] Bytes, string MimeType);

public sealed record CodeSuggestion(string Title, string Before, string After, string? Rationale);

/// <summary>One review finding. <see cref="Severity"/> is high, medium, or low. <see cref="Line"/> is 1-based when the model names a line.</summary>
public sealed record CodeFinding(string Severity, int? Line, string Message, string? Recommendation);

public sealed class CodeToolRequest
{
    public required CodeToolKind Tool { get; init; }
    public string? Code { get; init; }
    public IReadOnlyList<CodeToolFile>? Files { get; init; }
    public string? Instruction { get; init; }
    public string? Language { get; init; }
    public string? TargetLanguage { get; init; }
    public DiagramKind Diagram { get; init; } = DiagramKind.Class;
    public string? Mermaid { get; init; }
    public CodeToolImage? Image { get; init; }
    /// <summary>Overrides <see cref="CodeToolOptions.Model"/> for this call. <c>provider:model</c>.</summary>
    public string? Model { get; init; }
    /// <summary>Draw a structural class diagram and do not call the model.</summary>
    public bool PreferOffline { get; init; }
}

public sealed class CodeToolResult
{
    public CodeToolKind Tool { get; init; }
    public CodeToolStatus Status { get; init; }
    /// <summary>Markdown, code, or Mermaid, whichever the tool leads with.</summary>
    public string Text { get; init; } = "";
    public string? Code { get; init; }
    public string? Mermaid { get; init; }
    public string? Summary { get; init; }
    public string? Explanation { get; init; }
    public IReadOnlyList<CodeSuggestion> Suggestions { get; init; } = [];
    public IReadOnlyList<CodeFinding> Findings { get; init; } = [];
    public string? Language { get; init; }
    public string? FileName { get; init; }
    public string? SetupMessage { get; init; }
    public string? Error { get; init; }
    public bool UsedOfflineFallback { get; init; }
    public ModelRef? Model { get; init; }
}

/// <summary>A streamed piece of a tool reply. <see cref="Final"/> is set on the last item.</summary>
public sealed record CodeToolDelta(string? TextDelta, CodeToolResult? Final);

public sealed record CodeToolAvailability(bool IsConfigured, string? Provider, ModelRef? Model, bool SupportsStreaming, bool SupportsVision, string Message)
{
    public static CodeToolAvailability None { get; } = new(false, null, null, false, false, CodeToolMessages.SetupMessage);
}

/// <summary>What a tool needs from the host. <see cref="Id"/> matches the shell catalog id.</summary>
public sealed record CodeToolDescriptor(
    CodeToolKind Kind,
    string Id,
    string Title,
    string Description,
    CodeToolPresentation Presentation,
    bool NeedsCode,
    bool NeedsInstruction,
    bool NeedsTargetLanguage,
    bool NeedsFiles,
    bool AcceptsImage);

public sealed record CodeChatTurn(string Role, string Text);

/// <summary>
/// A coding chat whose history stays on this instance. Create one per circuit
/// with <see cref="ICodeToolService.OpenChat"/>. Not thread-safe.
/// </summary>
public interface ICodeChat
{
    IReadOnlyList<CodeChatTurn> Turns { get; }
    Task<CodeToolResult> SendAsync(string message, CancellationToken cancellationToken = default);
    IAsyncEnumerable<CodeToolDelta> SendStreamingAsync(string message, CancellationToken cancellationToken = default);
    void Reset();
}

public interface ICodeToolService
{
    CodeToolAvailability GetAvailability(string? model = null);
    Task<CodeToolResult> RunAsync(CodeToolRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<CodeToolDelta> StreamAsync(CodeToolRequest request, CancellationToken cancellationToken = default);
    ICodeChat OpenChat(string? model = null);
}

/// <summary>Bound from <c>Llm:CodeTools</c>. Keys themselves stay in the provider's environment variables.</summary>
public sealed class CodeToolOptions
{
    public const string SectionName = "Llm:CodeTools";

    /// <summary><c>provider:model</c>. Empty uses the first configured provider.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Local Ollama is treated as configured only when this is true, when
    /// <see cref="Model"/> names Ollama, or when <c>Llm:Ollama</c> sets an endpoint, key, or model.
    /// The provider's default localhost URL alone does not count.
    /// </summary>
    public bool AllowImplicitOllama { get; set; }
}

/// <summary>Menu order and input rules. Ids match <c>ShellToolCatalog</c>. The converter and the runner are not model calls.</summary>
public static class CodeToolCatalog
{
    public static IReadOnlyList<CodeToolDescriptor> All { get; } =
    [
        Describe(CodeToolKind.Explainer, "explainer", "Code Explainer", "Understand complex code with clear explanations.", CodeToolPresentation.Markdown, code: true, instruction: false, target: false, files: false, image: false),
        Describe(CodeToolKind.Generator, "generator", "Code Generator", "Generate code snippets easily and efficiently.", CodeToolPresentation.Code, false, true, true, false, false),
        Describe(CodeToolKind.Assistant, "assistant", "Code Assistant", "AI-driven coding assistance on demand.", CodeToolPresentation.Code, true, true, false, false, false),
        Describe(CodeToolKind.Enhancer, "enhancer", "Code Enhancer", "Improve code quality with smart suggestions.", CodeToolPresentation.Suggestions, true, false, false, false, false),
        Describe(CodeToolKind.CommentGenerator, "comments", "Comment Generator", "Add docstrings and comments in the language's idiom.", CodeToolPresentation.Code, true, false, false, false, false),
        Describe(CodeToolKind.DocumentationGenerator, "documentation", "Documentation Generator", "Docstrings, READMEs and API docs for whole projects.", CodeToolPresentation.Documentation, false, false, false, true, false),
        Describe(CodeToolKind.UnitTestGenerator, "unit-tests", "Unit Test Generator", "Create unit tests to ensure code quality.", CodeToolPresentation.Code, true, false, false, false, false),
        Describe(CodeToolKind.Reviewer, "reviewer", "Code Reviewer", "Find bugs and security issues with an expert AI review.", CodeToolPresentation.Findings, true, false, false, false, false),
        Describe(CodeToolKind.Optimizer, "optimizer", "Code Optimizer", "Make your code faster and leaner in one click.", CodeToolPresentation.Optimized, true, false, false, false, false),
        Describe(CodeToolKind.DiagramGenerator, "diagram", "Diagram Generator", "Generate visual diagrams from your code.", CodeToolPresentation.Diagram, true, false, false, false, false),
        Describe(CodeToolKind.DiagramToCode, "diagram-to-code", "Diagram to Code", "Convert diagrams into working code.", CodeToolPresentation.Code, false, false, true, false, true),
        Describe(CodeToolKind.Chat, "chat", "New Chat", "Chat with AI to solve any coding problem.", CodeToolPresentation.Chat, false, true, false, false, false)
    ];

    public static CodeToolDescriptor Get(CodeToolKind kind) => All.First(item => item.Kind == kind);

    public static bool TryGet(string? id, out CodeToolDescriptor? descriptor)
    {
        descriptor = All.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        return descriptor is not null;
    }

    private static CodeToolDescriptor Describe(CodeToolKind kind, string id, string title, string description, CodeToolPresentation presentation, bool code, bool instruction, bool target, bool files, bool image) =>
        new(kind, id, title, description, presentation, code, instruction, target, files, image);
}

/// <summary>Shared copy for an unconfigured model and for images the model cannot see.</summary>
public static class CodeToolMessages
{
    public const string SetupMessage =
        "No language model is configured. Add a provider under Llm, put the key in the provider's environment variable (for example OPENAI_API_KEY or ANTHROPIC_API_KEY), and set Llm:CodeTools:Model to provider:model. A local Ollama daemon is used when Llm:CodeTools:AllowImplicitOllama is true or when Llm:Ollama sets an endpoint, key, or model.";

    public const string VisionMessage =
        "This model does not accept images. Paste Mermaid text, or choose a vision-capable model.";
}
