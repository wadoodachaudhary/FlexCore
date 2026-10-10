using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Fx.ControlKit.Preview;

internal sealed class ProgramPreviewHost : IProgramPreviewHost, IDisposable
{
    private readonly ILogger<ProgramPreviewHost> _logger;
    private readonly ConcurrentDictionary<string, PreviewSession> _sessions = new();
    private readonly CancellationTokenSource _dispose = new();
    private readonly PeriodicTimer _sweep;
    private readonly object _gate = new();
    private int _disposed;

    public ProgramPreviewHost(ProgramPreviewOptions options, ILogger<ProgramPreviewHost> logger)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        options.Validate();
        Capability = PreviewTools.Probe(options);
        _sweep = new PeriodicTimer(options.SweepInterval);
        _ = SweepAsync();
    }

    public ProgramPreviewOptions Options { get; }
    public ProgramPreviewCapability Capability { get; }

    public async Task<ProgramPreviewHandle> StartAsync(ProgramLaunch launch, string? ownerKey = null, CancellationToken cancellationToken = default)
    {
        if (launch is null) return Reject(ProgramPreviewStatus.Failed, "No program was supplied.");
        try
        {
            launch.Validate();
        }
        catch (ArgumentException ex)
        {
            return Reject(ProgramPreviewStatus.Failed, ex.Message);
        }

        var kind = Resolve(launch.Kind);
        if (kind is null)
            return Reject(ProgramPreviewStatus.Unavailable, Capability.Message);
        if (kind == ProgramPreviewKind.Gui && !Capability.CanRunGui)
            return Reject(ProgramPreviewStatus.Unavailable, Capability.Message);

        var owner = CleanOwner(ownerKey);
        var session = new PreviewSession(launch, kind.Value, owner, Options, Capability);
        lock (_gate)
        {
            if (_disposed != 0)
            {
                session.Dispose();
                return Reject(ProgramPreviewStatus.Failed, "The preview host has stopped.");
            }
            if (!WithinLimits(owner, except: null))
            {
                session.Dispose();
                return Reject(ProgramPreviewStatus.Failed, LimitMessage(owner));
            }
            _sessions[session.Id] = session;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Options.StartTimeout);
        try
        {
            var snapshot = await session.StartAsync(timeout.Token).ConfigureAwait(false);
            if (snapshot.Status is ProgramPreviewStatus.Failed or ProgramPreviewStatus.Unavailable)
                _logger.LogWarning("Preview {SessionId} did not start: {Status}", session.Id, snapshot.StatusText);
            return new ProgramPreviewHandle { SessionId = session.Id, Token = session.Token, Snapshot = snapshot };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Preview {SessionId} failed to start.", session.Id);
            session.Fail(ex.Message);
            return new ProgramPreviewHandle { SessionId = session.Id, Token = session.Token, Snapshot = session.Snapshot() };
        }
    }

    public async Task<ProgramPreviewSnapshot> RestartAsync(string sessionId, string token, CancellationToken cancellationToken = default)
    {
        if (!TryGet(sessionId, token, out var session)) return ProgramPreviewSnapshot.Missing;
        if (!session.OccupiesSlot)
        {
            var reserved = false;
            lock (_gate)
            {
                if (WithinLimits(session.OwnerKey, session))
                {
                    session.MarkStarting();
                    reserved = true;
                }
            }
            if (!reserved)
            {
                session.Fail("Too many live previews are running.");
                return session.Snapshot();
            }
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Options.StartTimeout);
        return await session.StartAsync(timeout.Token).ConfigureAwait(false);
    }

    public async Task<ProgramPreviewSnapshot> StopAsync(string sessionId, string token, string? reason = null, CancellationToken cancellationToken = default)
    {
        if (!TryGet(sessionId, token, out var session)) return ProgramPreviewSnapshot.Missing;
        await session.StopAsync(string.IsNullOrWhiteSpace(reason) ? "Stopped" : reason).ConfigureAwait(false);
        return session.Snapshot();
    }

    public void Release(string sessionId, string token)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || !session.TokenEquals(token)) return;
        if (_sessions.TryRemove(sessionId, out var removed))
            removed.Dispose();
    }

    public ProgramPreviewSnapshot GetSnapshot(string sessionId, string token)
        => TryGet(sessionId, token, out var session) ? session.Snapshot() : ProgramPreviewSnapshot.Missing;

    public ProgramPreviewFrame ReadFrame(string sessionId, string? token)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return new ProgramPreviewFrame(404, "text/plain", "Preview session was not found."u8.ToArray());
        if (!session.TokenEquals(token))
            return new ProgramPreviewFrame(401, "text/plain", "Preview session token was rejected."u8.ToArray());
        return session.ReadFrame();
    }

    public ProgramPreviewImage? CopyFrame(string sessionId, string token)
        => TryGet(sessionId, token, out var session) ? session.CopyFrame() : null;

    public Task<bool> SendInputAsync(string sessionId, string token, ProgramPreviewInput input, CancellationToken cancellationToken = default)
    {
        if (input is null || !TryGet(sessionId, token, out var session)) return Task.FromResult(false);
        return Task.FromResult(session.Post(input));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _dispose.Cancel();
        _sweep.Dispose();
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
        _dispose.Dispose();
    }

    private async Task SweepAsync()
    {
        try
        {
            while (await _sweep.WaitForNextTickAsync(_dispose.Token).ConfigureAwait(false))
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var session in _sessions.Values.ToArray())
                {
                    var reason = session.IdleReason(now);
                    if (reason is not null)
                    {
                        _logger.LogInformation("Stopping preview {SessionId}: {Reason}", session.Id, reason);
                        await session.StopAsync(reason).ConfigureAwait(false);
                    }
                    else if (session.ShouldDrop(now) && _sessions.TryRemove(session.Id, out var removed))
                    {
                        removed.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private ProgramPreviewKind? Resolve(ProgramPreviewKind kind) => kind switch
    {
        ProgramPreviewKind.Console => ProgramPreviewKind.Console,
        ProgramPreviewKind.Gui => ProgramPreviewKind.Gui,
        _ => Capability.CanRunGui ? ProgramPreviewKind.Gui : null
    };

    private bool WithinLimits(string? owner, PreviewSession? except)
    {
        var running = 0;
        var owned = 0;
        foreach (var session in _sessions.Values)
        {
            if (ReferenceEquals(session, except) || !session.OccupiesSlot) continue;
            running++;
            if (owner is not null && session.OwnerKey == owner) owned++;
        }
        if (running >= Options.MaxConcurrentSessions) return false;
        if (owner is not null && owned >= Options.MaxSessionsPerOwner) return false;
        return true;
    }

    private static string LimitMessage(string? owner) => "Too many live previews are running.";

    private bool TryGet(string sessionId, string token, out PreviewSession session)
    {
        if (_sessions.TryGetValue(sessionId, out session!) && session.TokenEquals(token))
            return true;
        session = null!;
        return false;
    }

    private static ProgramPreviewHandle Reject(ProgramPreviewStatus status, string message) => new()
    {
        Snapshot = new ProgramPreviewSnapshot { Status = status, StatusText = message, ErrorText = message }
    };

    private static string? CleanOwner(string? ownerKey)
    {
        if (string.IsNullOrWhiteSpace(ownerKey)) return null;
        var trimmed = ownerKey.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return trimmed.Length <= 128 ? trimmed : trimmed[..128];
    }
}
