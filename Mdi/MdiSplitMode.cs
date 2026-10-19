namespace Fx.ControlKit.Mdi;

/// <summary>
/// Layout arrangement for displaying open MDI windows in <see cref="MdiHost"/>.
/// </summary>
public enum MdiSplitMode
{
    /// <summary>Single active tab fills the workspace.</summary>
    None = 0,

    /// <summary>Two tabs displayed side by side (left and right panes).</summary>
    SideBySide = 1,

    /// <summary>Two tabs displayed one below another (top and bottom panes).</summary>
    Stacked = 2
}
