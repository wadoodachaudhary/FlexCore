namespace Fx.ControlKit.Preview;

/// <summary>How <see cref="IProgramPreviewHost"/> runs a <see cref="ProgramLaunch"/>.</summary>
public enum ProgramPreviewKind
{
    /// <summary>
    /// Use a virtual display when this server has one. Startup fails with
    /// <see cref="ProgramPreviewCapability.Message"/> when it does not.
    /// Use <see cref="Console"/> for a terminal program.
    /// </summary>
    Auto,

    /// <summary>Virtual display, framebuffer capture, and pointer/key relay.</summary>
    Gui,

    /// <summary>Standard-input and standard-output relay, without a virtual display.</summary>
    Console,

    /// <summary>
    /// A web app. With <see cref="ProgramLaunch.FileName"/>, the host starts that process,
    /// waits until its URL responds, and shows it in the preview. With only
    /// <see cref="ProgramLaunch.Url"/>, the host shows that address and does not start a process.
    /// </summary>
    Web
}

/// <summary>Lifecycle of one preview session.</summary>
public enum ProgramPreviewStatus
{
    /// <summary>No session matches the id and token.</summary>
    Missing,

    /// <summary>The server cannot run this kind of program.</summary>
    Unavailable,

    /// <summary>The process is being launched.</summary>
    Starting,

    /// <summary>The process is running and input is delivered to it.</summary>
    Running,

    /// <summary>The process ended on its own.</summary>
    Exited,

    /// <summary>Startup or validation failed.</summary>
    Failed,

    /// <summary>The host stopped the process.</summary>
    Stopped
}

/// <summary>An input event delivered to the real program.</summary>
public enum ProgramPreviewInputKind
{
    MouseMove,
    MouseDown,
    MouseUp,
    Wheel,
    KeyDown,
    Text
}

/// <summary>
/// A program the host is willing to run. FlexCore does not accept this value from
/// the browser; the Blazor host sets it in its own code.
/// </summary>
public sealed class ProgramLaunch
{
    /// <summary>Executable to start. Omit it for a <see cref="ProgramPreviewKind.Web"/> preview that only displays <see cref="Url"/>.</summary>
    public string? FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
    public ProgramPreviewKind Kind { get; init; } = ProgramPreviewKind.Auto;
    public string? Label { get; init; }

    /// <summary>
    /// Absolute http(s) address to show, or a template containing <c>{port}</c> when this launch starts a server.
    /// When a server is started and this is null, the host probes <c>http://127.0.0.1:{port}/</c>.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>Fixed TCP port for a started web server. When null, the host allocates one.</summary>
    public int? Port { get; init; }

    /// <summary>Throws <see cref="ArgumentException"/> when the launch cannot be started.</summary>
    public void Validate()
    {
        var addressOnly = Kind == ProgramPreviewKind.Web && string.IsNullOrWhiteSpace(FileName);
        if (addressOnly)
        {
            if (!IsHttp(Url, allowPortToken: false))
                throw new ArgumentException("A web address preview needs an absolute http or https Url.", nameof(Url));
        }
        else if (string.IsNullOrWhiteSpace(FileName) || FileName.Contains('\0'))
        {
            throw new ArgumentException("FileName is required.", nameof(FileName));
        }
        if (Url is not null && (Url.Contains('\0') || Url.Length > 2048 || !IsHttp(Url, allowPortToken: true)))
            throw new ArgumentException("Url must be an absolute http or https address, or contain {port}.", nameof(Url));
        if (Port is < 1 or > 65535)
            throw new ArgumentException("Port must be from 1 to 65535.", nameof(Port));
        if (Arguments is null)
            throw new ArgumentException("Arguments cannot be null.", nameof(Arguments));
        if (Arguments.Count > 100)
            throw new ArgumentException("Use at most 100 arguments.", nameof(Arguments));
        foreach (var argument in Arguments)
        {
            if (argument is null || argument.Contains('\0') || argument.Length > 32 * 1024)
                throw new ArgumentException("An argument is null, too long, or contains NUL.", nameof(Arguments));
        }
        if (WorkingDirectory is not null && (WorkingDirectory.Contains('\0') || !Directory.Exists(WorkingDirectory)))
            throw new ArgumentException("WorkingDirectory must exist.", nameof(WorkingDirectory));
        if (Environment is { Count: > 100 })
            throw new ArgumentException("Use at most 100 environment variables.", nameof(Environment));
        if (Environment is null) return;
        foreach (var pair in Environment)
        {
            if (string.IsNullOrEmpty(pair.Key) || pair.Key.Contains('=') || pair.Key.Contains('\0'))
                throw new ArgumentException("An environment name is invalid.", nameof(Environment));
            if (pair.Value is { Length: > 32 * 1024 } || (pair.Value?.Contains('\0') ?? false))
                throw new ArgumentException("An environment value is invalid.", nameof(Environment));
        }
    }

