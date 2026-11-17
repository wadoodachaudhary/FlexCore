using System.Diagnostics;
using System.Text;

#pragma warning disable CA1416 // The runner starts a process. Browser hosts show the runtime-missing path instead.

namespace Fx.ControlKit.Preview;

/// <summary>Why a snippet run stopped.</summary>
public enum SnippetRunStatus
{
    Succeeded,
    Failed,
    TimedOut,
    RuntimeMissing,
    Rejected
}

/// <summary>A snippet to run in a constrained subprocess. The host chooses the language; the sandbox chooses the executable.</summary>
public sealed class SnippetRunRequest
{
    public required string Language { get; init; }
    public required string Code { get; init; }
    /// <summary>Wall-clock limit. C# compiles inside this limit, so give it longer than a script.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(8);
    public int CpuSeconds { get; init; } = 10;
    public int MaxFileKilobytes { get; init; } = 2048;
    public int MaxProcesses { get; init; } = 32;
    public int MaxOpenFiles { get; init; } = 64;
    public int MaxOutputChars { get; init; } = 64 * 1024;
    public int MaxCodeChars { get; init; } = 100_000;
}

/// <summary>Output of <see cref="SnippetSandbox.RunAsync"/>.</summary>
public sealed class SnippetRunResult
{
    public SnippetRunStatus Status { get; init; }
    public string Language { get; init; } = "";
    public string? Runtime { get; init; }
    public string StandardOutput { get; init; } = "";
    public string StandardError { get; init; } = "";
    public int? ExitCode { get; init; }
    public string Message { get; init; } = "";
    public bool OutputTruncated { get; init; }
    /// <summary>Limits the child actually received, for example <c>cpu 10s</c>.</summary>
    public IReadOnlyList<string> Limits { get; init; } = [];
}

/// <summary>
/// Files and a <see cref="ProgramLaunch"/> for one snippet.
/// <see cref="ProgramLaunch"/> is what <see cref="IProgramPreviewHost"/> can start
/// as a console preview. <see cref="SnippetSandbox.RunAsync"/> starts that same
/// launch itself and applies the resource limits.
/// </summary>
public sealed class SnippetPreparation : IDisposable
{
    internal SnippetPreparation(ProgramLaunch launch, string directory, string sourcePath, IReadOnlyList<string> limits)
    {
        Launch = launch;
        WorkingDirectory = directory;
        SourcePath = sourcePath;
        Limits = limits;
    }

