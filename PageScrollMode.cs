namespace Fx.ControlKit;

/// <summary>
/// How a <see cref="PageControl"/> handles content that does not fit the space
/// it is given — e.g. after a browser zoom, a text-zoom step, or a window
/// resize that leaves the page painted wider or taller than the window.
/// </summary>
public enum PageScrollMode
{
    /// <summary>Scrollbars appear per axis only when that axis overflows (default).</summary>
    Auto,

    /// <summary>No scroll container; overflow is left entirely to the host layout.</summary>
    None,

    /// <summary>Horizontal scrollbar when needed; vertical overflow is clipped.</summary>
    Horizontal,

    /// <summary>Vertical scrollbar when needed; horizontal overflow is clipped.</summary>
    Vertical,

    /// <summary>Both scrollbars always visible, whether or not content overflows.</summary>
    Both
}
