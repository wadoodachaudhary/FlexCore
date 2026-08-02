using System.Collections.Generic;

namespace Fx.ControlKit.Drawing;

/// <summary>
/// The drawing tools / shape kinds offered by <c>DrawingControl</c>. <see cref="Select"/> is the
/// pointer (select / move / edit) mode; <see cref="Crop"/> drags a region to keep (replacing the
/// background image); every other value is a creatable shape. Freehand is deliberately absent —
/// irregular outlines are drawn with <see cref="Polygon"/> (click vertices) so the whole surface
/// stays pure-Blazor (no real-time canvas JS).
/// </summary>
public enum DrawingTool
{
    Select,
    Crop,
    Rectangle,
    RectangleText,
    Ellipse,
    EllipseText,
    Circle,
    CircleText,
    Line,
    LineText,
    Arrow,
    ArrowText,
    Polygon,
    PolygonText,
    Text,
    // An image laid over the background (e.g. superimpose a second screenshot). Placed by dragging
    // a box (aspect-locked); carries its source in DrawingShape.ImageHref. Appended LAST so existing
    // serialised Kind values (stored as ints) are unchanged.
    Image,
    // Freehand pen — a continuous stroke captured into Points on pointer-move (no JS), rendered as an
    // open round-joined polyline (same as Polygon). Appended LAST to keep serialised int values stable.
    Pen
}

/// <summary>
/// One annotation object on a <c>DrawingControl</c> surface. Plain serialisable data — the visual
/// is rendered by the matching shape control (RectangleControl / EllipseControl / …) and the PNG
/// export is composed from this same data in C#. Coordinates are in image pixels (the surface is
/// rendered 1:1 with the background screenshot, so no scaling math is needed anywhere).
/// </summary>
public class DrawingShape
{
    public string Id { get; set; } = string.Empty;
    public DrawingTool Kind { get; set; }

    // Bounding box (image px). For Line/Arrow, (X,Y)→(X+W,Y+H) is the segment (W/H may be negative).
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public string Color { get; set; } = "#ff0000";
    public double StrokeWidth { get; set; } = 3;

    // Bubble / label text (RectangleText, ArrowText, Text).
    public string Text { get; set; } = string.Empty;

    // Polygon vertices (image px), used only when Kind == Polygon.
    public List<double[]> Points { get; set; } = new();

    // Source of an overlay image (data URL or web url), used only when Kind == Image. Rendered into
    // the box (Left,Top,AbsWidth,AbsHeight) on screen and composed into the exported PNG.
    public string ImageHref { get; set; } = string.Empty;

    public double Left => Width >= 0 ? X : X + Width;
    public double Top => Height >= 0 ? Y : Y + Height;
    public double AbsWidth => System.Math.Abs(Width);
    public double AbsHeight => System.Math.Abs(Height);

    // Circle radius (inscribed in the bounding box) + centre, for the Circle kind.
    public double Radius => System.Math.Min(AbsWidth, AbsHeight) / 2;
    public double Cx => Left + AbsWidth / 2;
    public double Cy => Top + AbsHeight / 2;

    // *Text tools carry a clickable bubble; the bubble + edit are drawn by DrawingControl so
    // EVERY geometry gets a text counterpart uniformly. BaseKind is the geometry to render.
    public bool HasBubble => Kind is DrawingTool.RectangleText or DrawingTool.EllipseText
        or DrawingTool.CircleText or DrawingTool.LineText or DrawingTool.ArrowText or DrawingTool.PolygonText;
    public bool HasText => HasBubble || Kind == DrawingTool.Text;
    public bool BubbleAtStart => BaseKind is DrawingTool.Line or DrawingTool.Arrow;  // bubble at segment start
    public DrawingTool BaseKind => Kind switch
    {
        DrawingTool.RectangleText => DrawingTool.Rectangle,
        DrawingTool.EllipseText => DrawingTool.Ellipse,
        DrawingTool.CircleText => DrawingTool.Circle,
        DrawingTool.LineText => DrawingTool.Line,
        DrawingTool.ArrowText => DrawingTool.Arrow,
        DrawingTool.PolygonText => DrawingTool.Polygon,
        _ => Kind
    };

    // Segment endpoints expressed RELATIVE to the (normalised) bounding box — used by the
    // self-contained Line/Arrow/ArrowText SVGs whose box origin is (Left, Top).
    public double LocalX1 => X - Left;
    public double LocalY1 => Y - Top;
    public double LocalX2 => X + Width - Left;
    public double LocalY2 => Y + Height - Top;
}
