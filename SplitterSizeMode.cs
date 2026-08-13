namespace Fx.ControlKit;

public enum SplitterSizeMode
{
    /// <summary>Primary pane size is in pixels and is unchanged by a container resize.</summary>
    Pixel,
    /// <summary>Primary pane size is a percentage of the container, so the panes keep their
    /// ratio as the container grows or shrinks.</summary>
    Proportional
}
