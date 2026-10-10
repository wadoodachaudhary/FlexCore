namespace Fx.ControlKit.Preview;

internal static class PreviewMessages
{
    public static string ForMissingTools(IReadOnlyList<string> missing, string operatingSystem)
    {
        if (missing.Count == 0) return "";
        var list = string.Join(", ", missing);
        const string install = "Debian/Ubuntu: sudo apt-get install xvfb xdotool ffmpeg x11-apps";
        if (operatingSystem.Contains("macOS", StringComparison.OrdinalIgnoreCase))
            return $"Live preview needs a virtual display ({list} not found). On macOS, run the app in a Linux container with those packages. {install}. XQuartz does not include Xvfb.";
        if (operatingSystem.Contains("Windows", StringComparison.OrdinalIgnoreCase))
            return $"Live preview needs a Linux virtual display ({list} not found). Use a Linux container. {install}. Windows App Service cannot host this preview.";
        return $"Live preview needs a virtual display. Missing: {list}. {install}. Azure App Service needs a custom Linux container that includes these packages.";
    }

    public static string OperatingSystemName()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsMacOS()) return "macOS";
        return "Linux";
    }
}

internal static class PreviewTools
{
    public static ProgramPreviewCapability Probe(ProgramPreviewOptions options)
    {
        var xvfb = Resolve(options.XvfbPath, "Xvfb");
        var xdotool = Resolve(options.XdotoolPath, "xdotool");
        var ffmpeg = Resolve(options.FfmpegPath, "ffmpeg");
        var xwd = Resolve(options.XwdPath, "xwd");
        var xauth = Resolve(options.XauthPath, "xauth");
        var mcookie = Resolve(options.McookiePath, "mcookie");
        var missing = new List<string>();
        if (xvfb is null) missing.Add("xvfb");
        if (xdotool is null) missing.Add("xdotool");
        if (ffmpeg is null && xwd is null) missing.Add("ffmpeg or x11-apps (xwd)");
        var canRunGui = missing.Count == 0;
        return new ProgramPreviewCapability
        {
            CanRunGui = canRunGui,
            CanRunConsole = true,
            HasXvfb = xvfb is not null,
            HasXdotool = xdotool is not null,
            HasFfmpeg = ffmpeg is not null,
            HasXwd = xwd is not null,
            XvfbPath = xvfb,
            XdotoolPath = xdotool,
            FfmpegPath = ffmpeg,
            XwdPath = xwd,
            XauthPath = xauth,
            McookiePath = mcookie,
            Missing = missing,
            Message = canRunGui ? "" : PreviewMessages.ForMissingTools(missing, PreviewMessages.OperatingSystemName())
        };
    }

    public static string? Resolve(string? configured, string name)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        return FindOnPath(name);
    }

    public static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(directory, name);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
