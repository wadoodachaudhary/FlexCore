using Fx.ControlKit.Logging;
using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit;

/// <summary>
/// Root base class for every FlexKit control.
/// Provides the standard <see cref="Tag"/> and <see cref="Log"/> fields on all controls,
/// hierarchical parent-location discovery (<see cref="ParentLocation"/> / <see cref="LogicalParent"/>),
/// and zero-round-trip server-side action logging into <see cref="IFlexControlLogSink"/>.
/// </summary>
public abstract class FlexKitControlBase : ComponentBase
{
    protected string? _tag;
    protected readonly FlexControlLog _log = new();

    private bool _servicesResolved;
    private IFlexControlLogSink? _resolvedSink;
    private IFlexControlContextProvider? _resolvedContextProvider;
    private FlexControlLocation? _cachedChildLocation;
    private bool _flushQueued;
    private string? _cachedTypeName;

    /// <summary>Application-defined metadata tag identifying this control instance.</summary>
    [Parameter]
    public string? Tag
    {
        get => _tag;
        set => _tag = value;
    }

    /// <summary>
    /// Live per-control action log buffer. Updated immediately whenever an action occurs on the control
    /// and flushed to the underlying <see cref="IFlexControlLogSink"/> in memory without extra server trips.
    /// </summary>
    public FlexControlLog Log => _log;

    /// <summary>Optional explicit logical parent control.</summary>
    public FlexKitControlBase? LogicalParent { get; set; }

    protected virtual FlexKitControlBase? ResolveExplicitParent() => LogicalParent;

    /// <summary>Automatic hierarchical container location cascaded by enclosing FlexKit containers.</summary>
    [CascadingParameter]
    public FlexControlLocation? ParentLocation { get; set; }

    /// <summary>Optional cascaded sink override (useful for isolated component tests).</summary>
    [CascadingParameter]
    public IFlexControlLogSink? CascadedLogSink { get; set; }

    [Inject]
    private IServiceProvider? FlexControlServices { get; set; }

    protected virtual string ControlTypeName
    {
        get
        {
            if (_cachedTypeName is not null)
                return _cachedTypeName;
            var raw = GetType().Name;
            var tick = raw.IndexOf('`');
            _cachedTypeName = tick > 0 ? raw[..tick] : raw;
            return _cachedTypeName;
        }
    }

    /// <summary>
    /// Subclasses override to return their primary human-readable or schema identifier
    /// (e.g., Label, Text, Title, Id, PreferenceKey) when <see cref="Tag"/> is not set.
    /// </summary>
    protected virtual string? ResolveControlIdentifier() =>
        !string.IsNullOrWhiteSpace(_tag) ? _tag.Trim() : null;

    protected FlexControlLocation GetOrCreateChildLocation(
        string containerType,
        Func<string?> nameResolver,
        Func<string?>? routeResolver = null,
        bool isScreenRoot = false)
    {
        _cachedChildLocation ??= new FlexControlLocation(
            ParentLocation,
            containerType,
            nameResolver,
            routeResolver,
            isScreenRoot);
        return _cachedChildLocation;
    }

    private (IFlexControlLogSink? Sink, IFlexControlContextProvider? Context) ResolveLoggingServices()
    {
        if (CascadedLogSink is not null)
            return (CascadedLogSink, _resolvedContextProvider);

        if (!_servicesResolved)
        {
            _servicesResolved = true;
            try
            {
                _resolvedSink = FlexControlServices?.GetService(typeof(IFlexControlLogSink)) as IFlexControlLogSink;
                _resolvedContextProvider = FlexControlServices?.GetService(typeof(IFlexControlContextProvider)) as IFlexControlContextProvider;
            }
            catch
            {
                // Safe fallback when instantiated outside a full DI scope.
            }
        }

        return (_resolvedSink, _resolvedContextProvider);
    }

    public (string Screen, string Route, string FullLocation, string ControlId) ResolveControlLocation(string? controlIdOverride = null)
    {
        var (_, ctxProvider) = ResolveLoggingServices();
        var hostCtx = ctxProvider?.GetCurrentContext() ?? default;

        string screen = string.Empty;
        string route = string.Empty;
        string containerPath = string.Empty;

        if (ParentLocation is not null)
        {
            (screen, route, containerPath) = ParentLocation.ResolveHierarchy();
        }
        else if (ResolveExplicitParent() is { } explicitParent)
        {
            var parentLoc = explicitParent.ResolveControlLocation();
            screen = parentLoc.Screen;
            route = parentLoc.Route;
            containerPath = parentLoc.FullLocation;
        }

        if (string.IsNullOrWhiteSpace(screen))
            screen = !string.IsNullOrWhiteSpace(hostCtx.FallbackScreen) ? hostCtx.FallbackScreen : "Main";
        if (string.IsNullOrWhiteSpace(route))
            route = !string.IsNullOrWhiteSpace(hostCtx.FallbackRoute) ? hostCtx.FallbackRoute : "/";

        var rawId = !string.IsNullOrWhiteSpace(controlIdOverride)
            ? controlIdOverride!.Trim()
            : (!string.IsNullOrWhiteSpace(_tag) ? _tag!.Trim() : ResolveControlIdentifier());

        var controlId = SanitizeToken(rawId, fallback: ControlTypeName, maxLen: 96);
        var leaf = string.Equals(controlId, ControlTypeName, StringComparison.OrdinalIgnoreCase)
            ? ControlTypeName
            : $"{ControlTypeName}:{controlId}";

        string fullLocation;
        if (string.IsNullOrWhiteSpace(containerPath))
            fullLocation = $"{screen} > {leaf}";
        else if (containerPath.StartsWith(screen, StringComparison.OrdinalIgnoreCase))
            fullLocation = $"{containerPath} > {leaf}";
        else
            fullLocation = $"{screen} > {containerPath} > {leaf}";

        return (screen, route, fullLocation, controlId);
    }