    public ProgramLaunch Launch { get; }
    public string WorkingDirectory { get; }
    public string SourcePath { get; }
    public IReadOnlyList<string> Limits { get; }
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try
        {
            if (Directory.Exists(WorkingDirectory)) Directory.Delete(WorkingDirectory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Runs untrusted C#, Python, or JavaScript in a subprocess: private temp directory,
/// allow-listed environment, CPU / file / process limits, a wall-clock timeout, and
/// an output cap. This is not a virtual machine. A multi-tenant host that must
/// contain hostile code should add a container or a separate user around it.
/// </summary>
public static class SnippetSandbox
{
    public const string PythonMissing = "Python needs python3 on PATH.";
    public const string JavaScriptMissing = "JavaScript needs Node.js (node) on PATH.";
    public const string CSharpMissing = "C# needs the dotnet SDK on PATH.";
    public const string UnsupportedLanguage = "Supported languages are C#, Python, and JavaScript.";

    public static SnippetRuntime FindRuntime(string? language)
    {
        var id = Normalize(language);
        return id switch
        {
            "python" => new SnippetRuntime(id, Which("python3") ?? Which("python"), PythonMissing),
            "javascript" => new SnippetRuntime(id, Which("node"), JavaScriptMissing),
            "csharp" => new SnippetRuntime(id, Which("dotnet"), CSharpMissing),
            _ => new SnippetRuntime(id, null, UnsupportedLanguage)
        };
    }

    /// <summary>Writes the snippet and returns a console <see cref="ProgramLaunch"/> plus the limit wrapper. The directory lives until the preparation is disposed.</summary>
    public static SnippetPreparation Prepare(SnippetRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var problem = Validate(request);
        if (problem is not null) throw new ArgumentException(problem);
        var runtime = FindRuntime(request.Language);
        if (runtime.Path is null) throw new InvalidOperationException(runtime.MissingMessage);

        var directory = Path.Combine(Path.GetTempPath(), "flexcore-snippet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = WriteSource(directory, runtime.Language, request.Code);
        var limits = new List<string>
        {
            "cpu " + request.CpuSeconds + "s",
            "file " + request.MaxFileKilobytes + "KB",
            "processes " + request.MaxProcesses,
            "files " + request.MaxOpenFiles,
            "wall " + Math.Ceiling(request.Timeout.TotalSeconds) + "s"
        };
        string fileName;
        var arguments = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            fileName = runtime.Path;
            arguments.AddRange(RuntimeArguments(runtime.Language, source, directory));
        }
        else
        {
            var script = Path.Combine(directory, "limit.sh");
            File.WriteAllText(script, LimitScript);
            fileName = "/bin/sh";
            arguments.Add(script);
            arguments.Add(runtime.Path);
            arguments.AddRange(RuntimeArguments(runtime.Language, source, directory).Skip(0));
        }

        var launch = new ProgramLaunch
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = directory,
            Kind = ProgramPreviewKind.Console,
            Label = runtime.Language + " snippet",
            Environment = ScrubbedEnvironment(directory, request)
        };
        launch.Validate();
        return new SnippetPreparation(launch, directory, source, limits);
    }

    public static async Task<SnippetRunResult> RunAsync(SnippetRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var problem = Validate(request);
        if (problem is not null)
        {
            return new SnippetRunResult { Status = SnippetRunStatus.Rejected, Language = Normalize(request.Language), Message = problem };
        }

        var runtime = FindRuntime(request.Language);
        if (runtime.Path is null)
        {
            return new SnippetRunResult
            {
                Status = SnippetRunStatus.RuntimeMissing,
                Language = runtime.Language,
                Message = runtime.MissingMessage
            };
        }

        SnippetPreparation? prepared = null;
        try
        {
            prepared = Prepare(request);
            return await ExecuteAsync(prepared, request, runtime, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return new SnippetRunResult { Status = SnippetRunStatus.Rejected, Language = runtime.Language, Message = ex.Message };
        }
        finally
        {
            prepared?.Dispose();
        }
    }

    private static async Task<SnippetRunResult> ExecuteAsync(SnippetPreparation prepared, SnippetRunRequest request, SnippetRuntime runtime, CancellationToken cancellationToken)
    {
        var launch = prepared.Launch;
        var psi = new ProcessStartInfo(launch.FileName!)
        {
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in launch.Arguments) psi.ArgumentList.Add(argument);
        psi.Environment.Clear();
        if (launch.Environment is not null)
        {
            foreach (var pair in launch.Environment)
            {
                if (pair.Value is not null) psi.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                return new SnippetRunResult { Status = SnippetRunStatus.Failed, Language = runtime.Language, Runtime = runtime.Path, Message = "The runtime did not start.", Limits = prepared.Limits };
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new SnippetRunResult { Status = SnippetRunStatus.Failed, Language = runtime.Language, Runtime = runtime.Path, Message = ex.Message, Limits = prepared.Limits };
        }

        var stdout = ReadCapAsync(process.StandardOutput, request.MaxOutputChars, cancellationToken);
        var stderr = ReadCapAsync(process.StandardError, request.MaxOutputChars, cancellationToken);
        var timeout = request.Timeout < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : request.Timeout;
        var finished = process.WaitForExitAsync(cancellationToken);
        var stop = Task.Delay(timeout, cancellationToken);
        var winner = await Task.WhenAny(finished, stop).ConfigureAwait(false);
        var timedOut = winner != finished && !process.HasExited;
        if (timedOut || cancellationToken.IsCancellationRequested && !process.HasExited)
        {
            TryKill(process);
        }

        try { await finished.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false); }
        catch (TimeoutException) { TryKill(process); }
        catch (OperationCanceledException) { TryKill(process); }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        var truncated = output.Truncated || error.Truncated;
        if (!timedOut && process.HasExited && process.ExitCode == 0)
        {
            return new SnippetRunResult
            {
                Status = SnippetRunStatus.Succeeded,
                Language = runtime.Language,
                Runtime = runtime.Path,
                StandardOutput = output.Text,
                StandardError = error.Text,
                ExitCode = 0,
                Message = truncated ? "Output was cut at the size limit." : "Finished.",
                OutputTruncated = truncated,
                Limits = prepared.Limits
            };
        }

        if (timedOut)
        {
            return new SnippetRunResult
            {
                Status = SnippetRunStatus.TimedOut,
                Language = runtime.Language,
                Runtime = runtime.Path,
                StandardOutput = output.Text,
                StandardError = error.Text,
                Message = "The run was stopped because it passed the time limit.",
                OutputTruncated = truncated,
                Limits = prepared.Limits
            };
        }

        int? code = process.HasExited ? process.ExitCode : null;
        return new SnippetRunResult
        {
            Status = SnippetRunStatus.Failed,
            Language = runtime.Language,
            Runtime = runtime.Path,
            StandardOutput = output.Text,
            StandardError = error.Text,
            ExitCode = code,
            Message = code is int exit ? "The process exited with code " + exit + "." : "The process stopped.",
            OutputTruncated = truncated,
            Limits = prepared.Limits
        };
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task<(string Text, bool Truncated)> ReadCapAsync(StreamReader reader, int cap, CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        var builder = new StringBuilder();
        var truncated = false;
        while (builder.Length < cap)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, cap - builder.Length)), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (read == 0) return (builder.ToString(), truncated);
            builder.Append(buffer, 0, read);
        }

        if (builder.Length >= cap)
        {
            truncated = true;
            try
            {
                while (await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0) { }
            }
            catch (OperationCanceledException) { }
        }

        return (builder.ToString(), truncated);
    }

    private static string? Validate(SnippetRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code)) return "Add code to run.";
        if (request.Code.Contains('\0')) return "The snippet contains a NUL character.";
        if (request.Code.Length > request.MaxCodeChars) return "The snippet is over the size limit.";
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromSeconds(60))
            return "Timeout must be from 1 second to 60 seconds.";
        if (request.CpuSeconds is < 1 or > 30) return "CpuSeconds must be from 1 to 30.";
        if (request.MaxOutputChars is < 256 or > 1_000_000) return "MaxOutputChars is out of range.";
        if (FindRuntime(request.Language).Language is not ("python" or "javascript" or "csharp"))
            return UnsupportedLanguage;
        return null;
    }

    private static string WriteSource(string directory, string language, string code)
    {
        switch (language)
        {
            case "python":
                var py = Path.Combine(directory, "main.py");
                File.WriteAllText(py, code);
                return py;
            case "javascript":
                var js = Path.Combine(directory, "main.js");
                File.WriteAllText(js, code);
                return js;
            default:
                var project = Path.Combine(directory, "snippet.csproj");
                var tfm = "net" + Environment.Version.Major + ".0";
                File.WriteAllText(project,
                    "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                    "  <PropertyGroup>\n" +
                    "    <OutputType>Exe</OutputType>\n" +
                    "    <TargetFramework>" + tfm + "</TargetFramework>\n" +
                    "    <ImplicitUsings>enable</ImplicitUsings>\n" +
                    "    <Nullable>disable</Nullable>\n" +
                    "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>\n" +
                    "  </PropertyGroup>\n" +
                    "  <ItemGroup>\n" +
                    "    <Compile Include=\"Program.cs\" />\n" +
                    "  </ItemGroup>\n" +
                    "</Project>\n");
                var cs = Path.Combine(directory, "Program.cs");
                File.WriteAllText(cs, code);
                return project;
        }
    }

    private static IEnumerable<string> RuntimeArguments(string language, string source, string directory)
    {
        if (language == "csharp")
        {
            yield return "run";
            yield return "--project";
            yield return source;
            yield return "--nologo";
            yield return "-v";
            yield return "q";
            yield break;
        }

        yield return source;
        _ = directory;
    }

    private static Dictionary<string, string?> ScrubbedEnvironment(string directory, SnippetRunRequest request)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in new[] { "PATH", "LANG", "LC_ALL", "LC_CTYPE", "SYSTEMROOT", "WINDIR", "PATHEXT", "DOTNET_ROOT", "DOTNET_HOST_PATH", "DOTNET_MULTILEVEL_LOOKUP" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) env[name] = value;
        }

        env["HOME"] = directory;
        env["TMPDIR"] = directory;
        env["TMP"] = directory;
        env["TEMP"] = directory;
        env["DOTNET_CLI_HOME"] = directory;
        env["DOTNET_NOLOGO"] = "1";
        env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        env["PYTHONDONTWRITEBYTECODE"] = "1";
        env["NO_COLOR"] = "1";
        env["FLEX_CPU"] = request.CpuSeconds.ToString();
        env["FLEX_FSIZE"] = request.MaxFileKilobytes.ToString();
        env["FLEX_NPROC"] = request.MaxProcesses.ToString();
        env["FLEX_NOFILE"] = request.MaxOpenFiles.ToString();
        return env;
    }

    private const string LimitScript = """
        #!/bin/sh
        ulimit -t "${FLEX_CPU:-10}" || true
        ulimit -f "${FLEX_FSIZE:-2048}" || true
        ulimit -n "${FLEX_NOFILE:-64}" || true
        ulimit -u "${FLEX_NPROC:-32}" || true
        exec "$@"
        """;

    internal static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return "";
        var text = language.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");
        return text switch
        {
            "py" or "python" or "python3" => "python",
            "js" or "javascript" or "node" => "javascript",
            "cs" or "csharp" or "c#" => "csharp",
            _ => text
        };
    }

    private static string? Which(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
            if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe")) return candidate + ".exe";
        }

        return null;
    }
}

/// <summary>Resolved executable for a snippet language.</summary>
public sealed record SnippetRuntime(string Language, string? Path, string MissingMessage)
{
    public bool IsAvailable => !string.IsNullOrEmpty(Path);
}
