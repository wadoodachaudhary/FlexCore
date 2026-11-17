using Fx.ControlKit.Migration;

internal static class SecurityAnalyzerChecks
{
    public static void Run(Action<bool, string> check)
    {
        var target = new SecuritySourceFile("Shop.cs", """
            var sql = "SELECT * FROM Users WHERE Name = '" + Request.Query["name"] + "'";
            var password = "s3cret-value";
            var formatter = new BinaryFormatter();
            Process.Start(command);
            eval("payload");
            """);
        var source = new SecuritySourceFile("Shop.cs", "var password = \"s3cret-value\";");
        var scan = SecurityAnalyzer.Scan([target], [source], (_, _) => "Two new issues.");
        check(scan.Findings.Any(finding => finding.Cwe == "CWE-89" && finding.Line == 1), "SQL concatenation is CWE-89 on its line");
        check(scan.Findings.Any(finding => finding.Rule == "hardcoded-secret" && !finding.IntroducedByMigration), "a secret that was already in the source is not a regression");
        check(scan.Findings.Any(finding => finding.Rule == "eval-exec" && finding.IntroducedByMigration && finding.Severity == SecuritySeverity.Critical), "eval added by the conversion is a regression");
        check(scan.Findings.Any(finding => finding.Rule == "unsafe-deserialize" && finding.SuggestedFix.Contains("JSON", StringComparison.Ordinal)), "unsafe deserialization names a fix");
        check(scan.LlmSummary == "Two new issues.", "an optional model pass can summarize without changing the rules");

        var onlyRules = SecurityAnalyzer.Scan([new SecuritySourceFile("a.py", "hashlib.md5(data)\nopen('/tmp/session-1', 'w')\n")], null, null);
        check(onlyRules.LlmSummary is null && onlyRules.Findings.Any(finding => finding.Cwe == "CWE-327") && onlyRules.Findings.Any(finding => finding.Cwe == "CWE-377"), "weak crypto and predictable temp files are found without a model");
    }
}
