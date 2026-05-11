using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Fx.ControlKit.Editor;

/// <summary>
/// A contenteditable block editor with a clean C# API. The editor owns its
/// DOM content via JS interop — <see cref="Blocks"/> seeds the initial state,
/// and the host reads the current state back with <see cref="ReadBlocksAsync"/>
/// (typically right before saving).
///
/// Supported inline formatting: bold / italic / underline / strike through
/// <see cref="ExecFormatCommandAsync"/>; color, font family, font size, and
/// font weight on the current selection through
/// <see cref="ApplyInlineStyleAsync"/>; block-level alignment and line-height
/// through <see cref="SetBlockAlignmentAsync"/> and
/// <see cref="ApplyBlockStyleAsync"/>.
///
/// Selection is captured via <see cref="GetSelectionAsync"/> (for floating
/// toolbars) and caret position via <see cref="GetCaretAsync"/> /
/// <see cref="SetCaretAsync"/> (for host-driven persistence).
/// </summary>
public partial class EditorControl : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    /// <summary>
    /// DOM id of the contenteditable element. Must be unique on the page —
    /// the library's JS helpers use <c>document.getElementById</c> to locate
    /// the editor.
    /// </summary>
    [Parameter] public string EditorId { get; set; } = "fx-editor";

    /// <summary>
    /// Seed content. Pushed into the DOM after each render when the reference
    /// has changed; the editor owns the DOM from that point forward.
    /// </summary>
    [Parameter] public IReadOnlyList<EditorBlock> Blocks { get; set; } = Array.Empty<EditorBlock>();

    /// <summary>
    /// When true, the editor lays its blocks out as a CSS Grid of page cards
    /// (<c>&lt;div class="fx-page"&gt;</c> wrappers, marker class
    /// <c>.fx-paged</c> on the editor root). JS does the actual pagination in
    /// editor-control.js: it measures the rendered height of each block
    /// against the page card's content area and packs as many blocks as fit,
    /// splitting paragraphs mid-text at word boundaries when a single block
    /// is too tall to fit on its own page. No content is ever dropped.
    /// <para>The editor is still a single contenteditable, so cursor and
    /// selection flow naturally between pages — pages are presentation only.
    /// Re-pagination after the user types is host-driven: call
    /// <see cref="PushAsync"/> (or trigger a parameter change) to re-measure.</para>
    /// </summary>
    [Parameter] public bool Paginate { get; set; }

    /// <summary>
    /// Extra CSS class applied to the editor root when <see cref="Paginate"/>
    /// is true — the host uses this to drive per-format page sizing through
    /// CSS variables (<c>paged-paperback</c>, <c>paged-hardcover</c>, …).
    /// </summary>
    [Parameter] public string? PagedCssClass { get; set; }

    /// <summary>
    /// Fires after every user edit so the host can capture dirty state. The
    /// parameter is the snapshot read back from the DOM via the sanitizer.
    /// </summary>
    [Parameter] public EventCallback<IReadOnlyList<EditorBlock>> OnBlocksChanged { get; set; }

    /// <summary>
    /// Fires on every <c>mouseup</c> inside the editor with the resolved
    /// selection (or <c>null</c> when the user didn't actually select text).
    /// </summary>
    [Parameter] public EventCallback<EditorSelectionInfo?> OnSelectionChanged { get; set; }

    [Parameter] public EditorPageWidth PageWidth { get; set; } = EditorPageWidth.Normal;
    [Parameter] public string FontFamily { get; set; } = "Merriweather";
    [Parameter] public int FontSizePx { get; set; } = 18;
    [Parameter] public double LineHeight { get; set; } = 1.7;
    [Parameter] public string? CssClass { get; set; }

    /// <summary>
    /// When true (the default), every parameter change that swaps the
    /// <see cref="Blocks"/> reference causes the DOM to be re-populated from
    /// scratch. Hosts that want to merge external changes without clobbering
    /// user edits should set this to <c>false</c> and call
    /// <see cref="PushAsync"/> explicitly.
    /// </summary>
    [Parameter] public bool AutoPush { get; set; } = true;

    private int _editorKey;
    private bool _pendingPush;
    private IReadOnlyList<EditorBlock> _lastPushed = Array.Empty<EditorBlock>();
    private bool _lastPaginatePushed;
    private string? _lastPagedCssClassPushed;

    private string PageWidthClass => PageWidth.ToString().ToLowerInvariant();

    private string EditorStyle
    {
        get
        {
            var ci = CultureInfo.InvariantCulture;
            return $"font-family: '{FontFamily}', Georgia, serif;" +
                   $" font-size: {FontSizePx}px;" +
                   $" line-height: {LineHeight.ToString(ci)};";
        }
    }

    protected override void OnParametersSet()
    {
        // Blocks change → only auto-push when AutoPush is on. Hosts that manage
        // content updates manually (GhostWriter does this — see PushCurrentChapter
        // ToEditorAsync) opt out so they can sync DOM edits back to the model
        // before the next push, otherwise typing-in-flight gets clobbered.
        if (AutoPush && !ReferenceEquals(Blocks, _lastPushed))
        {
            _pendingPush = true;
        }

        // Pagination toggles → ALWAYS re-init, regardless of AutoPush. Paginate
        // and PagedCssClass are presentation/layout choices; toggling Web ↔
        // paged or switching between paged formats means the .fx-paged class
        // and the <div class="fx-page"> wrappers need to materialise (or
        // disappear), and that only happens through a JS init pass. Skipping
        // it would leave the editor visually identical to its previous mode —
        // symptom: "switching to Paperback only changes the background".
        if (Paginate != _lastPaginatePushed ||
            !string.Equals(PagedCssClass, _lastPagedCssClassPushed, StringComparison.Ordinal))
        {
            _pendingPush = true;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_pendingPush)
        {
            _pendingPush = false;
            await PushAsync();
        }
    }

    /// <summary>
    /// Replaces the editor's contents. When <paramref name="blocks"/> is
    /// supplied, it overrides <see cref="Blocks"/> for this push — useful
    /// when the host just mutated its backing collection and wants to push
    /// the new state immediately, without waiting for the next render cycle
    /// to propagate the <see cref="Blocks"/> parameter through Blazor.
    /// </summary>
    public async Task PushAsync(IReadOnlyList<EditorBlock>? blocks = null)
    {
        var source = blocks ?? Blocks;
        var dtos = source.Select(b => new
        {
            id = b.Id,
            kind = b.Kind.ToString(),
            text = b.Text,
            html = b.Html ?? string.Empty,
            alignment = b.Alignment ?? string.Empty,
            lineHeight = b.LineHeight ?? string.Empty,
            imageSrc = b.ImageSrc ?? string.Empty
        }).ToArray();

        // Pagination: when Paginate is true, JS lays the blocks out as a CSS
        // grid of fixed-size page cards using its own measurement-based
        // splitter (greedy fit + word-boundary mid-paragraph splits). The C#
        // side just signals "go paginated", JS owns the slicing because only
        // it can measure rendered heights against the per-format card size.
        await JS.InvokeVoidAsync("hfEditor.init", EditorId, dtos,
            new
            {
                paged = Paginate,
                pagedCssClass = PagedCssClass ?? string.Empty
            });
        _lastPushed = source;
        _lastPaginatePushed = Paginate;
        _lastPagedCssClassPushed = PagedCssClass;
    }

    /// <summary>
    /// Reads the editor DOM back as a list of <see cref="EditorBlock"/>s.
    /// Inline formatting on spans (color, font-family, font-size) plus
    /// <c>&lt;b&gt;</c>/<c>&lt;i&gt;</c>/<c>&lt;u&gt;</c>/<c>&lt;s&gt;</c>/<c>&lt;strong&gt;</c>/<c>&lt;em&gt;</c>
    /// is preserved; everything else is flattened to text.
    /// </summary>
    public async Task<IReadOnlyList<EditorBlock>> ReadBlocksAsync()
    {
        var raw = await JS.InvokeAsync<List<JsBlock>>("hfEditor.read", EditorId);
        return raw.Select(r => new EditorBlock(
            r.Id ?? string.Empty,
            ParseKind(r.Kind),
            r.Text ?? string.Empty,
            string.IsNullOrWhiteSpace(r.Alignment) ? null : r.Alignment,
            string.IsNullOrWhiteSpace(r.LineHeight) ? null : r.LineHeight,
            ImageSrc: string.IsNullOrWhiteSpace(r.ImageSrc) ? null : r.ImageSrc))
            .ToList();
    }

    /// <summary>
    /// True when the user has typed/pasted/deleted anything since the last
    /// <see cref="PushAsync"/>. Hosts use this to skip expensive save
    /// roundtrips on read-only chapter navigation. Defaults to <c>true</c>
    /// when the probe itself throws so the save path still runs defensively.
    /// </summary>
    public async Task<bool> IsDirtyAsync()
    {
        try { return await JS.InvokeAsync<bool>("hfEditor.isDirty", EditorId); }
        catch { return true; }
    }

    /// <summary>Returns the current selection, or <c>null</c> when collapsed.</summary>
    public async Task<EditorSelectionInfo?> GetSelectionAsync()
    {
        var dto = await JS.InvokeAsync<JsSelection?>("hfEditor.getSelection", EditorId);
        if (dto is null || string.IsNullOrWhiteSpace(dto.Text)) return null;
        return new EditorSelectionInfo(
            dto.Text.Trim(),
            dto.BlockId ?? string.Empty,
            dto.Top, dto.Left, dto.Width, dto.Bottom);
    }

    /// <summary>Selects every top-level block in the editor.</summary>
    public Task SelectAllAsync() => JS.InvokeVoidAsync("hfEditor.selectAll", EditorId).AsTask();

    /// <summary>Clears the live browser selection owned by the editor.</summary>
    public Task ClearSelectionAsync() => JS.InvokeVoidAsync("hfEditor.clearSelection", EditorId).AsTask();

    /// <summary>Runs a contenteditable exec-command (bold/italic/underline/strikeThrough).</summary>
    public Task ExecFormatCommandAsync(string command) =>
        JS.InvokeVoidAsync("hfEditor.execCommand", EditorId, command).AsTask();

    /// <summary>
    /// Wraps the current selection in a <c>&lt;span style="property: value"&gt;</c>.
    /// Supported properties: <c>color</c>, <c>font-family</c>,
    /// <c>font-size</c>, and <c>font-weight</c>.
    /// </summary>
    public Task ApplyInlineStyleAsync(string property, string value) =>
        JS.InvokeVoidAsync("hfEditor.applyInlineStyle", EditorId, property, value).AsTask();

    /// <summary>
    /// Applies a heading-like inline preset to the current selection using the
    /// supplied font family and font size, plus bold weight by default.
    /// Hosts use this for Word-style H1/H2 actions without needing to manage
    /// the individual selection-preserving style calls themselves.
    /// </summary>
    public async Task ApplyHeadingStyleAsync(string fontFamily, int fontSizePx, bool bold = true)
    {
        if (!string.IsNullOrWhiteSpace(fontFamily))
        {
            await ApplyInlineStyleAsync("font-family", fontFamily);
        }

        if (fontSizePx > 0)
        {
            await ApplyInlineStyleAsync("font-size", $"{fontSizePx}px");
        }

        if (bold)
        {
            await ApplyInlineStyleAsync("font-weight", "700");
        }
    }

    /// <summary>
    /// Sets a block-level inline style (today: <c>line-height</c>) on every
    /// top-level block the selection intersects.
    /// </summary>
    public Task ApplyBlockStyleAsync(string property, string value) =>
        JS.InvokeVoidAsync("hfEditor.applyBlockStyle", EditorId, property, value).AsTask();

    /// <summary>Sets paragraph alignment on the intersecting blocks.</summary>
    public Task SetBlockAlignmentAsync(EditorAlignment alignment) =>
        JS.InvokeVoidAsync("hfEditor.setBlockAlignment", EditorId, alignment.ToString().ToLowerInvariant()).AsTask();

    /// <summary>Returns the caret as a (block-id, offset) pair.</summary>
    public async Task<EditorCaretInfo?> GetCaretAsync()
    {
        var dto = await JS.InvokeAsync<JsCaret?>("hfEditor.getCaret", EditorId);
        if (dto is null || string.IsNullOrWhiteSpace(dto.BlockId)) return null;
        return new EditorCaretInfo(dto.BlockId, dto.Offset);
    }

    /// <summary>Moves the caret to the given position.</summary>
    public Task SetCaretAsync(string blockId, int offset) =>
        JS.InvokeVoidAsync("hfEditor.setCaret", blockId, offset).AsTask();

    /// <summary>Scrolls a block into view (no-op if the id isn't present).</summary>
    public Task ScrollToBlockAsync(string blockId) =>
        JS.InvokeVoidAsync("hfEditor.scrollToBlock", blockId).AsTask();

    /// <summary>Scrolls the editor's owning viewport back to the top.</summary>
    public Task ScrollToTopAsync() =>
        JS.InvokeVoidAsync("hfEditor.scrollToTop", EditorId).AsTask();

    /// <summary>
    /// Forces the keyed root to remount on the next render, destroying the
    /// current DOM and triggering a fresh <see cref="PushAsync"/>.
    /// </summary>
    public void ResetEditorKey()
    {
        _editorKey++;
        _pendingPush = true;
    }

    private async Task HandleMouseUpAsync()
    {
        if (!OnSelectionChanged.HasDelegate) return;
        await Task.Delay(10);
        var sel = await GetSelectionAsync();
        await OnSelectionChanged.InvokeAsync(sel);
    }

    /// <summary>
    /// Fires after every contenteditable input event (typed character, paste,
    /// delete, drag-drop). The host listens to this to schedule debounced
    /// typing-undo snapshots — without this hook, only LLM-applied / format
    /// edits ever get into the undo stack and Ctrl+Z does nothing for plain
    /// typing.
    /// <para>
    /// We pass an empty list rather than calling <see cref="ReadBlocksAsync"/>
    /// per keystroke to avoid a DOM-readback round-trip on every key (which
    /// would be O(N) per key for an N-block chapter). The host typically
    /// wants the *signal* "user edited something", not the actual blocks —
    /// it can pull them via <see cref="ReadBlocksAsync"/> on its own when
    /// the snapshot timer fires.
    /// </para>
    /// </summary>
    private async Task HandleInputAsync()
    {
        if (!OnBlocksChanged.HasDelegate) return;
        await OnBlocksChanged.InvokeAsync(Array.Empty<EditorBlock>());
    }

    private static EditorBlockKind ParseKind(string? kind) => kind switch
    {
        "ChapterHeading" => EditorBlockKind.ChapterHeading,
        "SectionHeading" => EditorBlockKind.SectionHeading,
        "Image" => EditorBlockKind.Image,
        _ => EditorBlockKind.Paragraph
    };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ─── JSON interop DTOs ─────────────────────────────────────────────────
    private sealed class JsBlock
    {
        public string? Id { get; set; }
        public string? Kind { get; set; }
        public string? Text { get; set; }
        public string? Alignment { get; set; }
        public string? LineHeight { get; set; }
        public string? ImageSrc { get; set; }
    }

    private sealed class JsSelection
    {
        public string? Text { get; set; }
        public string? BlockId { get; set; }
        public double Top { get; set; }
        public double Left { get; set; }
        public double Width { get; set; }
        public double Bottom { get; set; }
    }

    private sealed class JsCaret
    {
        public string? BlockId { get; set; }
        public int Offset { get; set; }
    }
}
