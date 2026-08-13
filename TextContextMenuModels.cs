namespace Fx.ControlKit;

[Flags]
public enum TextContextMenuItems
{
    None = 0,
    Undo = 1 << 0,
    Cut = 1 << 1,
    Copy = 1 << 2,
    Paste = 1 << 3,
    Delete = 1 << 4,
    SelectAll = 1 << 5,
    ReadingOrder = 1 << 6,
    UnicodeOptions = 1 << 7,
    Ime = 1 << 8,
    Reconversion = 1 << 9,

    Editing = Undo | Cut | Copy | Paste | Delete | SelectAll,
    Legacy = Editing | ReadingOrder | UnicodeOptions | Ime | Reconversion,
    All = Legacy
}

public sealed class TextContextMenuState
{
    public bool HasSelection { get; set; }
    public bool HasValue { get; set; }
    public bool AllSelected { get; set; }
    public bool IsRightToLeft { get; set; }
}

public sealed class TextContextMenuPosition
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class TextContextCommandResult
{
    public string? Value { get; set; }
    public bool ValueChanged { get; set; }
}
