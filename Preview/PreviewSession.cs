#pragma warning disable CA1416 // Process and X11 are server-only. The preview reports when they are unavailable.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Fx.ControlKit.Preview;

internal sealed class PreviewSession : IDisposable
{
    private readonly ProgramPreviewOptions _options;
    private readonly ProgramPreviewCapability _capability;
    private readonly ProgramLaunch _launch;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly StringBuilder _toolLog = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private CancellationTokenSource _cts = new();
    private CancellationTokenSource? _linked;
    private Channel<ProgramPreviewInput>? _inputs;
    private Process? _app;
    private Process? _xvfb;
    private Process? _ffmpeg;
    private StreamWriter? _stdin;
    private Task? _capture;
    private int _display = -1;
    private string? _displayLockPath;
    private string? _authority;
    private ProgramPreviewStatus _status;
    private string _statusText;
    private int? _exitCode;
    private int? _processId;
    private byte[]? _frame;
    private int _frameWidth;
    private int _frameHeight;
    private long _frameVersion;
    private long _revision = 1;
    private DateTimeOffset _started;
    private DateTimeOffset _lastActivity;
    private DateTimeOffset _statusChanged;
    private long _windowStart;
    private int _inputsThisWindow;
    private int _toolFailures;
    private int _disposed;

    public PreviewSession(ProgramLaunch launch, ProgramPreviewKind kind, string? ownerKey, ProgramPreviewOptions options, ProgramPreviewCapability capability)
    {
        _launch = launch;
        Kind = kind;
        OwnerKey = ownerKey;
        _options = options;
        _capability = capability;
        Id = Guid.NewGuid().ToString("N");
        Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Home = Path.Combine(Path.GetTempPath(), "flexcore-preview", "sessions", Id);
        Directory.CreateDirectory(Path.Combine(Home, "tmp"));
        _started = DateTimeOffset.UtcNow;
        _lastActivity = _started;
        _statusChanged = _started;
        _status = ProgramPreviewStatus.Starting;
        _statusText = "Starting";
    }

    public string Id { get; }
    public string Token { get; }
    public string? OwnerKey { get; }
    public ProgramPreviewKind Kind { get; }
    public string Home { get; }
    public bool OccupiesSlot { get { lock (_gate) return _status is ProgramPreviewStatus.Starting or ProgramPreviewStatus.Running; } }

