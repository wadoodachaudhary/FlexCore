namespace Fx.ControlKit.Editor;

/// <summary>
/// Named output formats produced by <see cref="EditorLiteControl"/>.
/// </summary>
public enum EditorLiteOutputFormat
{
    Json,
    Html,
    PlainText,
    Markdown,
    JiraText,
    JiraDocumentJson,
    Rtf,
    OpenDocumentHtml
}

/// <summary>
/// A complete readback snapshot from <see cref="EditorLiteControl"/>.
/// </summary>
public sealed class EditorLiteValue
{
    public string Schema { get; set; } = "fx-editor-lite/1";
    public string Html { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Markdown { get; set; } = string.Empty;
    public string JiraText { get; set; } = string.Empty;
    public string JiraDocumentJson { get; set; } = string.Empty;
    public string Rtf { get; set; } = string.Empty;
    public string OpenDocumentHtml { get; set; } = string.Empty;
}
