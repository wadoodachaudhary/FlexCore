using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Fx.ControlKit.Preview;

/// <summary>
/// One live program. Pointer and keyboard events are sent to the process started
/// by <see cref="IProgramPreviewHost"/>. The picture is a capture of that process,
/// not a recording.
/// </summary>
public partial class ProgramPreviewWindow
{
    private readonly CancellationTokenSource _lifetime = new();
    private PeriodicTimer? _timer;
    private IJSObjectReference? _module;
    private ElementReference _stage;
    private ElementReference _image;
    private ProgramPreviewSnapshot? _snapshot;
    private string? _sessionId;
    private string? _token;
    private bool _disposed;
    private bool _loopStarted;
    private bool _busy;
    private long _lastMove;
    private string? _localMessage;

    [Inject] private IServiceProvider Services { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter] public ProgramLaunch? Launch { get; set; }
    [Parameter] public string Label { get; set; } = "Program";
    [Parameter] public bool AutoStart { get; set; }
    [Parameter] public bool ShowToolbar { get; set; } = true;
    [Parameter] public bool ShowOutput { get; set; } = true;
    [Parameter] public string? OwnerKey { get; set; }
    [Parameter] public string StartText { get; set; } = "Start";
    [Parameter] public string RestartText { get; set; } = "Restart";
    [Parameter] public string StopText { get; set; } = "Stop";
    [Parameter] public EventCallback<ProgramPreviewSnapshot> SnapshotChanged { get; set; }

    private IProgramPreviewHost? Host => Services.GetService<IProgramPreviewHost>();
    public ProgramPreviewSnapshot? Snapshot => _snapshot;
    public string? SessionId => _sessionId;

    private ProgramPreviewStatus Status => _snapshot?.Status ?? ProgramPreviewStatus.Missing;
    private string StatusName => _snapshot?.Status.ToString() ?? "Empty";
    private bool IsStarting => _busy || Status is ProgramPreviewStatus.Starting;
    private bool CanStart => Enabled && !_busy && Launch is not null && Status is not (ProgramPreviewStatus.Running or ProgramPreviewStatus.Starting);
    private bool CanRestart => Enabled && !_busy && Launch is not null && _sessionId is not null;
    private bool CanStop => Enabled && Status is ProgramPreviewStatus.Running or ProgramPreviewStatus.Starting;
    private bool ShowConsole => _snapshot?.Kind == ProgramPreviewKind.Console && _sessionId is not null;
    private bool IsAlert => Status is ProgramPreviewStatus.Failed or ProgramPreviewStatus.Unavailable || _localMessage is not null && _snapshot is null;
    private string SurfaceLabel => string.IsNullOrWhiteSpace(Label) ? "Live preview" : Label + " live preview";
    private string ConsoleText => string.IsNullOrEmpty(_snapshot?.OutputText) ? "" : _snapshot.OutputText;
    private string ErrorText => _snapshot?.ErrorText ?? "";
    private int FrameWidth => _snapshot is { FrameWidth: > 0 } ? _snapshot.FrameWidth : Host?.Options.DisplayWidth ?? 800;
    private int FrameHeight => _snapshot is { FrameHeight: > 0 } ? _snapshot.FrameHeight : Host?.Options.DisplayHeight ?? 600;

