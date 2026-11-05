using System.Text;

namespace Fx.ControlKit.Llm.CodeTools;

/// <summary>Prompt text for each tool. Hosts can read these; the service sends them as the system and user messages.</summary>
public static class CodeToolPrompts
{
    public static string SystemPrompt(CodeToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var language = CodeLanguages.FindOrDefault(request.Language);
        var target = CodeLanguages.FindOrDefault(request.TargetLanguage ?? request.Language);
        var tool = "Tool: " + request.Tool + ".";
        var body = request.Tool switch
        {
            CodeToolKind.Explainer =>
                "You explain source code to a developer who does not know it. Write clear Markdown: a short summary, then what the code does, then anything surprising. Quote only short excerpts.",
            CodeToolKind.Generator =>
                "You write " + target.Label + " from a natural-language request. Return one fenced " + target.Label + " code block and nothing outside it.",
            CodeToolKind.Assistant =>
                "You change the code to follow the instruction: fix a bug, improve it, or add a feature. Return the full updated file in one fenced code block, then a short Markdown list of what changed.",
            CodeToolKind.Enhancer =>
                "You suggest refactorings. Reply with JSON only: {\"summary\": string, \"suggestions\": [{\"title\": string, \"before\": string, \"after\": string, \"rationale\": string}]}. before and after are code, not diffs.",
            CodeToolKind.CommentGenerator =>
                "You add documentation to existing code without changing behavior. " + CommentRules(language) + " Return the full commented file in one fenced code block.",
            CodeToolKind.DocumentationGenerator =>
                "You document a whole project. " + CommentRules(language) + " Reply with Markdown only: a README (purpose, how to build, how to run) and an API section that names each public type and method. Do not wrap the whole answer in a code fence.",
            CodeToolKind.UnitTestGenerator =>
                "You write tests in " + language.TestFramework + " for the " + language.Label + " code, including edge cases. Return one fenced test file and nothing else.",
            CodeToolKind.DiagramGenerator =>
                "You draw a " + request.Diagram + " diagram of the code. Reply with JSON only: {\"summary\": string, \"mermaid\": string}. mermaid is a single " + MermaidHeader(request.Diagram) + " diagram.",
            CodeToolKind.DiagramToCode =>
                "You turn a diagram into working " + target.Label + ". The diagram may be Mermaid text or an attached image of a diagram. Return one fenced " + target.Label + " code block.",
            CodeToolKind.Reviewer =>
                "You review code for bugs and security issues. Reply with JSON only: {\"summary\": string, \"findings\": [{\"severity\": \"high\"|\"medium\"|\"low\", \"line\": number|null, \"message\": string, \"recommendation\": string}]}. Mention a line number when you can.",
            CodeToolKind.Optimizer =>
                "You make the code faster and leaner without changing its results. Reply with JSON only: {\"explanation\": string, \"code\": string}. code is the full optimized file.",
            CodeToolKind.Chat =>
                "You are a coding assistant. Answer in Markdown. Be concrete, and show code in fenced blocks when it helps.",
            _ => "You help a developer work with source code."
        };
        return tool + "\n" + body;
    }

    /// <summary>The comment convention shared by Comment Generator and Documentation Generator.</summary>
    public static string CommentRules(CodeLanguage language) =>
        "Write comments in the " + language.Label + " idiom: " + language.CommentIdiom + ".";

    public static string UserPrompt(CodeToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var language = CodeLanguages.FindOrDefault(request.Language);
        var target = CodeLanguages.FindOrDefault(request.TargetLanguage ?? request.Language);
        var builder = new StringBuilder();
        builder.Append("Language: ").AppendLine(language.Label);
        if (request.Tool is CodeToolKind.Generator or CodeToolKind.DiagramToCode)
            builder.Append("Target language: ").AppendLine(target.Label);
        if (request.Tool == CodeToolKind.UnitTestGenerator)
            builder.Append("Test framework: ").AppendLine(language.TestFramework);
        if (request.Tool == CodeToolKind.DiagramGenerator)
            builder.Append("Diagram: ").AppendLine(request.Diagram.ToString());
        if (!string.IsNullOrWhiteSpace(request.Instruction))
        {
            builder.AppendLine("Instruction:");
            builder.AppendLine(request.Instruction.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.Mermaid))
        {
            builder.AppendLine("Mermaid:");
            builder.AppendLine(request.Mermaid.Trim());
        }

        if (request.Files is { Count: > 0 })
        {
            builder.AppendLine("Files:");
            foreach (var file in request.Files)
            {
                builder.Append("--- ").AppendLine(string.IsNullOrWhiteSpace(file.Name) ? "file" : file.Name.Trim());
                builder.AppendLine(file.Content ?? "");
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.Code))
        {
            builder.AppendLine("Code:");
            builder.AppendLine(request.Code);
        }

        if (request.Image is not null)
            builder.AppendLine("An image of the diagram is attached.");
        return builder.ToString().Trim();
    }

    public static string MermaidHeader(DiagramKind kind) => kind switch
    {
        DiagramKind.Sequence => "sequenceDiagram",
        DiagramKind.Flow => "flowchart TD",
        _ => "classDiagram"
    };
}
