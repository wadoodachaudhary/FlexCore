using Fx.ControlKit.Preview;

internal static class SnippetSandboxChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var python = SnippetSandbox.FindRuntime("python");
        var node = SnippetSandbox.FindRuntime("javascript");
        var dotnet = SnippetSandbox.FindRuntime("csharp");
        check(python.IsAvailable && node.IsAvailable, "python3 and node are visible on PATH");
        check(dotnet.Language == "csharp", "C# maps to the dotnet SDK");

        var missing = await SnippetSandbox.RunAsync(new SnippetRunRequest { Language = "fortran", Code = "print 1" });
        check(missing.Status == SnippetRunStatus.Rejected && missing.Message == SnippetSandbox.UnsupportedLanguage, "an unknown language is rejected");

        var empty = await SnippetSandbox.RunAsync(new SnippetRunRequest { Language = "python", Code = "  " });
        check(empty.Status == SnippetRunStatus.Rejected && empty.Message == "Add code to run.", "an empty snippet is rejected");

        Environment.SetEnvironmentVariable("FLEXCORE_TEST_SECRET", "super-secret");
        try
        {
            var ran = await SnippetSandbox.RunAsync(new SnippetRunRequest
            {
                Language = "python",
                Code = "import os\nprint(os.environ.get('FLEXCORE_TEST_SECRET', ''))\nprint(1)\n",
                Timeout = TimeSpan.FromSeconds(8)
            });
            check(ran.Status == SnippetRunStatus.Succeeded && ran.StandardOutput.Contains('1'), "python prints its result");
            check(!ran.StandardOutput.Contains("super-secret"), "the child environment does not receive parent secrets");
            check(ran.Limits.Any(limit => limit.StartsWith("cpu ", StringComparison.Ordinal)), "the result names the resource limits");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLEXCORE_TEST_SECRET", null);
        }

        string? directory;
        using (var prepared = SnippetSandbox.Prepare(new SnippetRunRequest { Language = "python", Code = "print(1)\n" }))
        {
            directory = prepared.WorkingDirectory;
            prepared.Launch.Validate();
            check(prepared.Launch.Kind == ProgramPreviewKind.Console, "a snippet prepares a console ProgramLaunch");
            var script = File.ReadAllText(Path.Combine(prepared.WorkingDirectory, "limit.sh"));
            check(script.Contains("ulimit -t", StringComparison.Ordinal) && script.Contains("exec \"$@\"", StringComparison.Ordinal), "the launch wrapper applies ulimit before exec");
            check(prepared.Launch.Arguments.Contains(prepared.SourcePath), "the launch arguments point at the snippet file");
        }
        check(!Directory.Exists(directory), "dispose removes the private directory");

        var limited = await SnippetSandbox.RunAsync(new SnippetRunRequest
        {
            Language = "python",
            Code = "import resource\nprint(resource.getrlimit(resource.RLIMIT_CPU)[0])\n",
            CpuSeconds = 4,
            Timeout = TimeSpan.FromSeconds(8)
        });
        check(limited.Status == SnippetRunStatus.Succeeded && limited.StandardOutput.Trim() == "4", "the child CPU limit is the requested value");

        var hung = await SnippetSandbox.RunAsync(new SnippetRunRequest
        {
            Language = "python",
            Code = "while True:\n    pass\n",
            Timeout = TimeSpan.FromSeconds(2),
            CpuSeconds = 20
        });
        check(hung.Status is SnippetRunStatus.TimedOut or SnippetRunStatus.Failed, "a tight loop is stopped at the time limit");

        var js = await SnippetSandbox.RunAsync(new SnippetRunRequest
        {
            Language = "javascript",
            Code = "console.log('ok')\n",
            Timeout = TimeSpan.FromSeconds(8)
        });
        check(js.Status == SnippetRunStatus.Succeeded && js.StandardOutput.Contains("ok"), "node runs a javascript snippet");
    }
}