    private static bool IsHttp(string? value, bool allowPortToken)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (allowPortToken && value.Contains("{port}", StringComparison.Ordinal))
            return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && string.IsNullOrEmpty(uri.UserInfo);
    }
}

/// <summary>Limits and tool paths for <see cref="IProgramPreviewHost"/>.</summary>
public sealed class ProgramPreviewOptions
{
    public int MaxConcurrentSessions { get; set; } = 8;
    public int MaxSessionsPerOwner { get; set; } = 4;
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan FrameInterval { get; set; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan StoppedRetention { get; set; } = TimeSpan.FromMinutes(10);
    public int DisplayWidth { get; set; } = 800;
    public int DisplayHeight { get; set; } = 600;
    public int FirstDisplayNumber { get; set; } = 80;
    public int DisplayCount { get; set; } = 40;
    public int FirstPreviewPort { get; set; } = 41000;
    public int PreviewPortCount { get; set; } = 200;

    /// <summary>
    /// Root-relative frame path, for example <see cref="ProgramPreviewHttp.DefaultRoutePrefix"/>.
    /// When null, <see cref="ProgramPreviewWindow"/> embeds frames in the Blazor circuit.
    /// The host still has to map <c>{prefix}/{sessionId}/frame</c>.
    /// </summary>
    public string? FrameRoutePrefix { get; set; }

    public string? XvfbPath { get; set; }
    public string? XdotoolPath { get; set; }
    public string? FfmpegPath { get; set; }
    public string? XwdPath { get; set; }
    public string? XauthPath { get; set; }
    public string? McookiePath { get; set; }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> when a limit is outside its documented range.</summary>
    public void Validate()
    {
        Range(MaxConcurrentSessions, 1, 64, nameof(MaxConcurrentSessions));
        Range(MaxSessionsPerOwner, 1, 64, nameof(MaxSessionsPerOwner));
        Span(IdleTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromHours(24), nameof(IdleTimeout));
        Span(MaxLifetime, TimeSpan.FromSeconds(1), TimeSpan.FromHours(24), nameof(MaxLifetime));
        Span(FrameInterval, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5), nameof(FrameInterval));
        Span(StartTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2), nameof(StartTimeout));
        Span(SweepInterval, TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(1), nameof(SweepInterval));
        Span(StoppedRetention, TimeSpan.FromSeconds(1), TimeSpan.FromHours(24), nameof(StoppedRetention));
        Range(DisplayWidth, 160, 1920, nameof(DisplayWidth));
        Range(DisplayHeight, 120, 1200, nameof(DisplayHeight));
        Range(FirstDisplayNumber, 1, 500, nameof(FirstDisplayNumber));
        Range(DisplayCount, 1, 200, nameof(DisplayCount));
        Range(FirstPreviewPort, 1024, 65000, nameof(FirstPreviewPort));
        Range(PreviewPortCount, 1, 500, nameof(PreviewPortCount));
        if (FirstPreviewPort + PreviewPortCount - 1 > 65535)
            throw new ArgumentOutOfRangeException(nameof(PreviewPortCount), "The preview port range must stay within 65535.");
        if (FrameRoutePrefix is not null && (!FrameRoutePrefix.StartsWith('/') || FrameRoutePrefix.Contains('?')))
            throw new ArgumentException("FrameRoutePrefix must be a root-relative path.", nameof(FrameRoutePrefix));
    }

    private static void Range(int value, int min, int max, string name)
    {
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(name, $"Use a value from {min} to {max}.");
    }

    private static void Span(TimeSpan value, TimeSpan min, TimeSpan max, string name)
    {
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(name, $"Use a value from {min} to {max}.");
    }
}

