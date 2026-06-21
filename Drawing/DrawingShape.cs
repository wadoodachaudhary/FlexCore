using System.Collections.Generic;

namespace Fx.ControlKit.Drawing;

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
    Text
}

public class DrawingShape
{
    public string Id { get; set; } = string.Empty;
    public DrawingTool Kind { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public string Color { get; set; } = "#ff0000";
    public double StrokeWidth { get; set; } = 3;

    public string Text { get; set; } = string.Empty;

    public List<double[]> Points { get; set; } = new();

    public double Left => Width >= 0 ? X : X + Width;
    public double Top => Height >= 0 ? Y : Y + Height;
    public double AbsWidth => System.Math.Abs(Width);
    public double AbsHeight => System.Math.Abs(Height);

    public double Radius => System.Math.Min(AbsWidth, AbsHeight) / 2;
    public double Cx => Left + AbsWidth / 2;
    public double Cy => Top + AbsHeight / 2;

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

    public double LocalX1 => X - Left;
    public double LocalY1 => Y - Top;
    public double LocalX2 => X + Width - Left;
    public double LocalY2 => Y + Height - Top;
}
