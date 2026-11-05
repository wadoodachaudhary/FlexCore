namespace Fx.ControlKit.Llm.CodeTools;

/// <summary>Languages the code tools know how to name, comment, and test.</summary>
public sealed record CodeLanguage(string Id, string Label, string FileExtension, string TestFramework, string CommentIdiom, params string[] Aliases)
{
    public bool Matches(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.Equals(Id, StringComparison.OrdinalIgnoreCase) || text.Equals(Label, StringComparison.OrdinalIgnoreCase))
            return true;
        return Aliases.Any(alias => alias.Equals(text, StringComparison.OrdinalIgnoreCase));
    }
}

public static class CodeLanguages
{
    public static CodeLanguage CSharp { get; } = new("csharp", "C#", ".cs", "xUnit", "XML documentation comments (///) and short // remarks", "cs", "c#", "csharp");
    public static CodeLanguage Python { get; } = new("python", "Python", ".py", "pytest", "PEP 257 docstrings and sparse # comments", "py", "python3");
    public static CodeLanguage JavaScript { get; } = new("javascript", "JavaScript", ".js", "Jest", "JSDoc block comments", "js", "node");
    public static CodeLanguage TypeScript { get; } = new("typescript", "TypeScript", ".ts", "Jest", "JSDoc or TSDoc block comments", "ts");
    public static CodeLanguage Java { get; } = new("java", "Java", ".java", "JUnit 5", "Javadoc comments");
    public static CodeLanguage Cpp { get; } = new("cpp", "C++", ".cpp", "GoogleTest", "/// or Doxygen comments", "c++", "cplusplus");
    public static CodeLanguage C { get; } = new("c", "C", ".c", "a small C assert harness", "block comments above declarations");
    public static CodeLanguage Go { get; } = new("go", "Go", ".go", "the Go testing package", "doc comments immediately above the declaration", "golang");
    public static CodeLanguage Rust { get; } = new("rust", "Rust", ".rs", "Rust's built-in test harness", "/// and //! doc comments", "rs");
    public static CodeLanguage Ruby { get; } = new("ruby", "Ruby", ".rb", "RSpec", "YARD or # comments", "rb");
    public static CodeLanguage Kotlin { get; } = new("kotlin", "Kotlin", ".kt", "JUnit 5", "KDoc comments", "kt");
    public static CodeLanguage Php { get; } = new("php", "PHP", ".php", "PHPUnit", "PHPDoc comments");
    public static CodeLanguage Clojure { get; } = new("clojure", "Clojure", ".clj", "clojure.test", "docstrings on the var", "clj");

    public static IReadOnlyList<CodeLanguage> All { get; } =
    [
        CSharp, Python, JavaScript, TypeScript, Java, Cpp, C, Go, Rust, Ruby, Kotlin, Php, Clojure
    ];

    public static CodeLanguage FindOrDefault(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return CSharp;
        var text = value.Trim();
        if (text.Equals("c", StringComparison.OrdinalIgnoreCase)) return C;
        return All.FirstOrDefault(language => language.Matches(text)) ?? CSharp;
    }
}
