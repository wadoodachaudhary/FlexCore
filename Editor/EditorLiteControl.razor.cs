using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Fx.ControlKit.Editor;

public partial class EditorLiteControl : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter] public string? EditorId { get; set; }
    [Parameter] public string Value { get; set; } = string.Empty;
    [Parameter] public EventCallback<string> ValueChanged { get; set; }
    [Parameter] public EventCallback<EditorLiteValue> Changed { get; set; }
    [Parameter] public string Placeholder { get; set; } = "Type here...";
    [Parameter] public string? CssClass { get; set; }
    [Parameter] public string? Height { get; set; }
    [Parameter] public string MinHeight { get; set; } = "160px";
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public bool SpellCheck { get; set; } = true;
    [Parameter] public bool ShowToolbar { get; set; } = true;
    [Parameter] public string ToolbarLabel { get; set; } = "Editor formatting";

    private readonly string _generatedEditorId = $"fx-editor-lite-{Guid.NewGuid():N}";
    private IJSObjectReference? _module;
    private bool _pendingPush = true;
    private bool _jsReady;
    private int _editorKey;
    private string _lastPushedValue = string.Empty;
    private string _selectedBlock = "P";

    private string EffectiveEditorId => string.IsNullOrWhiteSpace(EditorId) ? _generatedEditorId : EditorId!;

    private string ContainerStyle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Height))
                parts.Add($"height:{Height}");
            if (!string.IsNullOrWhiteSpace(MinHeight))
                parts.Add($"--fx-editor-lite-min-height:{MinHeight}");
            return string.Join(';', parts);
        }
    }

    protected override void OnParametersSet()
    {
        if (!string.Equals(Value ?? string.Empty, _lastPushedValue, StringComparison.Ordinal))
            _pendingPush = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
            await EnsureScriptAsync();

        if (_pendingPush && _jsReady)
        {
            _pendingPush = false;
            await SetHtmlAsync(Value ?? string.Empty);
        }
    }

    public async Task SetHtmlAsync(string html)
    {
        await EnsureScriptAsync();
        _lastPushedValue = html ?? string.Empty;
        Value = _lastPushedValue;
        await JS.InvokeVoidAsync("fxEditorLite.setHtml", EffectiveEditorId, _lastPushedValue);
    }

    public async Task<string> GetHtmlAsync()
    {
        await EnsureScriptAsync();
        return await JS.InvokeAsync<string>("fxEditorLite.getHtml", EffectiveEditorId);
    }

    public async Task<EditorLiteValue> ReadAsync()
    {
        await EnsureScriptAsync();
        return await JS.InvokeAsync<EditorLiteValue>("fxEditorLite.read", EffectiveEditorId);
    }

    public async Task<string> ReadJsonAsync(bool indented = false)
    {
        var value = await ReadAsync();
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = indented });
    }

    public async Task<string> ReadFormatAsync(EditorLiteOutputFormat format)
    {
        var value = await ReadAsync();
        return format switch
        {
            EditorLiteOutputFormat.Json => JsonSerializer.Serialize(value),
            EditorLiteOutputFormat.Html => value.Html,
            EditorLiteOutputFormat.PlainText => value.Text,
            EditorLiteOutputFormat.Markdown => value.Markdown,
            EditorLiteOutputFormat.JiraText => value.JiraText,
            EditorLiteOutputFormat.JiraDocumentJson => value.JiraDocumentJson,
            EditorLiteOutputFormat.Rtf => value.Rtf,
            EditorLiteOutputFormat.OpenDocumentHtml => value.OpenDocumentHtml,
            _ => value.Text
        };
    }

    public async Task FocusAsync()
    {
        await EnsureScriptAsync();
        await JS.InvokeVoidAsync("fxEditorLite.focus", EffectiveEditorId);
    }

    public async Task ResetAsync(string html = "")
    {
        _editorKey++;
        _pendingPush = true;
        _lastPushedValue = html;
        await SetHtmlAsync(html);
    }

    private async Task ExecAsync(string command)
    {
        await EnsureScriptAsync();
        await JS.InvokeVoidAsync("fxEditorLite.exec", EffectiveEditorId, command);
        await NotifyChangedAsync();
    }

    private async Task CreateLinkAsync()
    {
        await EnsureScriptAsync();
        await JS.InvokeVoidAsync("fxEditorLite.createLink", EffectiveEditorId);
        await NotifyChangedAsync();
    }

    private async Task HandleBlockChangedAsync(ChangeEventArgs args)
    {
        _selectedBlock = args.Value?.ToString() ?? "P";
        await EnsureScriptAsync();
        await JS.InvokeVoidAsync("fxEditorLite.formatBlock", EffectiveEditorId, _selectedBlock);
        await NotifyChangedAsync();
    }

    private async Task HandleInputAsync()
    {
        await NotifyChangedAsync();
    }

    private async Task NotifyChangedAsync()
    {
        var value = await ReadAsync();
        _lastPushedValue = value.Html;
        Value = value.Html;

        if (ValueChanged.HasDelegate)
            await ValueChanged.InvokeAsync(value.Html);
        if (Changed.HasDelegate)
            await Changed.InvokeAsync(value);
    }

    private async Task EnsureScriptAsync()
    {
        if (_jsReady)
            return;

        _module ??= await JS.InvokeAsync<IJSObjectReference>(
            "import",
            $"./_content/{typeof(EditorLiteControl).Assembly.GetName().Name}/editor-control.js");
        _jsReady = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
            await _module.DisposeAsync();
    }
}
