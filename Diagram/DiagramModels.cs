namespace Fx.ControlKit.Diagram;

public class DiagramNode
{
    public string Id { get; set; } = "";

    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public double Width { get; set; } = 140;

    public double Height { get; set; } = 50;

    public string Label { get; set; } = "";

    public string? SubLabel { get; set; }

    public DiagramControlShape Shape { get; set; } = DiagramControlShape.Rectangle;

    public string FillColor { get; set; } = "#4A90D9";

    public string StrokeColor { get; set; } = "#2C5F8A";

    public string TextColor { get; set; } = "#FFFFFF";

    public double StrokeWidth { get; set; } = 2;

    public double CornerRadius { get; set; } = 6;

    public string? NavigateUrl { get; set; }

    public string? CssClass { get; set; }

    public string? Tooltip { get; set; }

    public string? Icon { get; set; }

    public string? BackgroundImage { get; set; }

    public double BackgroundImageOpacity { get; set; } = 1.0;

    public ImageFit BackgroundImageFit { get; set; } = ImageFit.Contain;

    public double BackgroundImagePadding { get; set; } = 4;

    public bool Enabled { get; set; } = true;
}

public class DiagramConnector
{
    public string Id { get; set; } = "";

    public string SourceId { get; set; } = "";

    public string TargetId { get; set; } = "";

    public ConnectorType Type { get; set; } = ConnectorType.Orthogonal;

    public string StrokeColor { get; set; } = "#555555";

    public double StrokeWidth { get; set; } = 2;

    public string? Label { get; set; }

    public string LabelColor { get; set; } = "#333333";

    public bool ShowArrow { get; set; } = true;

    public string? DashArray { get; set; }

    public DiagramPort SourcePort { get; set; } = DiagramPort.Auto;

    public DiagramPort TargetPort { get; set; } = DiagramPort.Auto;

    public ArrowStyle ArrowStyle { get; set; } = ArrowStyle.Open;

    public bool IsBroken { get; set; } = false;
}

public enum ArrowStyle
{
    Open,
    Closed
}

public enum DiagramControlShape
{
    Rectangle,
    RoundedRectangle,
    Ellipse,
    Diamond,
    Parallelogram,
    Stadium
}

public enum ConnectorType
{
    Straight,
    Orthogonal
}

public enum DiagramPort
{
    Auto,
    Top,
    Bottom,
    Left,
    Right
}

public enum ImageFit
{
    Contain,
    Cover,
    Icon
}

public class DiagramNodeClickEventArgs
{
    public DiagramNode Node { get; set; } = default!;
    public bool Handled { get; set; }
}
