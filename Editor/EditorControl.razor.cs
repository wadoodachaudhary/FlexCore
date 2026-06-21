using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Fx.ControlKit.Editor;

public partial class EditorControl : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter] public string EditorId { get; set; } = "fx-editor";

    [Parameter] public IReadOnlyList<EditorBlock> Blocks { get; set; } = Array.Empty<EditorBlock>();

    [Parameter] public bool Paginate { get; set; }

    [Parameter] public string? PagedCssClass { get; set; }

    [Parameter] public EventCallback<IReadOnlyList<EditorBlock>> OnBlocksChanged { get; set; }

    [Parameter] public EventCallback<EditorSelectionInfo?> OnSelectionChanged { get; set; }

    [Parameter] public EditorPageWidth PageWidth { get; set; } = EditorPageWidth.Normal;
    [Parameter] public string FontFamily { get; set; } = "Merriweather";
    [Parameter] public int FontSizePx { get; set; } = 18;
    [Parameter] public double LineHeight { get; set; } = 1.7;
    [Parameter] public string? CssClass { get; set; }

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
        if (AutoPush && !ReferenceEquals(Blocks, _lastPushed))
        {
            _pendingPush = true;
        }

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

        await JS.InvokeVoidAsync("fxEditor.init", EditorId, dtos,
            new
            {
                paged = Paginate,
                pagedCssClass = PagedCssClass ?? string.Empty
            });
        _lastPushed = source;
        _lastPaginatePushed = Paginate;
        _lastPagedCssClassPushed = PagedCssClass;
    }

    public async Task<IReadOnlyList<EditorBlock>> ReadBlocksAsync()
    {
        var raw = await JS.InvokeAsync<List<JsBlock>>("fxEditor.read", EditorId);
        return raw.Select(r => new EditorBlock(
            r.Id ?? string.Empty,
            ParseKind(r.Kind),
            r.Text ?? string.Empty,
            string.IsNullOrWhiteSpace(r.Alignment) ? null : r.Alignment,
            string.IsNullOrWhiteSpace(r.LineHeight) ? null : r.LineHeight,
            ImageSrc: string.IsNullOrWhiteSpace(r.ImageSrc) ? null : r.ImageSrc))
            .ToList();
    }

    public async Task<bool> IsDirtyAsync()
    {
        try { return await JS.InvokeAsync<bool>("fxEditor.isDirty", EditorId); }
        catch { return true; }
    }

    public async Task<EditorSelectionInfo?> GetSelectionAsync()
    {
        var dto = await JS.InvokeAsync<JsSelection?>("fxEditor.getSelection", EditorId);
        if (dto is null || string.IsNullOrWhiteSpace(dto.Text)) return null;
        return new EditorSelectionInfo(
            dto.Text.Trim(),
            dto.BlockId ?? string.Empty,
            dto.Top, dto.Left, dto.Width, dto.Bottom);
    }

    public Task SelectAllAsync() => JS.InvokeVoidAsync("fxEditor.selectAll", EditorId).AsTask();

    public Task ClearSelectionAsync() => JS.InvokeVoidAsync("fxEditor.clearSelection", EditorId).AsTask();

    public Task ExecFormatCommandAsync(string command) =>
        JS.InvokeVoidAsync("fxEditor.execCommand", EditorId, command).AsTask();

    public Task ApplyInlineStyleAsync(string property, string value) =>
        JS.InvokeVoidAsync("fxEditor.applyInlineStyle", EditorId, property, value).AsTask();

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

    public Task ApplyBlockStyleAsync(string property, string value) =>
        JS.InvokeVoidAsync("fxEditor.applyBlockStyle", EditorId, property, value).AsTask();

    public Task SetBlockAlignmentAsync(EditorAlignment alignment) =>
        JS.InvokeVoidAsync("fxEditor.setBlockAlignment", EditorId, alignment.ToString().ToLowerInvariant()).AsTask();

    public async Task<EditorCaretInfo?> GetCaretAsync()
    {
        var dto = await JS.InvokeAsync<JsCaret?>("fxEditor.getCaret", EditorId);
        if (dto is null || string.IsNullOrWhiteSpace(dto.BlockId)) return null;
        return new EditorCaretInfo(dto.BlockId, dto.Offset);
    }

    public Task SetCaretAsync(string blockId, int offset) =>
        JS.InvokeVoidAsync("fxEditor.setCaret", blockId, offset).AsTask();

    public Task ScrollToBlockAsync(string blockId) =>
        JS.InvokeVoidAsync("fxEditor.scrollToBlock", blockId).AsTask();

    public Task ScrollToTopAsync() =>
        JS.InvokeVoidAsync("fxEditor.scrollToTop", EditorId).AsTask();

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
