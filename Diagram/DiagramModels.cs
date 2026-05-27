namespace Fx.ControlKit.Diagram;

/// <summary>
/// Represents a node (box) in the DiagramControl workflow.
/// </summary>
public class DiagramNode
{
    /// <summary>Unique identifier for this node.</summary>
    public string Id { get; set; } = "";

    /// <summary>Horizontal center position in pixels.</summary>
    public double OffsetX { get; set; }

    /// <summary>Vertical center position in pixels.</summary>
    public double OffsetY { get; set; }

    /// <summary>Width of the node box in pixels.</summary>
    public double Width { get; set; } = 140;

    /// <summary>Height of the node box in pixels.</summary>
    public double Height { get; set; } = 50;

    /// <summary>Primary text displayed inside the node.</summary>
    public string Label { get; set; } = "";

    /// <summary>Optional secondary/description text displayed below the label.</summary>
    public string? SubLabel { get; set; }

    /// <summary>Shape of the node.</summary>
    public DiagramControlShape Shape { get; set; } = DiagramControlShape.Rectangle;

    /// <summary>Fill/background color of the node.</summary>
    public string FillColor { get; set; } = "#4A90D9";

    /// <summary>Border/stroke color of the node.</summary>
    public string StrokeColor { get; set; } = "#2C5F8A";

    /// <summary>Text color inside the node.</summary>
    public string TextColor { get; set; } = "#FFFFFF";

    /// <summary>Border width in pixels.</summary>
    public double StrokeWidth { get; set; } = 2;

    /// <summary>Corner radius for rounded rectangles.</summary>
    public double CornerRadius { get; set; } = 6;

    /// <summary>URL path to navigate to when the node is clicked (e.g., "/my-page").</summary>
    public string? NavigateUrl { get; set; }

    /// <summary>CSS class appended to the node group.</summary>
    public string? CssClass { get; set; }

    /// <summary>Tooltip text shown on hover.</summary>
    public string? Tooltip { get; set; }

    /// <summary>Optional icon text (emoji or single char) displayed above the label.</summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Optional background image URL rendered inside the node shape.
    /// Supports any image path or URL (e.g., "/images/icons/settings.png", "https://...").
    /// The image is clipped to the node shape and rendered behind the label text.
    /// </summary>
    public string? BackgroundImage { get; set; }

    /// <summary>Opacity of the background image (0.0 – 1.0). Defaults to 1.0.</summary>
    public double BackgroundImageOpacity { get; set; } = 1.0;

    /// <summary>
    /// How the background image fits within the node.
    /// Cover = fill entire node (may crop). Contain = fit inside (may letterbox). Icon = centered at original/specified size.
    /// Defaults to Contain.
    /// </summary>
    public ImageFit BackgroundImageFit { get; set; } = ImageFit.Contain;

    /// <summary>Padding in pixels between the node edge and the background image. Defaults to 4.</summary>
    public double BackgroundImagePadding { get; set; } = 4;

    /// <summary>Whether the node is enabled for interaction.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Represents a connector (arrow) between two nodes.
/// </summary>
public class DiagramConnector
{
    /// <summary>Unique identifier for this connector.</summary>
    public string Id { get; set; } = "";

    /// <summary>Id of the source node.</summary>
    public string SourceId { get; set; } = "";

    /// <summary>Id of the target node.</summary>
    public string TargetId { get; set; } = "";

    /// <summary>Type of connector line.</summary>
    public ConnectorType Type { get; set; } = ConnectorType.Orthogonal;

    /// <summary>Stroke color of the connector line.</summary>
    public string StrokeColor { get; set; } = "#555555";

    /// <summary>Line width in pixels.</summary>
    public double StrokeWidth { get; set; } = 2;

    /// <summary>Optional label displayed on the connector.</summary>
    public string? Label { get; set; }

    /// <summary>Text color for the connector label.</summary>
    public string LabelColor { get; set; } = "#333333";

    /// <summary>Whether to show an arrowhead at the target end.</summary>
    public bool ShowArrow { get; set; } = true;

    /// <summary>Dash pattern (e.g., "5,3" for dashed lines). Null for solid.</summary>
    public string? DashArray { get; set; }

    /// <summary>Source connection port side.</summary>
    public DiagramPort SourcePort { get; set; } = DiagramPort.Auto;

    /// <summary>Target connection port side.</summary>
    public DiagramPort TargetPort { get; set; } = DiagramPort.Auto;
}

/// <summary>
/// Shape of a diagram node.
/// </summary>
public enum DiagramControlShape
{
    Rectangle,
    RoundedRectangle,
    Ellipse,
    Diamond,
    Parallelogram,
    Stadium
}

/// <summary>
/// Type of connector line routing.
/// </summary>
public enum ConnectorType
{
    Straight,
    Orthogonal
}

/// <summary>
/// Connection port side for connectors.
/// </summary>
public enum DiagramPort
{
    Auto,
    Top,
    Bottom,
    Left,
    Right
}

/// <summary>
/// Controls how a background image fits within a diagram node.
/// </summary>
public enum ImageFit
{
    /// <summary>Image scales to fit entirely within the node (may have padding). Default.</summary>
    Contain,
    /// <summary>Image scales to cover the entire node (may be cropped).</summary>
    Cover,
    /// <summary>Image is rendered at its natural size, centered within the node (no scaling).</summary>
    Icon
}

/// <summary>
/// Event args raised when a node is clicked.
/// </summary>
public class DiagramNodeClickEventArgs
{
    public DiagramNode Node { get; set; } = default!;
    public bool Handled { get; set; }
}
