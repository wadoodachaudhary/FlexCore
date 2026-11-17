using System.Text.RegularExpressions;

namespace Fx.ControlKit.Migration;

public enum SecuritySeverity
{
    Info,
    Low,
    Medium,
    High,
    Critical
}

/// <summary>One file the analyzer reads. Paths are display names; the content is the text that was scanned.</summary>
public sealed record SecuritySourceFile(string Path, string Content);

/// <summary>A rule finding. <see cref="IntroducedByMigration"/> is true when the paired source file does not contain the same rule.</summary>
public sealed class SecurityFinding
{
    public string Rule { get; init; } = "";
    public SecuritySeverity Severity { get; init; }
    public string File { get; init; } = "";
    public int Line { get; init; }
    public string Cwe { get; init; } = "";
    public string Title { get; init; } = "";
    public string Explanation { get; init; } = "";
    public string SuggestedFix { get; init; } = "";
    public bool IntroducedByMigration { get; init; }
}

public sealed class SecurityScan
{
    public IReadOnlyList<SecurityFinding> Findings { get; init; } = [];
    /// <summary>Set when a language model was asked to comment. The rule findings do not depend on it.</summary>
    public string? LlmSummary { get; init; }
}

/// <summary>Optional model pass. Return a short summary, or null to keep the rule results only.</summary>
public delegate string? SecurityNarrator(IReadOnlyList<SecurityFinding> findings, string combinedSource);

/// <summary>Rule-based scan of converted code, with an optional comparison against the source it came from.</summary>
public static class SecurityAnalyzer
{
    private sealed record Rule(string Id, SecuritySeverity Severity, string Cwe, string Title, string Explanation, string Fix, string Pattern);

    private static readonly Rule[] Rules =
    [
        new("sql-injection", SecuritySeverity.High, "CWE-89", "SQL built from concatenated input",
            "A SQL statement is assembled from strings or an interpolated value. A caller can change the query.",
            "Use parameters. Do not concatenate user input into the statement text.",
            @"(?i)(select\s+.+\+|insert\s+.+\+|execute\s*\(\s*(f?[""']|\$"")|cursor\.execute\s*\(\s*f?[""'].*\{|sp_executesql\s*\+|SqlCommand\s*\([^)]*\+)"),
        new("command-injection", SecuritySeverity.High, "CWE-78", "Command built from external input",
            "A process or shell is started from a string that can include caller data.",
            "Pass an argument array and do not use a shell. Reject unexpected characters.",
            @"(?i)(Process\.Start\s*\(|os\.system\s*\(|subprocess\.(run|call|Popen)\s*\([^)]*shell\s*=\s*True|`[^`]+`|\bsystem\s*\()"),
        new("path-traversal", SecuritySeverity.High, "CWE-22", "Path taken from input",
            "A file path is combined with a request or argument value without a root check.",
            "Resolve the full path and require it to stay under a fixed directory.",
            @"(?i)(Path\.Combine\s*\([^)]*Request\.|open\s*\(\s*request\.|File\.(ReadAllText|Open)\s*\(\s*(path|input|name)|os\.path\.join\s*\([^)]*request)"),
        new("hardcoded-secret", SecuritySeverity.High, "CWE-798", "Hardcoded secret",
            "A password, token, or private key is written in the source.",
            "Read the secret from the host's secret store. Do not commit it.",
            @"(?i)((password|passwd|api[_-]?key|secret|token)\s*=\s*[""'][^""']{4,}[""']|AKIA[0-9A-Z]{16}|BEGIN (RSA |OPENSSH )?PRIVATE KEY)"),
        new("unsafe-deserialize", SecuritySeverity.Critical, "CWE-502", "Unsafe deserialization",
            "The deserializer can construct arbitrary objects from data the caller controls.",
            "Use a format that only reads data, such as JSON, and reject the binary formatter or pickle.",
            @"(?i)(BinaryFormatter|pickle\.loads|yaml\.load\s*\(|ObjectInputStream|Marshal\.Load|TypeNameHandling\s*=\s*TypeNameHandling\.All)"),
        new("weak-crypto", SecuritySeverity.Medium, "CWE-327", "Weak cryptography",
            "MD5, SHA-1, DES, or ECB mode is not a safe choice for a security property.",
            "Use SHA-256 or stronger for hashes, and AES-GCM for encryption.",
            @"(?i)(\bMD5\b|\bSHA1\b|SHA1CryptoServiceProvider|DESCryptoServiceProvider|CipherMode\.ECB|hashlib\.md5|hashlib\.sha1)"),
        new("missing-validation", SecuritySeverity.Medium, "CWE-20", "Request input used without a check",
            "A request value is used directly. Nothing in the statement constrains its shape.",
            "Validate the value against an allow-list before it reaches a query, path, or command.",
            @"(?i)(Request\.(Query|Form|Cookies)\[|request\.(args|form|values)\.get\()"),
        new("insecure-temp", SecuritySeverity.Medium, "CWE-377", "Predictable temporary file",
            "The temporary name can be guessed or the file is created in a shared directory without exclusive create.",
            "Use a random file created with exclusive access, and delete it when finished.",
            @"(?i)(Path\.GetTempFileName\s*\(|tempfile\.mktemp\s*\(|/tmp/[A-Za-z0-9_.-]+)"),
        new("eval-exec", SecuritySeverity.Critical, "CWE-95", "Dynamic code execution",
            "eval or exec runs text as code.",
            "Remove eval. If a template is required, use a renderer that does not execute code.",
            @"(?i)(\beval\s*\(|\bexec\s*\(|Invoke-Expression|Function\s*\(\s*[""'])")
    ];

    public static SecurityScan Scan(IReadOnlyList<SecuritySourceFile> target, IReadOnlyList<SecuritySourceFile>? source = null, SecurityNarrator? narrator = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        var sourceRules = SourceRules(source);
        var findings = new List<SecurityFinding>();
        foreach (var file in target)
        {
            var lines = file.Content.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var rule in Rules)
                {
                    if (!Regex.IsMatch(lines[i], rule.Pattern)) continue;
                    var key = rule.Id + "|" + Path.GetFileName(file.Path);
                    findings.Add(new SecurityFinding
                    {
                        Rule = rule.Id,
                        Severity = rule.Severity,
                        File = file.Path,
                        Line = i + 1,
                        Cwe = rule.Cwe,
                        Title = rule.Title,
                        Explanation = rule.Explanation,
                        SuggestedFix = rule.Fix,
                        IntroducedByMigration = source is not null && !sourceRules.Contains(key)
                    });
                }
            }
        }
        string? summary = null;
        if (narrator is not null)
        {
            var combined = string.Join("\n", target.Select(file => file.Path));
            summary = narrator(findings, combined);
        }
        return new SecurityScan { Findings = findings, LlmSummary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim() };
    }

    private static HashSet<string> SourceRules(IReadOnlyList<SecuritySourceFile>? source)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return keys;
        foreach (var file in source)
        {
            var lines = file.Content.Replace("\r\n", "\n").Split('\n');
            foreach (var line in lines)
                foreach (var rule in Rules)
                    if (Regex.IsMatch(line, rule.Pattern))
                        keys.Add(rule.Id + "|" + Path.GetFileName(file.Path));
        }
        return keys;
    }
}
