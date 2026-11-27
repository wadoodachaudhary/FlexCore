namespace Fx.ControlKit.Preview;

/// <summary>
/// Server-side launches for Java and Python programs. The browser never supplies the command.
/// A missing JDK or interpreter becomes a short process whose error text is the install note.
/// </summary>
public static class LanguageLaunch
{
    public static ProgramLaunch JavaSources(string workingDirectory, string mainClass, IReadOnlyList<string> relativeSources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mainClass);
        if (relativeSources is null || relativeSources.Count == 0)
            throw new ArgumentException("Name the Java sources to compile.", nameof(relativeSources));
        var files = string.Join(' ', relativeSources.Select(ShellQuote));
        var script = string.Join('\n',
        [
            "set -eu",
            "JAVA=$(command -v java || true)",
            "JAVAC=$(command -v javac || true)",
            "if [ -z \"$JAVA\" ] || [ -z \"$JAVAC\" ]; then",
            "  printf '%s\\n' 'Install a JDK so java and javac are on PATH. On the demo Mac, Java 18 is /usr/bin/java.' >&2",
            "  exit 1",
            "fi",
            "mkdir -p .preview-classes",
            "\"$JAVAC\" -encoding UTF-8 -d .preview-classes " + files,
            "exec \"$JAVA\" -cp .preview-classes " + mainClass
        ]);
        return Shell(workingDirectory, script, mainClass, ProgramPreviewKind.Gui, null);
    }

    public static ProgramLaunch PythonScript(string workingDirectory, string relativeScript, string requiredModule, ProgramPreviewKind kind = ProgramPreviewKind.Gui)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeScript);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredModule);
        if (requiredModule.Any(ch => !char.IsLetterOrDigit(ch) && ch != '_' && ch != '.'))
            throw new ArgumentException("The module name is not an import name.", nameof(requiredModule));
        var script = string.Join('\n',
        [
            "set -eu",
            "PYTHON=$(command -v python3 || command -v python || true)",
            "if [ -z \"$PYTHON\" ]; then",
            "  printf '%s\\n' 'python3 is not on PATH.' >&2",
            "  exit 1",
            "fi",
            "if ! \"$PYTHON\" -c 'import " + requiredModule + "' >/dev/null 2>&1; then",
            "  printf '%s\\n' 'Install " + requiredModule + " for this preview (pip install " + requiredModule + ").' >&2",
            "  exit 1",
            "fi",
            "exec \"$PYTHON\" " + ShellQuote(relativeScript)
        ]);
        // The preview host gives the process a private HOME. Python would otherwise miss packages
        // installed for the real user. Capture that site while HOME is still the operator's.
        IReadOnlyDictionary<string, string?>? environment = null;
        var userSite = UserSitePackages();
        if (!string.IsNullOrWhiteSpace(userSite))
            environment = new Dictionary<string, string?> { ["PYTHONPATH"] = userSite };
        return Shell(workingDirectory, script, relativeScript, kind, environment);
    }

    /// <summary>The current user's site-packages, or null when Python cannot be asked.</summary>
    public static string? UserSitePackages()
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("python3")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("import site; print(site.getusersitepackages())");
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(5000) || process.ExitCode != 0 || output.Length == 0) return null;
            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static ProgramLaunch Shell(string workingDirectory, string script, string label, ProgramPreviewKind kind, IReadOnlyDictionary<string, string?>? environment)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            throw new ArgumentException("WorkingDirectory must exist.", nameof(workingDirectory));
        var launch = new ProgramLaunch
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? ["/d", "/c", script] : ["-c", script],
            WorkingDirectory = workingDirectory,
            Kind = kind,
            Label = label,
            Environment = environment
        };
        launch.Validate();
        return launch;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
