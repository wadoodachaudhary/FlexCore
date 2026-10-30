using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Fx.ControlKit.Conversion;

/// <summary>
/// Plain-text editor with a line-number gutter. Used by <see cref="ConverterShell"/>
/// for the source and converted panes.
/// </summary>
public partial class ConverterEditor
{
    public const int MaxGutterLines = 10000;

    [Parameter] public string Value { get; set; } = "";
    [Parameter] public EventCallback<string> ValueChanged { get; set; }
    [Parameter] public string Placeholder { get; set; } = "";
    [Parameter] public string AriaLabel { get; set; } = "Code";
    [Parameter] public bool ReadOnly { get; set; }
    [Parameter] public bool Enabled { get; set; } = true;
    [Parameter] public bool ShowExpand { get; set; } = true;
    [Parameter] public bool Expanded { get; set; }
    [Parameter] public string ExpandText { get; set; } = "Expand";
    [Parameter] public EventCallback OnExpand { get; set; }
    [Parameter] public string EditorHeight { get; set; } = "320px";

    private ElementReference _root;
    private IJSObjectReference? _module;
    private IJSObjectReference? _binding;

    public int LineCount => CountLines(Value);
    private int ShownLines => Math.Min(LineCount, MaxGutterLines);
    private string InputStyle => string.IsNullOrWhiteSpace(EditorHeight) ? "" : $"height:{EditorHeight}";

    /// <summary>Counts logical lines. An empty value is one line, matching an empty editor gutter.</summary>
    public static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        var count = 1;
        foreach (var ch in text)
        {
            if (ch == '\n') count++;
        }
        return count;
    }

    private async Task OnInput(ChangeEventArgs args)
    {
        if (ReadOnly || !Enabled) return;
        var next = args.Value?.ToString() ?? "";
        if (string.Equals(next, Value, StringComparison.Ordinal)) return;
        Value = next;
        await ValueChanged.InvokeAsync(next);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        try
        {
            _module ??= await JS.InvokeAsync<IJSObjectReference>(
                "import",
                $"./_content/{typeof(ConverterEditor).Assembly.GetName().Name}/converter-shell.js");
            if (_module is null) return;
            _binding = await _module.InvokeAsync<IJSObjectReference>("bindEditor", _root);
        }
        catch
        {
            // The gutter still renders without scroll sync when the script is unavailable.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeBindingAsync(_binding);
        await DisposeBindingAsync(_module);
        _binding = null;
        _module = null;
    }

    private static async Task DisposeBindingAsync(IJSObjectReference? reference)
    {
        if (reference is null) return;
        try { await reference.DisposeAsync(); }
        catch { /* already gone */ }
    }
}
