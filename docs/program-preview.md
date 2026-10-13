# Live program preview

FlexCore can run a program on the server and show it in the browser. Pointer and keyboard events in the preview are delivered to that process. The picture is a capture of its real window, not a screenshot taken in advance and not a mock of the UI.

The host decides which command is launched. The browser only receives a session id and an unguessable token.

## Register

```csharp
using Fx.ControlKit.Preview;

builder.Services.AddFlexCoreProgramPreview(options =>
{
    options.FrameRoutePrefix = ProgramPreviewHttp.DefaultRoutePrefix;
    options.MaxConcurrentSessions = 8;
    options.MaxSessionsPerOwner = 4;
    options.IdleTimeout = TimeSpan.FromMinutes(10);
    options.MaxLifetime = TimeSpan.FromMinutes(30);
    options.DisplayWidth = 800;
    options.DisplayHeight = 600;
});
```

Frames can travel inside the Blazor circuit. For a side-by-side preview, map an image route so the browser loads the PNG directly. `AddFlexCoreProgramPreview` does not reference ASP.NET Core endpoint types, so the host maps the route:

```csharp
app.UseWebSockets();
app.MapGet(ProgramPreviewHttp.DefaultRoutePrefix + "/{sessionId}/frame",
    async (string sessionId, string? token, IProgramPreviewHost host, HttpContext http) =>
    {
        var frame = host.ReadFrame(sessionId, token);
        http.Response.StatusCode = frame.StatusCode;
        http.Response.ContentType = frame.ContentType;
        http.Response.Headers.CacheControl = "no-store";
        await http.Response.Body.WriteAsync(frame.Body);
    });
app.Map(ProgramPreviewHttp.DefaultRoutePrefix + "/{sessionId}/app/{**path}", async (HttpContext http, string sessionId, IProgramPreviewHost host) =>
{
    var token = ProgramPreviewHttp.TokenFrom(http.Request.Query["token"], http.Request.Headers.Cookie);
    var prefix = ProgramPreviewHttp.AppPrefix(ProgramPreviewHttp.DefaultRoutePrefix, sessionId);
    var pathAndQuery = UpstreamPath(http.Request);
    if (http.WebSockets.IsWebSocketRequest)
    {
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        await ProgramPreviewProxy.ForwardWebSocketAsync(host, sessionId, token, pathAndQuery, socket, http.RequestAborted);
        return;
    }

    var headers = http.Request.Headers.Select(header =>
        new KeyValuePair<string, IEnumerable<string>>(header.Key, header.Value.Select(value => value ?? "").ToArray()));
    await ProgramPreviewProxy.ForwardHttpAsync(host, sessionId, token, http.Request.Method, pathAndQuery, headers,
        http.Request.Body, prefix, http.Response.Body, (status, responseHeaders) =>
        {
            http.Response.StatusCode = status;
            foreach (var header in responseHeaders)
                http.Response.Headers.Append(header.Key, header.Value.ToArray());
        }, http.RequestAborted);
});

static string UpstreamPath(HttpRequest request)
{
    var query = QueryString.Empty;
    foreach (var pair in request.Query)
    {
        if (pair.Key.Equals("token", StringComparison.OrdinalIgnoreCase)) continue;
        foreach (var value in pair.Value)
            query = query.Add(pair.Key, value ?? "");
    }
    return (request.Path.Value ?? "/") + query.Value;
}
```

Leave `FrameRoutePrefix` null to embed frames in the circuit instead. Set the prefix when the frame route is mapped. A launched web app also needs that prefix and the app route: the browser cannot open the server's loopback port, so the host proxies `http://127.0.0.1:<port>` through `{prefix}/{sessionId}/app`.

The host is a singleton. Disposing the service provider stops every program. Blazor Server already does that on shutdown.

## Show one program, or source and target

```razor
@using Fx.ControlKit.Preview

<ProgramPreviewWindow Launch="_program" Label="Sample" AutoStart="true" />

<ProgramPreviewPair SourceLaunch="_source"
                    TargetLaunch="_target"
                    SourceLabel="Source"
                    TargetLabel="Target"
                    OwnerKey="@userId"
                    AutoStart="true" />

<ProgramPreviewGroup OwnerKey="@userId" AutoStart="true" Items="_panes" />
```

`ProgramLaunch` is the command. It is not taken from the browser.

```csharp
var source = new ProgramLaunch
{
    FileName = "python3",
    Arguments = ["/opt/apps/source_form.py"],
    WorkingDirectory = "/opt/apps",
    Kind = ProgramPreviewKind.Gui
};
```

