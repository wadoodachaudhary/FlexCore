using System.Collections.Generic;

namespace Fx.ControlKit.Editor;

/// <summary>
/// The kind of a block-level element rendered by <see cref="EditorControl"/>.
/// Paragraphs are the default body unit; headings cue structural breaks and
/// are rendered with an <c>&lt;h2&gt;</c> / <c>&lt;h3&gt;</c> tag instead of
/// a <c>&lt;p&gt;</c>.
/// </summary>
public enum EditorBlockKind
{
    Paragraph,
    ChapterHeading,
    SectionHeading,
    /// <summary>
    /// A picture block — rendered as a <c>&lt;figure&gt;</c> containing an
    /// <c>&lt;img&gt;</c>. Use <see cref="EditorBlock.ImageSrc"/> for the
    /// source (typically a data URL); <see cref="EditorBlock.Text"/> doubles
    /// as alt text. The block is non-editable but selectable and deletable.
    /// </summary>
    Image,
    /// <summary>
    /// A forced page break. Renders as a non-editable visual divider in Web
    /// view and as an invisible "end this page here" marker in paged views —
    /// the paginator closes the current page and starts the next block on a
    /// fresh card. Carries no text or image data; the block-id is the only
    /// stable identifier the host needs.
    /// </summary>
    PageBreak
}

/// <summary>
/// A single top-level block rendered by the editor. <see cref="Text"/> may
/// contain the library's allowlisted inline HTML tags (<c>&lt;b&gt;</c>,
/// <c>&lt;i&gt;</c>, <c>&lt;u&gt;</c>, <c>&lt;s&gt;</c>, <c>&lt;strong&gt;</c>,
/// <c>&lt;em&gt;</c>, and <c>&lt;span style="color|font-family|font-size"&gt;</c>).
/// <see cref="Alignment"/> and <see cref="LineHeight"/> are optional block-
/// level styling applied as <c>data-align</c> and <c>style="line-height:…"</c>.
/// <see cref="Html"/> lets the host pre-render a richer HTML form of the
/// block — e.g. wrapping host-specific markup (prompt directives, inline
/// annotations) in styleable spans — while <see cref="Text"/> retains the
/// raw, round-trippable source. When set, the JS init path uses
/// <c>Html</c> directly instead of escaping <c>Text</c>.
/// </summary>
public sealed record EditorBlock(
    string Id,
    EditorBlockKind Kind,
    string Text,
    string? Alignment = null,
    string? LineHeight = null,
    string? Html = null,
    string? ImageSrc = null);

/// <summary>
/// A snapshot of the active browser selection inside the editor, returned by
/// <see cref="EditorControl.GetSelectionAsync"/>. Coordinates are relative to
/// the editor's positioned container, so the caller can pin a floating
/// toolbar directly at <c>(Left, Top)</c> without further arithmetic.
/// </summary>
public sealed record EditorSelectionInfo(
    string Text,
    string BlockId,
    double Top,
    double Left,
    double Width,
    double Bottom);

/// <summary>
/// Caret position as a (block-id, character offset) pair — enough for the
/// host to persist and restore the last-edited cursor location across
/// document reloads or chapter navigation.
/// </summary>
public sealed record EditorCaretInfo(string BlockId, int Offset);

/// <summary>
/// Page-width preset that controls the max-width of the editor's writing
/// surface. Rendered as a CSS class on the editor container
/// (<c>fx-pw-narrow</c>, <c>fx-pw-normal</c>, <c>fx-pw-wide</c>, <c>fx-pw-full</c>).
/// </summary>
public enum EditorPageWidth
{
    Narrow,
    Normal,
    Wide,
    Full
}

/// <summary>
/// Paragraph alignment applied as a <c>data-align</c> attribute on the block
/// element. CSS maps each value to a <c>text-align</c> rule.
/// </summary>
public enum EditorAlignment
{
    Left,
    Center,
    Right,
    Justify
}