    protected void RegisterControlPresence(string? controlIdOverride = null)
    {
        var (sink, _) = ResolveLoggingServices();
        if (sink is null || !sink.IsEnabled)
            return;

        var (screen, route, fullLocation, controlId) = ResolveControlLocation(controlIdOverride);
        sink.RegisterControlLocation(screen, route, fullLocation, ControlTypeName, controlId, _tag);
    }

    /// <summary>
    /// Updates this control's <see cref="Log"/> field immediately in memory and schedules an in-memory
    /// batch flush to <see cref="IFlexControlLogSink"/> at the end of the server turn (zero extra server trips).
    /// </summary>
    protected void RecordControlAction(
        string action,
        string? value = null,
        string? detail = null,
        long? durationMs = null,
        string? controlIdOverride = null)
    {
        if (string.IsNullOrWhiteSpace(action))
            return;

        var (sink, ctxProvider) = ResolveLoggingServices();
        var hostCtx = ctxProvider?.GetCurrentContext() ?? default;
        var (screen, route, fullLocation, controlId) = ResolveControlLocation(controlIdOverride);

        var record = new FlexControlLogRecord
        {
            TimestampUtc = DateTime.UtcNow,
            Application = !string.IsNullOrWhiteSpace(hostCtx.Application) ? hostCtx.Application : "FlexKitApp",
            Tenant = hostCtx.Tenant ?? string.Empty,
            DivisionId = hostCtx.DivisionId,
            UserId = !string.IsNullOrWhiteSpace(hostCtx.UserId) ? hostCtx.UserId : "anonymous",
            SessionId = hostCtx.SessionId ?? string.Empty,
            ClientIp = hostCtx.ClientIp,
            Screen = screen,
            Route = route,
            Location = fullLocation,
            ControlType = ControlTypeName,
            ControlId = controlId,
            Tag = string.IsNullOrWhiteSpace(_tag) ? null : _tag!.Trim(),
            Action = action.Trim(),
            Value = SanitizeValue(value),
            Detail = SanitizeValue(detail),
            DurationMs = durationMs
        };

        // 1. Always update the control's own Log field immediately; queue it for the sink only when an
        //    enabled sink will flush it (otherwise nothing would ever take it off the queue).
        var forSink = sink is not null && sink.IsEnabled;
        var queued = _log.Append(record, forSink);

        if (!forSink || sink is null)
            return;

        // A long burst in one turn (an expand-all over a large tree) goes to the sink now, not at the turn's end.
        if (queued >= FlexControlLog.MaxPendingRecords)
        {
            _log.FlushTo(sink);
            return;
        }

        // 2. Coalesce same-turn updates on this control and flush in memory without blocking or adding server trips.
        var syncCtx = SynchronizationContext.Current;
        if (syncCtx is null)
        {
            _log.FlushTo(sink);
        }
        else if (!_flushQueued)
        {
            _flushQueued = true;
            syncCtx.Post(static state =>
            {
                var (ctrl, targetSink) = ((FlexKitControlBase, IFlexControlLogSink))state!;
                ctrl._flushQueued = false;
                ctrl._log.FlushTo(targetSink);
            }, (this, sink));
        }
    }

    /// <summary>Explicitly flushes any pending entries in <see cref="Log"/> to the configured sink.</summary>
    public int FlushControlLog()
    {
        var (sink, _) = ResolveLoggingServices();
        _flushQueued = false;
        return _log.FlushTo(sink);
    }

    protected static string SanitizeToken(string? raw, string fallback, int maxLen = 96)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        var clean = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= maxLen ? clean : clean[..maxLen];
    }

    protected static string? SanitizeValue(string? raw, int maxLen = 256)
    {
        if (raw is null)
            return null;
        var clean = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (clean.Length == 0)
            return string.Empty;
        return clean.Length <= maxLen ? clean : clean[..(maxLen - 3)] + "...";
    }
}