`Kind` is `Auto`, `Gui`, `Console`, or `Web`. `Auto` uses a virtual display when the server has one, and fails with the prerequisite message when it does not. It does not silently turn a GUI program into a console pipe. Set `Console` for a terminal program. Console preview writes keystrokes to standard input and shows standard output. It is line-oriented: there is no terminal emulator, so full-screen text UIs and cooked backspace are not reproduced.

`ProgramPreviewPair` starts, restarts, and stops both programs together. Each pane has its own kind, status, and error output, so one side can be a GUI program and the other a web app. `ProgramPreviewGroup` does the same for any number of panes and one toolbar. A three-pane group uses three session slots.

Pass `OwnerKey` (a user id, for example) so `MaxSessionsPerOwner` applies. When it is omitted, the pair or group uses one generated owner for every window.

## Window chrome

Each pane has a frame, a title bar (label, running status, and an optional icon), and space from the next pane. `Chrome` selects the frame. The default is a quiet card. `Windows` is a classic Windows / Visual Basic 6 caption. `Fusion` is a Qt / KDE frame. `Browser` is a modern browser window, which suits a web target. `None` removes the frame and the title bar. Start, restart, and stop stay on the toolbar in every theme.

Set it on a window, on each side of a pair, or on each `ProgramPreviewPane`. A pane that leaves `Chrome` null uses the group's `Chrome`.

```razor
<ProgramPreviewWindow Launch="_program" Label="Sample" Chrome="ProgramPreviewChrome.Fusion" Icon="Qt" />

<ProgramPreviewPair SourceLaunch="_source"
                    TargetLaunch="_target"
                    SourceChrome="ProgramPreviewChrome.Windows"
                    TargetChrome="ProgramPreviewChrome.Browser"
                    SourceIcon="W" />
```

```csharp
var panes = new ProgramPreviewPane[]
{
    new() { Label = "Source", Chrome = ProgramPreviewChrome.Windows, Icon = "W", Launch = source },
    new() { Label = "Blazor", Chrome = ProgramPreviewChrome.Browser, Launch = blazor }
};
```

An `http`, `https`, `data:image`, or root-relative `Icon` is an image. Any other text is drawn as the icon. `javascript:` is not loaded as an image.

## Web apps

`ProgramPreviewKind.Web` is either a process the host starts, or an address that is already hosted.

```csharp
var blazor = new ProgramLaunch
{
    FileName = "dotnet",
    Arguments = ["MyApp.dll", "--urls", "http://127.0.0.1:{port}"],
    Kind = ProgramPreviewKind.Web
};

var hosted = new ProgramLaunch
{
    Kind = ProgramPreviewKind.Web,
    Url = "https://preview.example/"
};
```

A started server gets a port from `FirstPreviewPort` (41000) through `PreviewPortCount` (200), or `ProgramLaunch.Port` when that port must be fixed. `{port}`, `{url}`, and `{pathBase}` in arguments, environment values, and `Url` are replaced. The process also receives `FLEXCORE_PREVIEW_PORT`, `FLEXCORE_PREVIEW_URL`, and `FLEXCORE_PREVIEW_PATHBASE`. Unless the arguments contain `--urls` or the launch environment sets `ASPNETCORE_URLS`, the host replaces any inherited `ASPNETCORE_URLS` with the loopback origin and removes inherited `ASPNETCORE_HTTP_PORTS` and `ASPNETCORE_HTTPS_PORTS`. When `FrameRoutePrefix` is set, it sets `ASPNETCORE_PATHBASE` to `{prefix}/{sessionId}/app` so a Blazor app generates links under the proxy, replacing a path base inherited from the host.

The host waits until that URL returns any HTTP response, then shows it. The same start timeout and idle and lifetime limits apply. Stopping, releasing, or the process exiting frees the port. The preview window polls the session, which keeps a running web preview from going idle while the page is open. An address-only launch probes `Url` and does not start a process; `Url` must be absolute `http` or `https` with no user info. `javascript:` and other schemes are rejected.

The window shows the page in an iframe and an **Open** link (`target="_blank"`). For a server this host launched, both use `{prefix}/{sessionId}/app/?token=...` when the frame prefix is set. The app route forwards HTTP and WebSockets to loopback, strips `X-Frame-Options` and `Content-Security-Policy`, rewrites root-relative links and redirects onto the prefix, and sets an `HttpOnly` `FlexCore.Preview` cookie so later asset requests stay authorized. The app's own cookies are forwarded. An address that is already public is iframed directly and is not proxied; a site that refuses framing still opens from the **Open** link.

Closing the preview page releases the session and stops a server this host started. The separate tab uses that same session.

## Converter shell