/// <summary>What this process can launch. <see cref="Message"/> is safe to show when <see cref="CanRunGui"/> is false.</summary>
public sealed record ProgramPreviewCapability
{
    public bool CanRunGui { get; init; }
    public bool CanRunConsole { get; init; } = true;
    public bool HasXvfb { get; init; }
    public bool HasXdotool { get; init; }
    public bool HasFfmpeg { get; init; }
    public bool HasXwd { get; init; }
    public string? XvfbPath { get; init; }
    public string? XdotoolPath { get; init; }
    public string? FfmpegPath { get; init; }
    public string? XwdPath { get; init; }
    public string? XauthPath { get; init; }
    public string? McookiePath { get; init; }
    public IReadOnlyList<string> Missing { get; init; } = [];
    public string Message { get; init; } = "";
}

/// <summary>Point-in-time view of one session. Strings are already truncated.</summary>
public sealed record ProgramPreviewSnapshot
{
    public ProgramPreviewStatus Status { get; init; }
    public ProgramPreviewKind Kind { get; init; }
    public string StatusText { get; init; } = "";
    public string ErrorText { get; init; } = "";
    public string OutputText { get; init; } = "";
    public int? ExitCode { get; init; }
    public int? ProcessId { get; init; }
    public int? DisplayNumber { get; init; }
    public int? Port { get; init; }

    /// <summary>
    /// Address the browser should open. For a server this host launched, this is the preview
    /// app route when <see cref="ProgramPreviewOptions.FrameRoutePrefix"/> is set, and the
    /// loopback address otherwise. For an address-only web preview, this is <see cref="ProgramLaunch.Url"/>.
    /// </summary>
    public string? Url { get; init; }

    public long FrameVersion { get; init; }
    public int FrameWidth { get; init; }
    public int FrameHeight { get; init; }
    public long Revision { get; init; }

    public static ProgramPreviewSnapshot Missing { get; } = new()
    {
        Status = ProgramPreviewStatus.Missing,
        StatusText = "The preview session is not running."
    };
}

/// <summary>Identifies a running or retained session. <see cref="Token"/> authorizes frame and input calls.</summary>
public sealed record ProgramPreviewHandle
{
    public string? SessionId { get; init; }
    public string? Token { get; init; }
    public required ProgramPreviewSnapshot Snapshot { get; init; }
    public bool HasSession => !string.IsNullOrEmpty(SessionId);
}

/// <summary>Pointer or keyboard input. Coordinates are framebuffer pixels.</summary>
public sealed record ProgramPreviewInput
{
    public ProgramPreviewInputKind Kind { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Button { get; init; }
    public int WheelDelta { get; init; }
    public string? Key { get; init; }
    public string? Text { get; init; }
    public bool Shift { get; init; }
    public bool Ctrl { get; init; }
    public bool Alt { get; init; }
    public bool Meta { get; init; }
}

/// <summary>Raw RGB24 capture of the virtual display.</summary>
public sealed record ProgramPreviewImage(int Width, int Height, byte[] Rgb, long Version);

/// <summary>Bytes a host returns from the frame route.</summary>
public sealed record ProgramPreviewFrame(int StatusCode, string ContentType, byte[] Body);

/// <summary>
/// Launches programs, captures their interface, and relays input.
/// Register it with <c>AddFlexCoreProgramPreview</c>. Dispose the host to stop every session.
/// </summary>
public interface IProgramPreviewHost
{
    ProgramPreviewOptions Options { get; }
    ProgramPreviewCapability Capability { get; }

