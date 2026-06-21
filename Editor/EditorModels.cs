using System.Collections.Generic;

namespace Fx.ControlKit.Editor;

public enum EditorBlockKind
{
    Paragraph,
    ChapterHeading,
    SectionHeading,
    Image,
    PageBreak
}

public sealed record EditorBlock(
    string Id,
    EditorBlockKind Kind,
    string Text,
    string? Alignment = null,
    string? LineHeight = null,
    string? Html = null,
    string? ImageSrc = null);

public sealed record EditorSelectionInfo(
    string Text,
    string BlockId,
    double Top,
    double Left,
    double Width,
    double Bottom);

public sealed record EditorCaretInfo(string BlockId, int Offset);

public enum EditorPageWidth
{
    Narrow,
    Normal,
    Wide,
    Full
}

public enum EditorAlignment
{
    Left,
    Center,
    Right,
    Justify
}