`ConverterShell` keeps the single-file and file-browser layouts. Set `EnableRunPreview` to add a **Run / Preview** mode, and pass the launches:

```razor
<ConverterShell EnableRunPreview="true"
                OwnerKey="@userId"
                SourcePreview="_source"
                TargetPreview="_target"
                SourcePreviewLabel="Source"
                TargetPreviewLabel="Target"
                AutoStartPreview="false"
                Convert="@convert" />
```

`OwnerKey` is passed through to the pair, so Run / Preview counts against the signed-in user's `MaxSessionsPerOwner` limit.

Setting `Mode` to `ConverterShellMode.RunPreview` opens that layout directly. The editors stay in memory while the preview is visible. Convert and Clear still operate on that text.

## What the server does

A GUI session:

1. Allocates a private X display and a private `HOME` / temp directory.
2. Creates `/tmp/.X11-unix` (mode 1777) if it is missing, then starts `Xvfb` on that display. An `Xauthority` cookie is used when `xauth` and `mcookie` exist. Otherwise Xvfb starts with `-ac` and listens only on a local socket (`-nolisten tcp`). Two displays started together used to race inside Xvfb while creating that directory; the host creates it first and retries a start once if Xvfb still reports the race.
3. Starts the program with `DISPLAY` pointed at that display. `WAYLAND_DISPLAY` is removed. `GDK_BACKEND=x11` and `QT_QPA_PLATFORM=xcb` are set when the launch did not set them.
4. Captures the framebuffer with `ffmpeg` (`x11grab`) or, if ffmpeg is missing, with `xwd`.
5. Relays pointer and key events with `xdotool`. Pointer motion keeps only the latest position, so a burst of hover events cannot sit ahead of a click or a key.
6. Stops the process tree on Stop, on dispose, on idle timeout, and on the maximum lifetime.

A web session allocates its port under the same host lock that enforces the session limits, probes the URL until the start timeout, and proxies the browser to loopback only while the session is running and the token matches. `ResolveWeb` returns 401 for a bad token and 404 when the session is missing or is not a server this host started.

The program's standard error is shown as error output. Standard output is kept on the session and shown for console previews.

Calls accept at most 80 input events per second per session. Frame size defaults to 800×600. Change it with `DisplayWidth` and `DisplayHeight` (160–1920 by 120–1200). The launched process sees `FLEXCORE_PREVIEW_WIDTH` and `FLEXCORE_PREVIEW_HEIGHT`.

Sessions live in the server process. Scale-out needs affinity so the frame request reaches the instance that owns the session. One instance is the simple deployment.

## API

| Type | Role |
| --- | --- |
| `AddFlexCoreProgramPreview` | Registers `IProgramPreviewHost` and `ProgramPreviewOptions`. |
| `IProgramPreviewHost` | Starts, stops, restarts, releases, reads frames, copies pixels, sends input, `Touch`, and `ResolveWeb`. |
| `ProgramLaunch` | `FileName`, `Arguments`, `WorkingDirectory`, `Environment`, `Kind`, `Label`, `Url`, `Port`. `FileName` is optional for an address-only web preview. |
| `ProgramPreviewKind` | `Auto`, `Gui`, `Console`, `Web`. |
| `ProgramPreviewOptions` | Limits, display size, tool paths, `FrameRoutePrefix`, `FirstPreviewPort`, `PreviewPortCount`. |
| `ProgramPreviewCapability` | Which tools were found, and `Message` when GUI is unavailable. |
| `ProgramPreviewHandle` | `SessionId`, `Token`, and the first `Snapshot`. |
| `ProgramPreviewSnapshot` | Status, status text, stdout, stderr, exit code, process id, display number, port, browser URL, frame version. |
| `ProgramPreviewStatus` | `Missing`, `Unavailable`, `Starting`, `Running`, `Exited`, `Failed`, `Stopped`. |
| `ProgramPreviewInput` | Mouse move/down/up, wheel, key, or text. Coordinates are framebuffer pixels. |
| `ProgramPreviewFrame` | Status code, content type, and body for the image route. |
| `ProgramPreviewHttp` | `DefaultRoutePrefix` (`/_flexcore/program-preview`), `CookieName`, `AppPrefix`, `AppPath`, `TokenFrom`. |
| `ProgramPreviewProxy` | `ForwardHttpAsync` and `ForwardWebSocketAsync`. The host maps the route. |
| `ProgramPreviewWebTarget` | Status code and loopback upstream from `ResolveWeb`. |
| `ProgramPreviewChrome` | `Default`, `Windows`, `Fusion`, `Browser`, `None`. |
| `ProgramPreviewPane` | `Label`, `Launch`, `Chrome`, `Icon`. Null `Chrome` uses the group theme. |
| `ProgramPreviewWindow` | One preview. Parameters: `Launch`, `Label`, `Chrome`, `Icon`, `AutoStart`, `ShowToolbar`, `ShowOutput`, `OwnerKey`, `StartText`, `RestartText`, `StopText`. A web snapshot adds an **Open** link. |
| `ProgramPreviewPair` | Two previews. Parameters: `SourceLaunch`, `TargetLaunch`, `SourceLabel`, `TargetLabel`, `SourceChrome`, `TargetChrome`, `SourceIcon`, `TargetIcon`, `OwnerKey`, `AutoStart`, `StartText`, `RestartText`, `StopText`, `PairLabel`. |
| `ProgramPreviewGroup` | N previews, one toolbar. Parameters: `Items`, `Chrome`, `OwnerKey`, `AutoStart`, `StartText`, `RestartText`, `StopText`, `GroupLabel`. |
| `ConverterShell.EnableRunPreview` | Adds the Run / Preview mode. |
| `ConverterShell.OwnerKey` | Owner passed to that mode's pair. |
| `ConverterShell.SourcePreview` / `TargetPreview` | Launches used by that mode. |