    Task<ProgramPreviewHandle> StartAsync(ProgramLaunch launch, string? ownerKey = null, CancellationToken cancellationToken = default);
    Task<ProgramPreviewSnapshot> RestartAsync(string sessionId, string token, CancellationToken cancellationToken = default);
    Task<ProgramPreviewSnapshot> StopAsync(string sessionId, string token, string? reason = null, CancellationToken cancellationToken = default);
    void Release(string sessionId, string token);
    ProgramPreviewSnapshot GetSnapshot(string sessionId, string token);
    ProgramPreviewFrame ReadFrame(string sessionId, string? token);
    ProgramPreviewImage? CopyFrame(string sessionId, string token);
    Task<bool> SendInputAsync(string sessionId, string token, ProgramPreviewInput input, CancellationToken cancellationToken = default);

    /// <summary>Resets the idle timer. Frame reads, input, and proxied web requests already do this.</summary>
    void Touch(string sessionId, string? token);

    /// <summary>
    /// Loopback origin of a running web server this host started.
    /// Status 404 means the session is missing or is not a proxied server. Status 401 means the token was rejected.
    /// </summary>
    ProgramPreviewWebTarget ResolveWeb(string sessionId, string? token);
}

/// <summary>Result of <see cref="IProgramPreviewHost.ResolveWeb"/>.</summary>
public sealed record ProgramPreviewWebTarget(int StatusCode, Uri? Upstream);

/// <summary>Frame drawn around a <see cref="ProgramPreviewWindow"/>.</summary>
public enum ProgramPreviewChrome
{
    /// <summary>A quiet border, title bar, and status. This is the default.</summary>
    Default,

    /// <summary>Classic Windows / Visual Basic 6 caption and raised border.</summary>
    Windows,

    /// <summary>Qt Fusion / KDE-style frame.</summary>
    Fusion,

    /// <summary>A modern browser window, suited to a web target.</summary>
    Browser,

    /// <summary>No frame and no title bar. Start, restart, and stop stay available.</summary>
    None
}

/// <summary>One pane in <see cref="ProgramPreviewGroup"/>.</summary>
public sealed class ProgramPreviewPane
{
    public string Label { get; init; } = "Program";
    public ProgramLaunch? Launch { get; init; }

    /// <summary>Frame for this pane. Null uses <see cref="ProgramPreviewGroup.Chrome"/>.</summary>
    public ProgramPreviewChrome? Chrome { get; init; }

    /// <summary>
    /// Optional title-bar icon. An <c>http</c>, <c>https</c>, <c>data:image</c>, or root-relative
    /// value is shown as an image. Any other text is shown as the icon itself.
    /// </summary>
    public string? Icon { get; init; }
}

/// <summary>Frame-route helpers that do not reference ASP.NET Core types.</summary>
public static class ProgramPreviewHttp
{
    public const string DefaultRoutePrefix = "/_flexcore/program-preview";

    public const string CookieName = "FlexCore.Preview";

    public static string FramePath(string prefix, string sessionId, string token, long version)
        => $"{prefix.TrimEnd('/')}/{sessionId}/frame?token={Uri.EscapeDataString(token)}&v={version}";

    public static string AppPrefix(string prefix, string sessionId)
        => $"{prefix.TrimEnd('/')}/{sessionId}/app";

    public static string AppPath(string prefix, string sessionId, string token)
        => $"{AppPrefix(prefix, sessionId)}/?token={Uri.EscapeDataString(token)}";

    public static string? TokenFrom(string? queryToken, string? cookieHeader)
    {
        if (!string.IsNullOrEmpty(queryToken)) return queryToken;
        if (string.IsNullOrEmpty(cookieHeader)) return null;
        foreach (var part in cookieHeader.Split(';'))
        {
            var piece = part.Trim();
            const string name = CookieName + "=";
            if (piece.StartsWith(name, StringComparison.Ordinal))
                return Uri.UnescapeDataString(piece[name.Length..]);
        }
        return null;
    }
}
