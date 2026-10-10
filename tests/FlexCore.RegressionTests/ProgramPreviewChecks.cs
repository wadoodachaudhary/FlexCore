using System.Diagnostics;
using Fx.ControlKit.Conversion;
using Fx.ControlKit.Preview;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

internal static class ProgramPreviewChecks
{
    public static async Task Run(HtmlRenderer renderer, List<IComponent> components, Action<bool, string> check)
    {
        var red = PngRgb.Encode([(byte)255, 0, 0, 0, 0, 255], 2, 1);
        var decoded = PngRgb.Decode(red);
        check(decoded.Width == 2 && decoded.Height == 1 && decoded.Rgb.SequenceEqual(new byte[] { 255, 0, 0, 0, 0, 255 }), "preview PNG round-trips RGB pixels");

        var xwd = SampleXwd();
        var dump = XwdImage.Decode(xwd);
        check(dump.Width == 2 && dump.Height == 2
            && dump.Rgb.Take(3).SequenceEqual(new byte[] { 255, 0, 0 })
            && dump.Rgb.Skip(3).Take(3).SequenceEqual(new byte[] { 0, 255, 0 })
            && dump.Rgb.Skip(6).Take(3).SequenceEqual(new byte[] { 0, 0, 255 }), "XWD decoder reads a 32-bit ZPixmap");

        var mac = PreviewMessages.ForMissingTools(["xvfb"], "macOS");
        var windows = PreviewMessages.ForMissingTools(["xdotool"], "Windows");
        var linux = PreviewMessages.ForMissingTools(["ffmpeg or x11-apps (xwd)"], "Linux");
        check(mac.Contains("container", StringComparison.OrdinalIgnoreCase) && mac.Contains("XQuartz"), "macOS guidance points at a Linux container");
        check(windows.Contains("Windows App Service") && windows.Contains("Linux container"), "Windows guidance refuses the desktop host");
        check(linux.Contains("apt-get") && linux.Contains("Azure"), "Linux guidance names the packages and Azure container");

        var invalid = new ProgramPreviewOptions { MaxConcurrentSessions = 0 };
        try
        {
            invalid.Validate();
            throw new Exception("zero session limit was accepted");
        }
        catch (ArgumentOutOfRangeException)
        {
            check(true, "a zero session limit is rejected");
        }

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var shellRoot = await renderer.RenderComponentAsync<ConverterShell>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["EnableRunPreview"] = true,
                ["SourcePreviewLabel"] = "Left program",
                ["TargetPreviewLabel"] = "Right program",
                ["ShowInstructions"] = false
            }));
            var shell = components.OfType<ConverterShell>().Last();
            check(shellRoot.ToHtmlString().Contains("Run / Preview") && shellRoot.ToHtmlString().Contains("data-mode=\"SingleFile\""), "enabling run preview adds the mode without selecting it");
            await shell.SetModeAsync(ConverterShellMode.RunPreview);
            var previewHtml = shellRoot.ToHtmlString();
            check(shell.EffectiveMode == ConverterShellMode.RunPreview && previewHtml.Contains("data-mode=\"RunPreview\""), "run preview mode replaces the editors");
            check(previewHtml.Contains("Left program") && previewHtml.Contains("Right program") && previewHtml.Contains(">Start<") && previewHtml.Contains(">Stop<"), "the shell hosts the side-by-side preview");
            check(previewHtml.Contains("AddFlexCoreProgramPreview") && !previewHtml.Contains("Your input code here"), "a preview without the host service explains how to register it");
        });

        var activation = new PreviewActivator();
        var uiServices = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime, NoJs>()
            .AddSingleton<IComponentActivator>(activation)
            .AddSingleton<IProgramPreviewHost>(new RecordingPreviewHost())
            .BuildServiceProvider();
        await using var uiRenderer = new HtmlRenderer(uiServices, uiServices.GetRequiredService<ILoggerFactory>());
        await uiRenderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await uiRenderer.RenderComponentAsync<ProgramPreviewWindow>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Label"] = "Recorded",
                ["Launch"] = new ProgramLaunch { FileName = "python3", Arguments = ["-c", "pass"] }
            }));
            var window = activation.All.OfType<ProgramPreviewWindow>().Single();
            check(root.ToHtmlString().Contains("Recorded") && root.ToHtmlString().Contains(">Start<") && root.ToHtmlString().Contains("Press Start"), "one preview window shows its label and start action");
            await window.StartAsync();
            check(root.ToHtmlString().Contains("data-status=\"Running\""), "starting a preview window shows the running status");
            await window.PointerAsync(ProgramPreviewInputKind.MouseDown, 130, 254);
            await window.KeyAsync("a");
            var host = (RecordingPreviewHost)uiServices.GetRequiredService<IProgramPreviewHost>();
            check(host.Inputs.Any(input => input.Kind == ProgramPreviewInputKind.MouseDown && input.X == 130 && input.Y == 254)
                && host.Inputs.Any(input => input.Kind == ProgramPreviewInputKind.KeyDown && input.Key == "a"), "preview window events reach the host as pointer and key input");
            await window.DisposeAsync();
        });

        await using var blind = Build(options =>
        {
            options.XvfbPath = "/no/such/flexcore-xvfb";
            options.XdotoolPath = "/no/such/flexcore-xdotool";
            options.FfmpegPath = "/no/such/flexcore-ffmpeg";
            options.XwdPath = "/no/such/flexcore-xwd";
        });
        var blindHost = blind.GetRequiredService<IProgramPreviewHost>();
        check(!blindHost.Capability.CanRunGui && blindHost.Capability.Message.Contains("apt-get", StringComparison.OrdinalIgnoreCase), "missing virtual-display tools produce an install message");
        var denied = await blindHost.StartAsync(new ProgramLaunch { FileName = "python3", Arguments = ["-c", "print(1)"], Kind = ProgramPreviewKind.Gui });
        check(!denied.HasSession && denied.Snapshot.Status == ProgramPreviewStatus.Unavailable && denied.Snapshot.StatusText.Contains("xvfb", StringComparison.OrdinalIgnoreCase), "a GUI launch explains the missing packages");
        var auto = await blindHost.StartAsync(new ProgramLaunch { FileName = "python3", Arguments = ["-c", "print(1)"], Kind = ProgramPreviewKind.Auto });
        check(auto.Snapshot.Status == ProgramPreviewStatus.Unavailable, "Auto does not silently downgrade a GUI launch to the console");
        var consoleOnly = await blindHost.StartAsync(new ProgramLaunch
        {
            FileName = "python3",
            Arguments = ["-c", "print('console-ok', flush=True)"],
            Kind = ProgramPreviewKind.Console
        });
        check(consoleOnly.Snapshot.Status == ProgramPreviewStatus.Running, "console preview still runs when the virtual display is missing");
        await WaitForOutput(blindHost, consoleOnly, "console-ok");
        check(true, "console preview relays program output");
        if (consoleOnly.SessionId is not null && consoleOnly.Token is not null)
            blindHost.Release(consoleOnly.SessionId, consoleOnly.Token);

        await using var limited = Build(options =>
        {
            options.MaxConcurrentSessions = 2;
            options.MaxSessionsPerOwner = 1;
            options.IdleTimeout = TimeSpan.FromHours(1);
            options.MaxLifetime = TimeSpan.FromHours(1);
        });
        var limitedHost = limited.GetRequiredService<IProgramPreviewHost>();
        var first = await limitedHost.StartAsync(Sleeper(), "user-1");
        var second = await limitedHost.StartAsync(Sleeper(), "user-1");
        var third = await limitedHost.StartAsync(Sleeper(), "user-2");
        var fourth = await limitedHost.StartAsync(Sleeper(), "user-3");
        check(first.Snapshot.Status == ProgramPreviewStatus.Running && third.Snapshot.Status == ProgramPreviewStatus.Running, "two owners can each run one preview");
        check(second.Snapshot.Status == ProgramPreviewStatus.Failed && fourth.Snapshot.Status == ProgramPreviewStatus.Failed
            && second.Snapshot.StatusText.Contains("Too many", StringComparison.OrdinalIgnoreCase), "session and process limits reject extra previews");
        check(limitedHost.ReadFrame(first.SessionId!, "not-the-token").StatusCode == 401, "a wrong preview token is rejected");
        check(limitedHost.ReadFrame("missing-session", first.Token).StatusCode == 404, "an unknown preview session is not found");
        check(await limitedHost.SendInputAsync(first.SessionId!, "not-the-token", new ProgramPreviewInput { Kind = ProgramPreviewInputKind.Text, Text = "nope" }) == false, "input without the token is ignored");
        var echo = await limitedHost.StartAsync(new ProgramLaunch
        {
            FileName = "python3",
            Arguments = [Fixture("echo_line.py")],
            Kind = ProgramPreviewKind.Console
        }, "user-4");
        check(echo.Snapshot.Status == ProgramPreviewStatus.Failed, "the global session limit still applies to console previews");
        var limitedPid = first.Snapshot.ProcessId ?? 0;
        limitedHost.Release(first.SessionId!, first.Token!);
        check(await WaitUntilDead(limitedPid), "releasing a preview kills its process");
        if (third.SessionId is not null && third.Token is not null)
            limitedHost.Release(third.SessionId, third.Token);

        await using var idle = Build(options =>
        {
            options.IdleTimeout = TimeSpan.FromSeconds(1);
            options.MaxLifetime = TimeSpan.FromHours(1);
            options.SweepInterval = TimeSpan.FromMilliseconds(200);
        });
        var idleHost = idle.GetRequiredService<IProgramPreviewHost>();
        var idling = await idleHost.StartAsync(Sleeper());
        var idlePid = idling.Snapshot.ProcessId ?? 0;
        var idleDeadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < idleDeadline && !idleHost.GetSnapshot(idling.SessionId!, idling.Token!).StatusText.Contains("idle", StringComparison.OrdinalIgnoreCase))
            await Task.Delay(200);
        check(idleHost.GetSnapshot(idling.SessionId!, idling.Token!).StatusText.Contains("idle", StringComparison.OrdinalIgnoreCase), "an idle preview is stopped");
        check(!Alive(idlePid), "an idle timeout kills the program");

        await using var lifetime = Build(options =>
        {
            options.IdleTimeout = TimeSpan.FromHours(1);
            options.MaxLifetime = TimeSpan.FromSeconds(1);
            options.SweepInterval = TimeSpan.FromMilliseconds(200);
        });
        var lifetimeHost = lifetime.GetRequiredService<IProgramPreviewHost>();
        var expiring = await lifetimeHost.StartAsync(Sleeper());
        var lifeDeadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < lifeDeadline && !lifetimeHost.GetSnapshot(expiring.SessionId!, expiring.Token!).StatusText.Contains("time limit", StringComparison.OrdinalIgnoreCase))
            await Task.Delay(200);
        check(lifetimeHost.GetSnapshot(expiring.SessionId!, expiring.Token!).StatusText.Contains("time limit", StringComparison.OrdinalIgnoreCase), "a preview stops at its time limit");

        await using var consoleHostProvider = Build();
        var consoleHost = consoleHostProvider.GetRequiredService<IProgramPreviewHost>();
        var echoed = await consoleHost.StartAsync(new ProgramLaunch
        {
            FileName = "python3",
            Arguments = [Fixture("echo_line.py")],
            Kind = ProgramPreviewKind.Console
        });
        check(echoed.Snapshot.Status == ProgramPreviewStatus.Running, "a console program starts in its own session");
        await WaitForOutput(consoleHost, echoed, "ready");
        await consoleHost.SendInputAsync(echoed.SessionId!, echoed.Token!, new ProgramPreviewInput { Kind = ProgramPreviewInputKind.KeyDown, Key = "h" });
        await consoleHost.SendInputAsync(echoed.SessionId!, echoed.Token!, new ProgramPreviewInput { Kind = ProgramPreviewInputKind.KeyDown, Key = "i" });
        await consoleHost.SendInputAsync(echoed.SessionId!, echoed.Token!, new ProgramPreviewInput { Kind = ProgramPreviewInputKind.KeyDown, Key = "Enter" });
        await WaitForOutput(consoleHost, echoed, "got:hi");
        check(true, "console keystrokes are written to the real program");
        consoleHost.Release(echoed.SessionId!, echoed.Token!);

        await using var guiProvider = Build(options =>
        {
            options.DisplayWidth = 800;
            options.DisplayHeight = 600;
            options.FrameInterval = TimeSpan.FromMilliseconds(150);
        });
        var gui = guiProvider.GetRequiredService<IProgramPreviewHost>();
        check(gui.Capability.CanRunGui, "virtual display tools are installed for the live preview check: " + gui.Capability.Message);
        await ExerciseGui(gui, check);

        await using var xwdProvider = Build(options =>
        {
            options.DisplayWidth = 800;
            options.DisplayHeight = 600;
            options.FrameInterval = TimeSpan.FromMilliseconds(200);
            options.FfmpegPath = "/no/such/flexcore-ffmpeg";
        });
        var xwdHost = xwdProvider.GetRequiredService<IProgramPreviewHost>();
        check(xwdHost.Capability is { CanRunGui: true, HasFfmpeg: false, HasXwd: true }, "frame capture falls back to xwd when ffmpeg is absent");
        var xwdSession = await xwdHost.StartAsync(GuiLaunch("source_form.py"));
        try
        {
            check(xwdSession.Snapshot.Status == ProgramPreviewStatus.Running, "xwd preview starts the real program");
            await WaitForColor(xwdHost, xwdSession, 700, 80, 0xC0, 0x39, 0x2B);
            check(true, "xwd capture shows the live window");
            var frame = xwdHost.ReadFrame(xwdSession.SessionId!, xwdSession.Token);
            var png = PngRgb.Decode(frame.Body);
            check(frame.StatusCode == 200 && png.Width == 800 && png.Rgb[(80 * 800 + 700) * 3] == 0xC0, "the frame route returns the captured PNG");
        }
        finally
        {
            if (xwdSession.SessionId is not null && xwdSession.Token is not null)
                xwdHost.Release(xwdSession.SessionId, xwdSession.Token);
        }
    }

    private static async Task ExerciseGui(IProgramPreviewHost host, Action<bool, string> check)
    {
        var source = await host.StartAsync(GuiLaunch("source_form.py"), "pair");
        var target = await host.StartAsync(GuiLaunch("target_form.py"), "pair");
        var sourcePid = source.Snapshot.ProcessId ?? 0;
        var targetPid = target.Snapshot.ProcessId ?? 0;
        try
        {
            check(source.Snapshot.Status == ProgramPreviewStatus.Running && target.Snapshot.Status == ProgramPreviewStatus.Running, "source and target previews run together");
            check(source.Snapshot.DisplayNumber is int left && target.Snapshot.DisplayNumber is int right && left != right, "each preview gets its own virtual display");
            await WaitForColor(host, source, 700, 80, 0xC0, 0x39, 0x2B);
            await WaitForColor(host, target, 700, 80, 0x1D, 0x4E, 0x89);
            check(true, "both live windows are visible on their displays");
            await Click(host, source, 200, 180);
            await Task.Delay(200);
            await Key(host, source, "a");
            await Key(host, source, "b");
            await Task.Delay(200);
            for (var i = 0; i < 300; i++)
            {
                await host.SendInputAsync(source.SessionId!, source.Token!, new ProgramPreviewInput
                {
                    Kind = ProgramPreviewInputKind.MouseMove,
                    X = 20 + (i % 40),
                    Y = 20 + (i % 25)
                });
            }
            var clicked = DateTime.UtcNow;
            await Click(host, source, 130, 254);
            await WaitForColor(host, source, 700, 80, 0x1F, 0x7A, 0x3A);
            await WaitForOutput(host, source, "CLICKED:ab");
            check((DateTime.UtcNow - clicked).TotalSeconds < 5, "a burst of pointer moves does not delay the click");
            await WaitForColor(host, target, 700, 80, 0x1D, 0x4E, 0x89);
            check(!host.GetSnapshot(target.SessionId!, target.Token!).OutputText.Contains("CLICKED", StringComparison.Ordinal), "input reaches only the preview that was clicked");
            check(true, "a click and keystrokes change the real source program");
        }
        finally
        {
            if (source.SessionId is not null && source.Token is not null) host.Release(source.SessionId, source.Token);
            if (target.SessionId is not null && target.Token is not null) host.Release(target.SessionId, target.Token);
        }
        check(await WaitUntilDead(sourcePid) && await WaitUntilDead(targetPid), "releasing a preview kills both programs");
    }

    private static ProgramLaunch GuiLaunch(string script) => new()
    {
        FileName = "python3",
        Arguments = [Fixture(script)],
        WorkingDirectory = AppContext.BaseDirectory,
        Kind = ProgramPreviewKind.Gui
    };

    private static ProgramLaunch Sleeper() => new()
    {
        FileName = "python3",
        Arguments = ["-c", "import time; time.sleep(60)"],
        Kind = ProgramPreviewKind.Console
    };

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Preview", name);

    private static ServiceProvider Build(Action<ProgramPreviewOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlexCoreProgramPreview(configure);
        return services.BuildServiceProvider();
    }

    private static async Task Click(IProgramPreviewHost host, ProgramPreviewHandle handle, int x, int y)
    {
        await host.SendInputAsync(handle.SessionId!, handle.Token!, new ProgramPreviewInput { Kind = ProgramPreviewInputKind.MouseDown, X = x, Y = y });
        await host.SendInputAsync(handle.SessionId!, handle.Token!, new ProgramPreviewInput { Kind = ProgramPreviewInputKind.MouseUp, X = x, Y = y });
    }

    private static Task Key(IProgramPreviewHost host, ProgramPreviewHandle handle, string key)
        => host.SendInputAsync(handle.SessionId!, handle.Token!, new ProgramPreviewInput { Kind = ProgramPreviewInputKind.KeyDown, Key = key });

    private static async Task WaitForOutput(IProgramPreviewHost host, ProgramPreviewHandle handle, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (host.GetSnapshot(handle.SessionId!, handle.Token!).OutputText.Contains(expected, StringComparison.Ordinal))
                return;
            await Task.Delay(100);
        }
        throw new Exception($"Output did not contain \"{expected}\". Got: {host.GetSnapshot(handle.SessionId!, handle.Token!).OutputText}");
    }

    private static async Task WaitForColor(IProgramPreviewHost host, ProgramPreviewHandle handle, int x, int y, byte red, byte green, byte blue)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        string seen = "no frame";
        while (DateTime.UtcNow < deadline)
        {
            var image = host.CopyFrame(handle.SessionId!, handle.Token!);
            if (image is not null && x >= 0 && y >= 0 && x < image.Width && y < image.Height)
            {
                var index = (y * image.Width + x) * 3;
                seen = $"{image.Rgb[index]:X2}{image.Rgb[index + 1]:X2}{image.Rgb[index + 2]:X2}";
                if (Math.Abs(image.Rgb[index] - red) <= 12
                    && Math.Abs(image.Rgb[index + 1] - green) <= 12
                    && Math.Abs(image.Rgb[index + 2] - blue) <= 12)
                    return;
            }
            await Task.Delay(100);
        }
        throw new Exception($"Pixel {x},{y} stayed {seen}; expected {red:X2}{green:X2}{blue:X2}. Status: {host.GetSnapshot(handle.SessionId!, handle.Token!).StatusText} {host.GetSnapshot(handle.SessionId!, handle.Token!).ErrorText}");
    }

    private static async Task<bool> WaitUntilDead(int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Alive(pid) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        return !Alive(pid);
    }

    private static bool Alive(int pid)
    {
        if (pid <= 0) return false;
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }

    private static byte[] SampleXwd()
    {
        var header = new uint[25];
        header[0] = 102;
        header[1] = 7;
        header[2] = 2;
        header[3] = 24;
        header[4] = 2;
        header[5] = 2;
        header[7] = 0;
        header[11] = 32;
        header[12] = 8;
        header[14] = 0x00ff0000;
        header[15] = 0x0000ff00;
        header[16] = 0x000000ff;
        header[19] = 0;
        var bytes = new byte[102 + 32];
        for (var index = 0; index < header.Length; index++)
        {
            bytes[index * 4] = (byte)(header[index] >> 24);
            bytes[index * 4 + 1] = (byte)(header[index] >> 16);
            bytes[index * 4 + 2] = (byte)(header[index] >> 8);
            bytes[index * 4 + 3] = (byte)header[index];
        }
        bytes[100] = (byte)'t';
        WritePixel(bytes, 102, 0x00, 0x00, 0xFF);
        WritePixel(bytes, 106, 0x00, 0xFF, 0x00);
        WritePixel(bytes, 110, 0xFF, 0x00, 0x00);
        WritePixel(bytes, 118, 0xFF, 0xFF, 0xFF);
        return bytes;
    }

    private static void WritePixel(byte[] bytes, int offset, byte blue, byte green, byte red)
    {
        bytes[offset] = blue;
        bytes[offset + 1] = green;
        bytes[offset + 2] = red;
    }

    private sealed class PreviewActivator : IComponentActivator
    {
        public List<IComponent> All { get; } = [];
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            All.Add(component);
            return component;
        }
    }

    private sealed class RecordingPreviewHost : IProgramPreviewHost
    {
        private readonly byte[] _png = PngRgb.Encode([0, 128, 0], 1, 1);
        public List<ProgramPreviewInput> Inputs { get; } = [];
        public ProgramPreviewOptions Options { get; } = new();
        public ProgramPreviewCapability Capability { get; } = new() { CanRunGui = true, CanRunConsole = true };
        public Task<ProgramPreviewHandle> StartAsync(ProgramLaunch launch, string? ownerKey = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProgramPreviewHandle
            {
                SessionId = "recorded",
                Token = "token",
                Snapshot = new ProgramPreviewSnapshot
                {
                    Status = ProgramPreviewStatus.Running,
                    Kind = ProgramPreviewKind.Gui,
                    StatusText = "Running",
                    FrameVersion = 1,
                    FrameWidth = 1,
                    FrameHeight = 1,
                    Revision = 1
                }
            });
        public Task<ProgramPreviewSnapshot> RestartAsync(string sessionId, string token, CancellationToken cancellationToken = default)
            => Task.FromResult(GetSnapshot(sessionId, token));
        public Task<ProgramPreviewSnapshot> StopAsync(string sessionId, string token, string? reason = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProgramPreviewSnapshot { Status = ProgramPreviewStatus.Stopped, StatusText = reason ?? "Stopped" });
        public void Release(string sessionId, string token) { }
        public ProgramPreviewSnapshot GetSnapshot(string sessionId, string token) => token == "token"
            ? new ProgramPreviewSnapshot { Status = ProgramPreviewStatus.Running, StatusText = "Running", Kind = ProgramPreviewKind.Gui, FrameVersion = 1, FrameWidth = 1, FrameHeight = 1, Revision = 1 }
            : ProgramPreviewSnapshot.Missing;
        public ProgramPreviewFrame ReadFrame(string sessionId, string? token)
            => token == "token" ? new ProgramPreviewFrame(200, "image/png", _png) : new ProgramPreviewFrame(401, "text/plain", []);
        public ProgramPreviewImage? CopyFrame(string sessionId, string token) => null;
        public Task<bool> SendInputAsync(string sessionId, string token, ProgramPreviewInput input, CancellationToken cancellationToken = default)
        {
            if (token != "token" || input is null) return Task.FromResult(false);
            Inputs.Add(input);
            return Task.FromResult(true);
        }
    }
}