    public bool TokenEquals(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != Token.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(Token));
    }

    public void MarkStarting()
    {
        lock (_gate)
        {
            _status = ProgramPreviewStatus.Starting;
            _statusText = "Starting";
            _statusChanged = DateTimeOffset.UtcNow;
            _revision++;
        }
    }

    public string? IdleReason(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_status is not (ProgramPreviewStatus.Running or ProgramPreviewStatus.Starting)) return null;
            if (now - _started >= _options.MaxLifetime) return "Stopped (time limit).";
            if (now - _lastActivity >= _options.IdleTimeout) return "Stopped (idle timeout).";
            return null;
        }
    }

    public bool ShouldDrop(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_status is ProgramPreviewStatus.Running or ProgramPreviewStatus.Starting) return false;
            return now - _statusChanged >= _options.StoppedRetention;
        }
    }

    public async Task<ProgramPreviewSnapshot> StartAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StopCore(null);
            _linked?.Dispose();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
            _linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            var token = _linked.Token;
            lock (_gate)
            {
                _status = ProgramPreviewStatus.Starting;
                _statusText = "Starting";
                _started = DateTimeOffset.UtcNow;
                _lastActivity = _started;
                _statusChanged = _started;
                _exitCode = null;
                _processId = null;
                _revision++;
            }
            _inputs = Channel.CreateUnbounded<ProgramPreviewInput>(new UnboundedChannelOptions { SingleReader = true });
            if (Kind == ProgramPreviewKind.Console)
                StartConsole();
            else
                await StartGuiAsync(token).ConfigureAwait(false);
            var channel = _inputs;
            var lifetime = _cts.Token;
            _ = Task.Run(() => InputLoop(channel, lifetime));
            var app = _app;
            if (app is not null)
                _ = Task.Run(() => WatchExitAsync(app, lifetime));
            Set(ProgramPreviewStatus.Running, "Running");
            return Snapshot();
        }
        catch (OperationCanceledException)
        {
            StopCore(null);
            var timedOut = !cancellationToken.IsCancellationRequested;
            Set(timedOut ? ProgramPreviewStatus.Failed : ProgramPreviewStatus.Stopped,
                timedOut ? "The preview did not start in time." : "Stopped");
            return Snapshot();
        }
        catch (Exception ex)
        {
            StopCore(null);
            Append(_stderr, ex.Message);
            Set(ProgramPreviewStatus.Failed, Short(ex.Message));
            return Snapshot();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task StopAsync(string reason)
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        await _mutex.WaitAsync().ConfigureAwait(false);
        try { StopCore(reason); }
        finally { _mutex.Release(); }
    }

    public bool Post(ProgramPreviewInput input)
    {
        Channel<ProgramPreviewInput>? channel;
        lock (_gate)
        {
            if (_status is not (ProgramPreviewStatus.Running or ProgramPreviewStatus.Starting)) return false;
            channel = _inputs;
        }
        if (channel is null || !AdmitInput()) return false;
        return channel.Writer.TryWrite(input);
    }

    public ProgramPreviewSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new ProgramPreviewSnapshot
            {
                Status = _status,
                Kind = Kind,
                StatusText = _statusText,
                ErrorText = Tail(_stderr),
                OutputText = Tail(_stdout),
                ExitCode = _exitCode,
                ProcessId = _processId,
                DisplayNumber = _display >= 0 ? _display : null,
                FrameVersion = _frameVersion,
                FrameWidth = _frameWidth,
                FrameHeight = _frameHeight,
                Revision = _revision
            };
        }
    }

    public ProgramPreviewFrame ReadFrame()
    {
        byte[]? frame;
        int width;
        int height;
        lock (_gate)
        {
            _lastActivity = DateTimeOffset.UtcNow;
            frame = _frame;
            width = _frameWidth;
            height = _frameHeight;
        }
        if (frame is null || width < 1 || height < 1)
            return new ProgramPreviewFrame(204, "image/png", []);
        return new ProgramPreviewFrame(200, "image/png", PngRgb.Encode(frame, width, height));
    }

    public ProgramPreviewImage? CopyFrame()
    {
        lock (_gate)
        {
            _lastActivity = DateTimeOffset.UtcNow;
            if (_frame is null || _frameWidth < 1) return null;
            var copy = new byte[_frame.Length];
            Buffer.BlockCopy(_frame, 0, copy, 0, _frame.Length);
            return new ProgramPreviewImage(_frameWidth, _frameHeight, copy, _frameVersion);
        }
    }

    public void Fail(string message)
    {
        Append(_stderr, message);
        Set(ProgramPreviewStatus.Failed, Short(message));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { StopCore("Stopped"); } catch { }
        try { _linked?.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
        try { Directory.Delete(Home, recursive: true); } catch { }
        _mutex.Dispose();
    }

    private async Task StartGuiAsync(CancellationToken cancellationToken)
    {
        if (_capability.XvfbPath is null || _capability.XdotoolPath is null)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(_capability.Message) ? "A virtual display is not available." : _capability.Message);
        var slot = DisplaySlots.Allocate(_options);
        _display = slot.Number;
        _displayLockPath = slot.LockPath;
        _authority = await TryCreateAuthorityAsync(_display, cancellationToken).ConfigureAwait(false);
        var xvfb = Tool(_capability.XvfbPath);
        xvfb.ArgumentList.Add(":" + _display);
        xvfb.ArgumentList.Add("-screen");
        xvfb.ArgumentList.Add("0");
        xvfb.ArgumentList.Add($"{_options.DisplayWidth}x{_options.DisplayHeight}x24");
        xvfb.ArgumentList.Add("-nolisten");
        xvfb.ArgumentList.Add("tcp");
        if (_authority is null)
        {
            xvfb.ArgumentList.Add("-ac");
        }
        else
        {
            xvfb.ArgumentList.Add("-auth");
            xvfb.ArgumentList.Add(_authority);
        }
        _xvfb = StartText(xvfb, userVisible: false);
        await WaitForDisplayAsync(cancellationToken).ConfigureAwait(false);
        _capture = Task.Run(() => CaptureAsync(_cts.Token));
        _app = StartText(Child(gui: true), userVisible: true);
        _processId = _app.Id;
        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        if (_app.HasExited)
            throw new InvalidOperationException($"The program exited ({_app.ExitCode}). {FirstDetail()}");
    }

    private void StartConsole()
    {
        var psi = Child(gui: false);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        _app = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the program.");
        _processId = _app.Id;
        _stdin = new StreamWriter(_app.StandardInput.BaseStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        _app.OutputDataReceived += (_, args) => { if (args.Data is not null) Append(_stdout, args.Data); };
        _app.ErrorDataReceived += (_, args) => { if (args.Data is not null) Append(_stderr, args.Data); };
        _app.BeginOutputReadLine();
        _app.BeginErrorReadLine();
    }

    private async Task WaitForDisplayAsync(CancellationToken cancellationToken)
    {
        var socket = "/tmp/.X11-unix/X" + _display;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_xvfb is { HasExited: true })
                throw new InvalidOperationException("Xvfb exited. " + FirstDetail());
            if (File.Exists(socket))
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                return;
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Xvfb did not open a display. " + FirstDetail());
    }

    private async Task CaptureAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_capability.FfmpegPath is not null)
            {
                try
                {
                    await CaptureFfmpegAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    NoteToolFailure(ex.Message);
                }
            }
            if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _frameVersion) > 0 || _capability.XwdPath is null)
                return;
            await CaptureXwdAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private async Task CaptureFfmpegAsync(CancellationToken cancellationToken)
    {
        var fps = Math.Clamp((int)Math.Round(1000d / _options.FrameInterval.TotalMilliseconds), 1, 12);
        var psi = Tool(_capability.FfmpegPath!);
        foreach (var argument in new[]
        {
            "-nostdin", "-loglevel", "error", "-f", "x11grab", "-draw_mouse", "1",
            "-video_size", $"{_options.DisplayWidth}x{_options.DisplayHeight}",
            "-framerate", fps.ToString(), "-i", ":" + _display,
            "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"
        })
            psi.ArgumentList.Add(argument);
        ApplyGuiEnvironment(psi);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardErrorEncoding = Encoding.UTF8;
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        _ffmpeg = process;
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) Append(_toolLog, args.Data); };
        process.BeginErrorReadLine();
        var stream = process.StandardOutput.BaseStream;
        var size = _options.DisplayWidth * _options.DisplayHeight * 3;
        var buffer = new byte[size];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = 0;
            while (read < size)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read, size - read), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    if (Volatile.Read(ref _frameVersion) == 0)
                        NoteToolFailure("ffmpeg did not produce a frame.");
                    return;
                }
                read += count;
            }
            var copy = new byte[size];
            Buffer.BlockCopy(buffer, 0, copy, 0, size);
            Publish(copy, _options.DisplayWidth, _options.DisplayHeight);
        }
    }

    private async Task CaptureXwdAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var dump = await CaptureXwdOnceAsync(cancellationToken).ConfigureAwait(false);
            if (dump is not null)
            {
                try
                {
                    var image = XwdImage.Decode(dump);
                    Publish(image.Rgb, image.Width, image.Height);
                }
                catch (Exception ex)
                {
                    NoteToolFailure("xwd: " + ex.Message);
                }
            }
            await Task.Delay(_options.FrameInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<byte[]?> CaptureXwdOnceAsync(CancellationToken cancellationToken)
    {
        if (_capability.XwdPath is null) return null;
        var psi = Tool(_capability.XwdPath);
        psi.ArgumentList.Add("-display");
        psi.ArgumentList.Add(":" + _display);
        psi.ArgumentList.Add("-root");
        psi.ArgumentList.Add("-silent");
        ApplyGuiEnvironment(psi);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var process = Process.Start(psi);
        if (process is null) return null;
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var memory = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            NoteToolFailure(string.IsNullOrWhiteSpace(error) ? "xwd failed." : error.Trim());
            return null;
        }
        return memory.ToArray();
    }

    private async Task InputLoop(Channel<ProgramPreviewInput> channel, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var input in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (Kind == ProgramPreviewKind.Console)
                        await WriteConsoleAsync(input, cancellationToken).ConfigureAwait(false);
                    else
                        await WriteGuiAsync(input, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    NoteToolFailure(ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task WriteGuiAsync(ProgramPreviewInput input, CancellationToken cancellationToken)
    {
        var x = Math.Clamp(input.X, 0, Math.Max(0, _options.DisplayWidth - 1));
        var y = Math.Clamp(input.Y, 0, Math.Max(0, _options.DisplayHeight - 1));
        switch (input.Kind)
        {
            case ProgramPreviewInputKind.MouseMove:
                await XdoAsync(cancellationToken, "mousemove", x.ToString(), y.ToString()).ConfigureAwait(false);
                break;
            case ProgramPreviewInputKind.MouseDown:
                await XdoAsync(cancellationToken, "mousemove", x.ToString(), y.ToString(), "mousedown", XButton(input.Button)).ConfigureAwait(false);
                break;
            case ProgramPreviewInputKind.MouseUp:
                await XdoAsync(cancellationToken, "mousemove", x.ToString(), y.ToString(), "mouseup", XButton(input.Button)).ConfigureAwait(false);
                break;
            case ProgramPreviewInputKind.Wheel:
                var button = input.WheelDelta >= 0 ? "5" : "4";
                var repeats = Math.Clamp(Math.Abs(input.WheelDelta) / 100, 1, 4);
                for (var index = 0; index < repeats; index++)
                    await XdoAsync(cancellationToken, "mousemove", x.ToString(), y.ToString(), "click", button).ConfigureAwait(false);
                break;
            case ProgramPreviewInputKind.Text:
                if (!string.IsNullOrEmpty(input.Text))
                    await XdoAsync(cancellationToken, "type", "--delay", "0", input.Text).ConfigureAwait(false);
                break;
            case ProgramPreviewInputKind.KeyDown:
                await WriteKeyAsync(input, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task WriteKeyAsync(ProgramPreviewInput input, CancellationToken cancellationToken)
    {
        var key = input.Key ?? "";
        if ((input.Ctrl || input.Alt || input.Meta) && KeyName(key) is { } modified)
        {
            await XdoAsync(cancellationToken, "key", "--delay", "0", Combo(input, modified)).ConfigureAwait(false);
            return;
        }
        if (key.Length == 1 || key is " " or "Space")
        {
            await XdoAsync(cancellationToken, "type", "--delay", "0", key is " " or "Space" ? " " : key).ConfigureAwait(false);
            return;
        }
        if (KeyName(key) is { } named)
            await XdoAsync(cancellationToken, "key", "--delay", "0", Combo(input, named)).ConfigureAwait(false);
    }

    private async Task WriteConsoleAsync(ProgramPreviewInput input, CancellationToken cancellationToken)
    {
        var text = input.Kind switch
        {
            ProgramPreviewInputKind.Text => input.Text ?? "",
            ProgramPreviewInputKind.KeyDown => ConsoleKey(input.Key),
            _ => ""
        };
        if (text.Length == 0 || _stdin is null) return;
        try
        {
            await _stdin.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private async Task XdoAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        if (_capability.XdotoolPath is null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var psi = Tool(_capability.XdotoolPath);
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        ApplyGuiEnvironment(psi);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var process = Process.Start(psi);
        if (process is null) return;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0)
                NoteToolFailure(string.IsNullOrWhiteSpace(stderr.Result) ? "xdotool failed." : "xdotool: " + stderr.Result.Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            NoteToolFailure("xdotool timed out.");
        }
    }

    private async Task WatchExitAsync(Process app, CancellationToken cancellationToken)
    {
        try
        {
            await app.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) { return; }
        if (cancellationToken.IsCancellationRequested) return;
        lock (_gate)
        {
            if (_status is not ProgramPreviewStatus.Running) return;
            try { _exitCode = app.ExitCode; } catch { _exitCode = null; }
            _status = ProgramPreviewStatus.Exited;
            _statusText = _exitCode is int code ? $"Exited ({code})" : "Exited";
            _statusChanged = DateTimeOffset.UtcNow;
            _revision++;
        }
    }

    private ProcessStartInfo Child(bool gui)
    {
        var psi = new ProcessStartInfo(_launch.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrEmpty(_launch.WorkingDirectory))
            psi.WorkingDirectory = _launch.WorkingDirectory;
        foreach (var argument in _launch.Arguments)
            psi.ArgumentList.Add(argument);
        psi.Environment["FLEXCORE_PREVIEW_WIDTH"] = _options.DisplayWidth.ToString();
        psi.Environment["FLEXCORE_PREVIEW_HEIGHT"] = _options.DisplayHeight.ToString();
        if (_launch.Environment is not null)
        {
            foreach (var pair in _launch.Environment)
            {
                if (pair.Value is null) psi.Environment.Remove(pair.Key);
                else psi.Environment[pair.Key] = pair.Value;
            }
        }
        if (!gui) return psi;
        var temp = Path.Combine(Home, "tmp");
        psi.Environment["HOME"] = Home;
        psi.Environment["TMPDIR"] = temp;
        psi.Environment["TMP"] = temp;
        psi.Environment["TEMP"] = temp;
        psi.Environment["DISPLAY"] = ":" + _display;
        psi.Environment.Remove("WAYLAND_DISPLAY");
        if (!psi.Environment.ContainsKey("GDK_BACKEND")) psi.Environment["GDK_BACKEND"] = "x11";
        if (!psi.Environment.ContainsKey("QT_QPA_PLATFORM")) psi.Environment["QT_QPA_PLATFORM"] = "xcb";
        if (_authority is not null) psi.Environment["XAUTHORITY"] = _authority;
        else psi.Environment.Remove("XAUTHORITY");
        return psi;
    }

    private void ApplyGuiEnvironment(ProcessStartInfo psi)
    {
        psi.Environment["DISPLAY"] = ":" + _display;
        psi.Environment.Remove("WAYLAND_DISPLAY");
        if (_authority is not null) psi.Environment["XAUTHORITY"] = _authority;
        else psi.Environment.Remove("XAUTHORITY");
    }

    private Process StartText(ProcessStartInfo psi, bool userVisible)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {psi.FileName}.");
        var stdout = userVisible ? _stdout : _toolLog;
        var stderr = userVisible ? _stderr : _toolLog;
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) Append(stdout, args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) Append(stderr, args.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static ProcessStartInfo Tool(string fileName) => new(fileName)
    {
        UseShellExecute = false,
        CreateNoWindow = true
    };

    private async Task<string?> TryCreateAuthorityAsync(int display, CancellationToken cancellationToken)
    {
        if (_capability.XauthPath is null || _capability.McookiePath is null) return null;
        try
        {
            var cookie = (await RunCaptureAsync(_capability.McookiePath, cancellationToken).ConfigureAwait(false)).Trim();
            if (cookie.Length < 8) return null;
            var path = Path.Combine(Home, "Xauthority");
            var code = await RunWaitAsync(_capability.XauthPath, ["-f", path, "add", ":" + display, "MIT-MAGIC-COOKIE-1", cookie], cancellationToken).ConfigureAwait(false);
            return code == 0 && File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> RunCaptureAsync(string fileName, CancellationToken cancellationToken)
    {
        var psi = Tool(fileName);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.Environment.Remove("DISPLAY");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException(fileName);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        return stdout.Result;
    }

    private static async Task<int> RunWaitAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var psi = Tool(fileName);
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.Environment.Remove("DISPLAY");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException(fileName);
        await Task.WhenAll(
            process.StandardOutput.ReadToEndAsync(cancellationToken),
            process.StandardError.ReadToEndAsync(cancellationToken),
            process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        return process.ExitCode;
    }

    private void Publish(byte[] rgb, int width, int height)
    {
        lock (_gate)
        {
            _frame = rgb;
            _frameWidth = width;
            _frameHeight = height;
            _frameVersion++;
            _revision++;
        }
    }

    private bool AdmitInput()
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            _lastActivity = DateTimeOffset.UtcNow;
            if (now - _windowStart > 1000)
            {
                _windowStart = now;
                _inputsThisWindow = 0;
            }
            if (_inputsThisWindow >= 80) return false;
            _inputsThisWindow++;
            return true;
        }
    }

    private void StopCore(string? reason)
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _inputs?.Writer.TryComplete(); } catch { }
        try { _stdin?.Dispose(); } catch { }
        _stdin = null;
        Kill(ref _app);
        Kill(ref _ffmpeg);
        Kill(ref _xvfb);
        DisplaySlots.Release(_displayLockPath);
        _displayLockPath = null;
        _display = -1;
        if (reason is null) return;
        lock (_gate)
        {
            if (_status is not (ProgramPreviewStatus.Running or ProgramPreviewStatus.Starting)) return;
            _status = ProgramPreviewStatus.Stopped;
            _statusText = reason;
            _statusChanged = DateTimeOffset.UtcNow;
            _revision++;
        }
    }

    private void Set(ProgramPreviewStatus status, string text)
    {
        lock (_gate)
        {
            _status = status;
            _statusText = text;
            _statusChanged = DateTimeOffset.UtcNow;
            _revision++;
        }
    }

    private void Append(StringBuilder builder, string line)
    {
        lock (_gate) AppendUnlocked(builder, line);
    }

    private void AppendUnlocked(StringBuilder builder, string line)
    {
        builder.AppendLine(line);
        if (builder.Length > 32_000)
            builder.Remove(0, builder.Length - 24_000);
        _revision++;
    }

    private void NoteToolFailure(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (_gate)
        {
            if (_toolFailures++ > 0) return;
            AppendUnlocked(_stderr, message.Trim());
        }
    }

    private string FirstDetail()
    {
        lock (_gate)
        {
            var error = Tail(_stderr);
            if (!string.IsNullOrWhiteSpace(error)) return Short(error);
            var tool = Tail(_toolLog);
            if (!string.IsNullOrWhiteSpace(tool)) return Short(tool);
            var output = Tail(_stdout);
            return string.IsNullOrWhiteSpace(output) ? "The program produced no output." : Short(output);
        }
    }

    private static string Tail(StringBuilder builder)
    {
        var text = builder.ToString().Trim();
        return text.Length <= 4000 ? text : text[^4000..];
    }

    private static string Short(string message)
    {
        var line = message.Trim().Split('\n', 2)[0].Trim();
        return line.Length <= 240 ? line : line[..240];
    }

    private static string Combo(ProgramPreviewInput input, string key)
    {
        var parts = new List<string>(5);
        if (input.Ctrl) parts.Add("ctrl");
        if (input.Alt) parts.Add("alt");
        if (input.Shift) parts.Add("shift");
        if (input.Meta) parts.Add("super");
        parts.Add(key);
        return string.Join('+', parts);
    }

    private static string? KeyName(string key) => key switch
    {
        "Enter" => "Return",
        "Backspace" => "BackSpace",
        "Tab" => "Tab",
        "Escape" => "Escape",
        "Delete" => "Delete",
        "ArrowLeft" => "Left",
        "ArrowRight" => "Right",
        "ArrowUp" => "Up",
        "ArrowDown" => "Down",
        "Home" => "Home",
        "End" => "End",
        "PageUp" => "Prior",
        "PageDown" => "Next",
        " " or "Space" => "space",
        { Length: 1 } => char.IsLetter(key[0]) ? key.ToLowerInvariant() : key,
        _ => null
    };

    private static string ConsoleKey(string? key) => key switch
    {
        "Enter" => "\n",
        "Tab" => "\t",
        "Backspace" => "\b",
        " " or "Space" => " ",
        { Length: 1 } => key,
        _ => ""
    };

    private static string XButton(int button) => button switch
    {
        1 => "2",
        2 => "3",
        _ => "1"
    };

    private static void Kill(ref Process? process)
    {
        var current = process;
        process = null;
        Kill(current);
    }

    private static void Kill(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
        try { process.Dispose(); } catch { }
    }
}

internal static class DisplaySlots
{
    private static readonly object Gate = new();

    public static (int Number, string LockPath) Allocate(ProgramPreviewOptions options)
    {
        var directory = Path.Combine(Path.GetTempPath(), "flexcore-preview", "displays");
        Directory.CreateDirectory(directory);
        lock (Gate)
        {
            for (var number = options.FirstDisplayNumber; number < options.FirstDisplayNumber + options.DisplayCount; number++)
            {
                if (File.Exists("/tmp/.X11-unix/X" + number)) continue;
                var path = Path.Combine(directory, number + ".lock");
                if (File.Exists(path))
                {
                    if (LockIsLive(path)) continue;
                    try { File.Delete(path); } catch { continue; }
                }
                try
                {
                    using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    using var writer = new StreamWriter(created);
                    writer.Write(Environment.ProcessId);
                }
                catch (IOException)
                {
                    continue;
                }
                return (number, path);
            }
        }
        throw new InvalidOperationException("No virtual display numbers are free.");
    }

    public static void Release(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { File.Delete(path); } catch { }
    }

    private static bool LockIsLive(string path)
    {
        try
        {
            var text = File.ReadAllText(path).Trim();
            if (!int.TryParse(text, out var pid)) return false;
            return !Process.GetProcessById(pid).HasExited;
        }
        catch
        {
            return false;
        }
    }
}
