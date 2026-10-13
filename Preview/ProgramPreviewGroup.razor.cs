using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Preview;

/// <summary>
/// Any number of previews with one start, restart, and stop toolbar.
/// Each pane keeps its own kind, status, and error output.
/// </summary>
public partial class ProgramPreviewGroup
{
    private readonly string _generatedOwner = Guid.NewGuid().ToString("N");
    private ProgramPreviewWindow[] _windows = [];
    private bool _busy;
    private bool _autoStarted;

    [Parameter] public IReadOnlyList<ProgramPreviewPane> Items { get; set; } = [];
    [Parameter] public string? OwnerKey { get; set; }
    [Parameter] public bool AutoStart { get; set; }
    [Parameter] public string StartText { get; set; } = "Start";
    [Parameter] public string RestartText { get; set; } = "Restart";
    [Parameter] public string StopText { get; set; } = "Stop";
    [Parameter] public string GroupLabel { get; set; } = "Live program preview";

    /// <summary>Frame used for panes that leave <see cref="ProgramPreviewPane.Chrome"/> null, and for the group itself.</summary>
    [Parameter] public ProgramPreviewChrome Chrome { get; set; } = ProgramPreviewChrome.Default;

    private string EffectiveOwner => string.IsNullOrWhiteSpace(OwnerKey) ? _generatedOwner : OwnerKey!;
    private string ChromeCss => Chrome switch
    {
        ProgramPreviewChrome.Windows => "windows",
        ProgramPreviewChrome.Fusion => "fusion",
        ProgramPreviewChrome.Browser => "browser",
        ProgramPreviewChrome.None => "none",
        _ => "default"
    };

    protected override string? BaseCssClass => "fx-preview-group fx-preview-chrome-" + ChromeCss;

    protected override void OnParametersSet()
    {
        var count = Items?.Count ?? 0;
        if (_windows.Length != count)
            _windows = new ProgramPreviewWindow[count];
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _autoStarted || !AutoStart) return;
        _autoStarted = true;
        await StartAsync();
    }

    public async Task StartAsync()
    {
        if (_busy || !Enabled) return;
        _busy = true;
        try
        {
            await Task.WhenAll(Commands(window => window.StartAsync()));
        }
        finally
        {
            _busy = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    public async Task RestartAsync()
    {
        if (_busy || !Enabled) return;
        _busy = true;
        try
        {
            await Task.WhenAll(Commands(window => window.RestartAsync()));
        }
        finally
        {
            _busy = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    public async Task StopAsync()
    {
        if (!Enabled) return;
        await Task.WhenAll(Commands(window => window.StopAsync()));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private IEnumerable<Task> Commands(Func<ProgramPreviewWindow, Task> command)
    {
        for (var index = 0; index < _windows.Length; index++)
        {
            var window = _windows[index];
            if (window is null || Items is null || index >= Items.Count || Items[index].Launch is null)
                continue;
            yield return command(window);
        }
    }
}
