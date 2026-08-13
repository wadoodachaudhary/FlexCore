using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Drawing;

/// <summary>
/// Base class for every drawing object rendered on a <c>DrawingControl</c> surface
/// (RectangleControl, RectangleTextObject, EllipseControl, LineControl, ArrowControl,
/// ArrowTextObject, TextControl, PolygonControl). Inherits the common FlexKit control surface
/// (<see cref="FlexControlBase"/>: CssClass / Style / Visible / Id …) and adds the shared shape
/// state plus geometry helpers. The objects are rendered as pointer-transparent overlays; all
/// pointer interaction is owned by the host <c>DrawingControl</c>, so subclasses are purely
/// presentational and need no event wiring of their own.
/// </summary>
public abstract class DrawingObjectBase : FlexControlBase
{
    /// <summary>The shape data this control renders.</summary>
    [Parameter, EditorRequired] public DrawingShape Shape { get; set; } = new();

    /// <summary>When true, the object draws its selection outline / handles.</summary>
    [Parameter] public bool Selected { get; set; }

    // Absolute-position style for the object's bounding box (image-pixel coordinates; the surface
    // is rendered 1:1, so these map straight to CSS px). Shared by all box-style subclasses.
    protected string BoxStyle =>
        $"position:absolute; left:{Px(Shape.Left)}; top:{Px(Shape.Top)}; " +
        $"width:{Px(Shape.AbsWidth)}; height:{Px(Shape.AbsHeight)}; pointer-events:none;";

    // Selection outline, appended inline so it works without any cross-component scoped CSS.
    protected string SelStyle => Selected ? " outline:1px dashed #316ac5; outline-offset:2px;" : "";

    // Full inline style for a text-bubble callout at (left, top). Yellow sticky-note fill, the
    // shape's colour as the border; grey placeholder text until the user types.
    protected string BubbleStyle(double left, double top) =>
        $"position:absolute; left:{Px(left)}; top:{Px(top)}; pointer-events:none; " +
        $"background:#ffffe0; border:1.5px solid {Shape.Color}; border-radius:5px; padding:2px 6px; " +
        $"font:600 13px 'Segoe UI',Tahoma,sans-serif; white-space:pre; max-width:340px; " +
        $"color:{(string.IsNullOrWhiteSpace(Shape.Text) ? "#999" : "#222")};";

    protected static string Px(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px";
    protected static string Num(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
