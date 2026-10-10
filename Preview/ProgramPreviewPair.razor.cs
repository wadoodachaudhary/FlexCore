using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Preview;

/// <summary>
/// Source and target programs side by side. Start, restart, and stop apply to both.
/// Each window still reports its own status and error output.
/// </summary>
public partial class ProgramPreviewPair
{
    private readonly string _generatedOwner = Guid.NewGuid().ToString("N");
    private ProgramPreviewWindow? _source;
    private ProgramPreviewWindow? _target;
    private bool _busy;
    private bool _autoStarted;

    [Parameter] public ProgramLaunch? SourceLaunch { get; set; }
    [Parameter] public ProgramLaunch? TargetLaunch { get; set; }
    [Parameter] public string SourceLabel { get; set; } = "Source";
    [Parameter] public string TargetLabel { get; set; } = "Target";
    [Parameter] public string? OwnerKey { get; set; }
    [Parameter] public bool AutoStart { get; set; }
    [Parameter] public string StartText { get; set; } = "Start";
    [Parameter] public string RestartText { get; set; } = "Restart";
    [Parameter] public string StopText { get; set; } = "Stop";
    [Parameter] public string PairLabel { get; set; } = "Live program preview";

    private string EffectiveOwner => string.IsNullOrWhiteSpace(OwnerKey) ? _generatedOwner : OwnerKey!;

    protected override string? BaseCssClass => "fx-preview-pair";

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
            await Task.WhenAll(Start(_source, SourceLaunch), Start(_target, TargetLaunch));
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
            await Task.WhenAll(Restart(_source, SourceLaunch), Restart(_target, TargetLaunch));
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
        var tasks = new List<Task>(2);
        if (_source is not null) tasks.Add(_source.StopAsync());
        if (_target is not null) tasks.Add(_target.StopAsync());
        await Task.WhenAll(tasks);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Task Start(ProgramPreviewWindow? window, ProgramLaunch? launch)
        => window is null || launch is null ? Task.CompletedTask : window.StartAsync();

    private static Task Restart(ProgramPreviewWindow? window, ProgramLaunch? launch)
        => window is null || launch is null ? Task.CompletedTask : window.RestartAsync();
}
