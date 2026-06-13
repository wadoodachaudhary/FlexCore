namespace Fx.ControlKit.Grid;

/// <summary>
/// Shared inline-style strings for the small bordered "+"/"−" expand/collapse
/// glyph used by both <c>GridControl</c> (group rows) and <c>TreeGridControl</c>
/// (per-node icons). Lives in a non-generic helper so .razor markup can
/// reference it without needing an explicit type-argument syntax (which
/// confuses the Razor parser when the consumer is itself generic).
///
/// Inline styles are used in preference to an external CSS rule because
/// <c>GridControl</c> emits the icon span via <c>RenderTreeBuilder</c>,
/// where Blazor's component-scoped CSS attribute is not reliably applied —
/// inline styles always win and guarantee the rectangle paints.
/// </summary>
public static class HfGridIconStyles
{
    /// <summary>Style for an active "+" / "−" expand/collapse box.</summary>
    public const string PlusMinus =
        "display:inline-flex;align-items:center;justify-content:center;" +
        "width:13px;height:13px;font-size:11px;line-height:1;" +
        "border:1px solid #8a8a8a;background:#fff;color:#555;" +
        "font-weight:400;border-radius:2px;box-sizing:border-box;" +
        "margin-right:6px;flex-shrink:0;user-select:none;cursor:pointer;";

    /// <summary>Invisible spacer of the same width — keeps leaf cells aligned with parent siblings.</summary>
    public const string LeafSpacer =
        "display:inline-block;width:14px;height:14px;margin-right:6px;flex-shrink:0;";
}