    private string StatusLine
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_snapshot?.StatusText)) return _snapshot.StatusText;
            if (!string.IsNullOrWhiteSpace(_localMessage)) return _localMessage;
            if (Host is null) return "Call AddFlexCoreProgramPreview in the host.";
            return "Press Start to run this program.";
        }
    }

    private string Placeholder
    {
        get
        {
            if (Launch is null) return "No program was supplied.";
            if (Host is null) return "Call AddFlexCoreProgramPreview in the host.";
            if (GuiBlocked is { Length: > 0 } blocked && _sessionId is null) return blocked;
            if (!string.IsNullOrWhiteSpace(_localMessage) && _snapshot is null) return _localMessage;
            return "Press Start to run this program.";
        }
    }

    private string? GuiBlocked
        => Launch?.Kind == ProgramPreviewKind.Console ? null
            : Host is { Capability.CanRunGui: false } host ? host.Capability.Message : null;

    private string? FrameSource
    {
        get
        {
            var host = Host;
            if (host is null || _sessionId is null || _token is null || _snapshot is not { FrameVersion: > 0 })
                return null;
            if (!string.IsNullOrWhiteSpace(host.Options.FrameRoutePrefix))
                return ProgramPreviewHttp.FramePath(host.Options.FrameRoutePrefix, _sessionId, _token, _snapshot.FrameVersion);
            var frame = host.ReadFrame(_sessionId, _token);
            return frame.StatusCode == 200 && frame.Body.Length > 0
                ? "data:image/png;base64," + Convert.ToBase64String(frame.Body)
                : null;
        }
    }

    protected override string? BaseCssClass => "fx-preview";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _loopStarted || _disposed) return;
        _loopStarted = true;
        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        _ = PollAsync();
        if (AutoStart && Launch is not null)
            await StartAsync();
    }

    public async Task StartAsync()
    {
        if (_disposed || _busy || !Enabled || Launch is null) return;
        var host = Host;
        if (host is null)
        {
            _localMessage = "Call AddFlexCoreProgramPreview in the host.";
            await InvokeAsync(StateHasChanged);
            return;
        }
        _busy = true;
        _localMessage = null;
        await InvokeAsync(StateHasChanged);
        try
        {
            if (_sessionId is not null && _token is not null)
                host.Release(_sessionId, _token);
            var handle = await host.StartAsync(Launch, OwnerKey, _lifetime.Token);
            _sessionId = handle.SessionId;
            _token = handle.Token;
            await ApplySnapshotAsync(handle.Snapshot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _localMessage = ex.Message;
        }
        finally
        {
            _busy = false;
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    public async Task RestartAsync()
    {
        if (_disposed || _busy || !Enabled || Launch is null) return;
        var host = Host;
        if (host is null || _sessionId is null || _token is null)
        {
            await StartAsync();
            return;
        }
        _busy = true;
        await InvokeAsync(StateHasChanged);
        try
        {
            var snapshot = await host.RestartAsync(_sessionId, _token, _lifetime.Token);
            await ApplySnapshotAsync(snapshot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _localMessage = ex.Message;
        }
        finally
        {
            _busy = false;
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    public async Task StopAsync()
    {
        if (_disposed || Host is null || _sessionId is null || _token is null) return;
        var snapshot = await Host.StopAsync(_sessionId, _token, cancellationToken: _lifetime.Token);
        await ApplySnapshotAsync(snapshot);
    }

    public Task PointerAsync(ProgramPreviewInputKind kind, int x, int y, int button = 0)
        => SendAsync(new ProgramPreviewInput { Kind = kind, X = x, Y = y, Button = button });

    public Task KeyAsync(string key, bool shift = false, bool ctrl = false, bool alt = false, bool meta = false)
        => SendAsync(new ProgramPreviewInput
        {
            Kind = ProgramPreviewInputKind.KeyDown,
            Key = key,
            Shift = shift,
            Ctrl = ctrl,
            Alt = alt,
            Meta = meta
        });

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _timer?.Dispose();
        if (Host is not null && _sessionId is not null && _token is not null)
            Host.Release(_sessionId, _token);
        _lifetime.Dispose();
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); } catch { }
        }
    }

    private async Task PollAsync()
    {
        if (_timer is null) return;
        try
        {
            while (await _timer.WaitForNextTickAsync(_lifetime.Token))
            {
                var host = Host;
                if (host is null || _sessionId is null || _token is null) continue;
                var snapshot = host.GetSnapshot(_sessionId, _token);
                if (snapshot.Status == ProgramPreviewStatus.Missing) continue;
                if (_snapshot is null || snapshot.Revision != _snapshot.Revision)
                    await ApplySnapshotAsync(snapshot);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ApplySnapshotAsync(ProgramPreviewSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (SnapshotChanged.HasDelegate)
            await SnapshotChanged.InvokeAsync(snapshot);
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnMouseDown(MouseEventArgs args)
    {
        await FocusAsync();
        await PointerFromEventAsync(ProgramPreviewInputKind.MouseDown, args, (int)args.Button);
    }

    private Task OnMouseUp(MouseEventArgs args)
        => PointerFromEventAsync(ProgramPreviewInputKind.MouseUp, args, (int)args.Button);

    private Task OnMouseMove(MouseEventArgs args)
    {
        var now = Environment.TickCount64;
        if (now - _lastMove < 40) return Task.CompletedTask;
        _lastMove = now;
        return PointerFromEventAsync(ProgramPreviewInputKind.MouseMove, args, (int)args.Button);
    }

    private async Task OnWheel(WheelEventArgs args)
    {
        var point = await MapAsync(args.ClientX, args.ClientY, args.OffsetX, args.OffsetY);
        await SendAsync(new ProgramPreviewInput
        {
            Kind = ProgramPreviewInputKind.Wheel,
            X = point.X,
            Y = point.Y,
            WheelDelta = (int)Math.Round(args.DeltaY)
        });
    }

    private Task OnKeyDown(KeyboardEventArgs args)
        => KeyAsync(args.Key, args.ShiftKey, args.CtrlKey, args.AltKey, args.MetaKey);

    private async Task PointerFromEventAsync(ProgramPreviewInputKind kind, MouseEventArgs args, int button)
    {
        var point = await MapAsync(args.ClientX, args.ClientY, args.OffsetX, args.OffsetY);
        await PointerAsync(kind, point.X, point.Y, button);
    }

    private async Task<(int X, int Y)> MapAsync(double clientX, double clientY, double offsetX, double offsetY)
    {
        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>("import",
                $"./_content/{typeof(ProgramPreviewWindow).Assembly.GetName().Name}/program-preview.js");
            if (_module is not null)
            {
                var point = await _module.InvokeAsync<ProgramPreviewPoint>("imagePoint", _image, clientX, clientY);
                if (point is not null)
                    return ((int)Math.Round(point.X), (int)Math.Round(point.Y));
            }
        }
        catch
        {
            // HtmlRenderer and a missing script both fall back to the event offset.
        }
        return ((int)Math.Round(offsetX), (int)Math.Round(offsetY));
    }

    private async Task FocusAsync()
    {
        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>("import",
                $"./_content/{typeof(ProgramPreviewWindow).Assembly.GetName().Name}/program-preview.js");
            if (_module is not null)
                await _module.InvokeVoidAsync("focus", _stage);
        }
        catch { }
    }

    private Task SendAsync(ProgramPreviewInput input)
    {
        var host = Host;
        if (host is null || _sessionId is null || _token is null) return Task.CompletedTask;
        return host.SendInputAsync(_sessionId, _token, input, _lifetime.Token);
    }

}

/// <summary>Pointer position returned by the preview script, in framebuffer pixels.</summary>
public sealed class ProgramPreviewPoint
{
    public double X { get; set; }
    public double Y { get; set; }
}
