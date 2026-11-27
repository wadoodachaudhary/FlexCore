using System.Diagnostics;

namespace Fx.ControlKit.Preview;

/// <summary>
/// The preview process gets a private HOME, so Python would miss packages installed for the
/// real user. Remember that site once, while HOME is still the operator's, and put it on PYTHONPATH.
/// </summary>
internal static class PreviewPythonSite
{
    private static readonly string? Site = Probe();

    public static void Apply(ProcessStartInfo start)
    {
        if (string.IsNullOrWhiteSpace(Site)) return;
        if (start.Environment.TryGetValue("PYTHONPATH", out var existing) && !string.IsNullOrWhiteSpace(existing))
            return;
        start.Environment["PYTHONPATH"] = Site;
    }

    private static string? Probe()
    {
        try
        {
            var start = new ProcessStartInfo("python3")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("import site; print(site.getusersitepackages())");
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(5000) || process.ExitCode != 0 || output.Length == 0 || !Directory.Exists(output))
                return null;
            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