`ProgramPreviewWindow` also exposes `StartAsync`, `RestartAsync`, `StopAsync`, `PointerAsync`, and `KeyAsync`.

## Server packages

Debian/Ubuntu, including a container image:

```sh
sudo apt-get update
sudo apt-get install -y xvfb xdotool ffmpeg x11-apps x11-xserver-utils util-linux fonts-dejavu-core
```

| Package | Why |
| --- | --- |
| `xvfb` | Virtual display. Required for GUI previews. |
| `xdotool` | Relays clicks and keystrokes. Required for GUI previews. |
| `ffmpeg` | Preferred framebuffer capture (`x11grab`). |
| `x11-apps` | Provides `xwd`, used when ffmpeg is missing. |
| `x11-xserver-utils` | Provides `xauth`. Optional. Without it the display is local-only and uses `-ac`. |
| `util-linux` | Provides `mcookie` for the X cookie. Usually already installed. |
| `fonts-dejavu-core` | A font for toolkits that do not ship one. |

GUI programs also need their own libraries (`python3-tk`, Qt, GTK, and so on). FlexCore does not install those.

If a required tool is missing, the preview stays on the page and shows which packages to install. Console previews still run. `Auto` does not pretend a GUI program ran.

Override tool paths with `XvfbPath`, `XdotoolPath`, `FfmpegPath`, `XwdPath`, `XauthPath`, and `McookiePath` when they are not on `PATH`.

## macOS

Xvfb is a Linux X server. XQuartz does not include it, and FlexCore will not drive the Mac desktop as if it were a per-session display.

Run the app in a Linux container (or any Linux VM) with the packages above. A Homebrew or MacPorts `Xvfb` on `PATH` is used when it really exists; that is uncommon. When the tools are missing, the preview shows the macOS container message instead of a blank pane.

## Azure App Service and containers

Windows App Service cannot host this preview. Linux App Service code deploy cannot `apt-get install` Xvfb either. Use a custom Linux container:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        xvfb xdotool ffmpeg x11-apps x11-xserver-utils util-linux fonts-dejavu-core \
        python3 python3-tk \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY publish/ .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "YourApp.dll"]
```

Add the toolkit packages the migrated programs need. Enable WebSockets (Blazor Server). Turn ARR affinity on, or run a single instance, because a session's frames and input must reach the process that launched it. The page itself still loads when the image is built without these packages; the preview shows the install message.

## Try the harness

`tests/FlexCore.PreviewHarness` is a Blazor Server page that hosts `ConverterShell` in Run / Preview with two small Tk windows, plus a group that mixes a GUI program, a console program, and a local web server. It is a facility check, not a product sample. The page maps the frame route and the web app route.

```sh
dotnet run --project tests/FlexCore.PreviewHarness
```

Open the printed URL. `dotnet run` uses the Development launch profile so library scripts and styles are served from the project. A published build copies those files into the output. The page links `FlexCore.PreviewHarness.styles.css`; a host app does the same with its own `YourApp.styles.css` bundle, which pulls in FlexCore's scoped styles.

Start is automatic. Click a field, type, and click Sign in. Those events go to the Python process on the server.

The regression runner also drives a real click and keystroke without a browser:

```sh
dotnet run --project tests/FlexCore.RegressionTests/FlexCore.RegressionTests.csproj
```

That GUI check expects `xvfb`, `xdotool`, `ffmpeg` or `xwd`, and `python3-tk` on the machine. The rest of the suite does not start a web server.
