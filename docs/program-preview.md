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
app.MapGet(ProgramPreviewHttp.DefaultRoutePrefix + "/{sessionId}/frame",
    async (string sessionId, string? token, IProgramPreviewHost host, HttpContext http) =>
    {
        var frame = host.ReadFrame(sessionId, token);
        http.Response.StatusCode = frame.StatusCode;
        http.Response.ContentType = frame.ContentType;
        http.Response.Headers.CacheControl = "no-store";
        await http.Response.Body.WriteAsync(frame.Body);
    });
```

Leave `FrameRoutePrefix` null to embed frames in the circuit instead. Set the prefix only when this route is mapped.

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

`Kind` is `Auto`, `Gui`, or `Console`. `Auto` uses a virtual display when the server has one, and fails with the prerequisite message when it does not. It does not silently turn a GUI program into a console pipe. Set `Console` for a terminal program. Console preview writes keystrokes to standard input and shows standard output. It is line-oriented: there is no terminal emulator, so full-screen text UIs and cooked backspace are not reproduced.

`ProgramPreviewPair` starts, restarts, and stops both programs together. Each pane has its own status and error output.

Pass `OwnerKey` (a user id, for example) so `MaxSessionsPerOwner` applies. When it is omitted, the pair uses one generated owner for both windows.

## Converter shell

`ConverterShell` keeps the single-file and file-browser layouts. Set `EnableRunPreview` to add a **Run / Preview** mode, and pass the launches:

```razor
<ConverterShell EnableRunPreview="true"
                SourcePreview="_source"
                TargetPreview="_target"
                SourcePreviewLabel="Source"
                TargetPreviewLabel="Target"
                AutoStartPreview="false"
                Convert="@convert" />
```

Setting `Mode` to `ConverterShellMode.RunPreview` opens that layout directly. The editors stay in memory while the preview is visible. Convert and Clear still operate on that text.

## What the server does

A GUI session:

1. Allocates a private X display and a private `HOME` / temp directory.
2. Starts `Xvfb` on that display, with an `Xauthority` cookie when `xauth` and `mcookie` exist. Otherwise it starts with `-ac` and listens only on a local socket (`-nolisten tcp`).
3. Starts the program with `DISPLAY` pointed at that display. `WAYLAND_DISPLAY` is removed. `GDK_BACKEND=x11` and `QT_QPA_PLATFORM=xcb` are set when the launch did not set them.
4. Captures the framebuffer with `ffmpeg` (`x11grab`) or, if ffmpeg is missing, with `xwd`.
5. Relays pointer and key events with `xdotool`.
6. Stops the process tree on Stop, on dispose, on idle timeout, and on the maximum lifetime.

The program's standard error is shown as error output. Standard output is kept on the session and shown for console previews.

Calls accept at most 80 input events per second per session. Frame size defaults to 800×600. Change it with `DisplayWidth` and `DisplayHeight` (160–1920 by 120–1200). The launched process sees `FLEXCORE_PREVIEW_WIDTH` and `FLEXCORE_PREVIEW_HEIGHT`.

Sessions live in the server process. Scale-out needs affinity so the frame request reaches the instance that owns the session. One instance is the simple deployment.

## API

| Type | Role |
| --- | --- |
| `AddFlexCoreProgramPreview` | Registers `IProgramPreviewHost` and `ProgramPreviewOptions`. |
| `IProgramPreviewHost` | Starts, stops, restarts, releases, reads frames, copies pixels, and sends input. |
| `ProgramLaunch` | `FileName`, `Arguments`, `WorkingDirectory`, `Environment`, `Kind`, `Label`. |
| `ProgramPreviewKind` | `Auto`, `Gui`, `Console`. |
| `ProgramPreviewOptions` | Limits, display size, tool paths, `FrameRoutePrefix`. |
| `ProgramPreviewCapability` | Which tools were found, and `Message` when GUI is unavailable. |
| `ProgramPreviewHandle` | `SessionId`, `Token`, and the first `Snapshot`. |
| `ProgramPreviewSnapshot` | Status, status text, stdout, stderr, exit code, process id, display number, frame version. |
| `ProgramPreviewStatus` | `Missing`, `Unavailable`, `Starting`, `Running`, `Exited`, `Failed`, `Stopped`. |
| `ProgramPreviewInput` | Mouse move/down/up, wheel, key, or text. Coordinates are framebuffer pixels. |
| `ProgramPreviewFrame` | Status code, content type, and body for the image route. |
| `ProgramPreviewHttp.DefaultRoutePrefix` | `/_flexcore/program-preview`. |
| `ProgramPreviewWindow` | One preview. Parameters: `Launch`, `Label`, `AutoStart`, `ShowToolbar`, `ShowOutput`, `OwnerKey`, `StartText`, `RestartText`, `StopText`. |
| `ProgramPreviewPair` | Two previews. Parameters: `SourceLaunch`, `TargetLaunch`, `SourceLabel`, `TargetLabel`, `OwnerKey`, `AutoStart`, `StartText`, `RestartText`, `StopText`, `PairLabel`. |
| `ConverterShell.EnableRunPreview` | Adds the Run / Preview mode. |
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

`tests/FlexCore.PreviewHarness` is a Blazor Server page that hosts `ConverterShell` in Run / Preview with two small Tk windows. It is a facility check, not a product sample.

```sh
dotnet run --project tests/FlexCore.PreviewHarness
```

Open the printed URL. Start is automatic. Click a field, type, and click Sign in. Those events go to the Python process on the server.

The regression runner also drives a real click and keystroke without a browser:

```sh
dotnet run --project tests/FlexCore.RegressionTests/FlexCore.RegressionTests.csproj
```

That GUI check expects `xvfb`, `xdotool`, `ffmpeg` or `xwd`, and `python3-tk` on the machine. The rest of the suite does not start a web server.
