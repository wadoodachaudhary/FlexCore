// Fx.ControlKit.Mdi.MdiWindow — descriptor for one open child window inside
// an <see cref="MdiHost"/> tab strip. The host doesn't own routing decisions;
// the consumer (FMain in HomeFront, FApp in a future client, …) builds the
// list, hands it to MdiHost, and reacts to OnActivate / OnCloseRequested
// callbacks.
//
// Why a class with a settable Title rather than a record:
//   The Title is updated at runtime via the per-window SetPageTitle cascading
//   value MdiHost provides — child pages call SetPageTitle("Lot 12 – Roof")
//   from OnParametersSet, and the tab bar must reflect that. Records with
//   init-only props would force the consumer to replace the entire window
//   instance on every title change, which would re-key the DynamicComponent
//   pane and tear down the child's state. A simple mutable Title sidesteps
//   that.

namespace Fx.ControlKit.Mdi;

public sealed class MdiWindow
{
    /// <summary>Immutable per-instance identifier. Used as the DynamicComponent
    /// <c>@key</c> so Blazor preserves the child component instance across
    /// tab activations. NEVER changes for the lifetime of the window; using
    /// e.g. the Route would re-mount the pane every time the user clicks
    /// "Open Model 12" again with different parameters.</summary>
    public string Id { get; }

    /// <summary>Last route this window resolved to (e.g. "/assembly/12/3"
    /// or "/assembly?community=X&amp;model=12"). Mutable on purpose: a
    /// child page that publishes its internal state to the URL — e.g.
    /// FAssembly.SyncAssemblyRouteState — calls back through the consumer's
    /// OpenOrFocus, which detects "same PageType as the active window" and
    /// updates Route in place. Without this, every state publish would
    /// look like a new tab. Used by consumers for singleton dedup and
    /// session-restore — MdiHost itself doesn't interpret it.</summary>
    public string Route { get; set; }

    /// <summary>The Blazor component type to render inside this tab's pane.
    /// Resolved from the consumer's route map.</summary>
    public Type PageType { get; }

    /// <summary>Parameters forwarded to the DynamicComponent. Same shape the
    /// consumer used pre-MDI (route segments parsed to {ParamName, value}).
    /// Query-string parameters are intentionally NOT included — child pages
    /// pick those up via [SupplyParameterFromQuery] directly.</summary>
    public IDictionary<string, object> Parameters { get; }

    /// <summary>Tab caption. Starts at the consumer's initial label; mutates
    /// when the child calls SetPageTitle. Mutable on purpose — see class
    /// comment above for why this isn't a record property.</summary>
    public string Title { get; set; }

    /// <summary>The live Blazor component instance rendered for this MDI
    /// window, when available. MdiHost sets this after render through the
    /// MdiPane wrapper so consumers can ask opt-in pages whether a close is
    /// allowed before removing the tab.</summary>
    public object? ComponentInstance { get; internal set; }

    /// <summary>True if this window represents a singleton page (About,
    /// Settings, FPriceList, …) — informational only; MdiHost doesn't use
    /// it. The consumer's OpenOrFocus logic reads it to decide whether
    /// re-opening the same route should focus this window or spawn a new
    /// one.</summary>
    public bool IsSingleton { get; }

    public MdiWindow(
        string id,
        string route,
        Type pageType,
        IDictionary<string, object> parameters,
        string title,
        bool isSingleton)
    {
        Id = id;
        Route = route;
        PageType = pageType;
        Parameters = parameters;
        Title = title;
        IsSingleton = isSingleton;
    }
}
