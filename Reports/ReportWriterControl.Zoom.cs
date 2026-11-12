using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Fx.ControlKit.Reports;

public partial class ReportWriterControl
{
    // Mode belongs to the tab; effective fit scale belongs to the visible viewport.
    private sealed class ZoomView(int level)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public int Level { get; set; } = level;
        public string? StatusBeforeValidation { get; set; }
        public double Scale { get; set; } = level > 0 ? level / 100d : 1;
    }

    private readonly ConditionalWeakTable<ReportTab, ZoomView> _zoomViews = new();
    private int _defaultZoomLevel = 100;
    private int _zoomInputVersion;
    private const string ZoomValidationMessage = "Enter a zoom from 25% to 400%, or select Fit Width or Fit Page.";
    private ZoomView? CurrentZoom => ActiveTab is { } tab ? _zoomViews.GetValue(tab, _ => new(_defaultZoomLevel)) : null;
    private int ZoomLevel
    {
        get => CurrentZoom?.Level ?? _defaultZoomLevel;
        set { if (CurrentZoom is { } zoom) zoom.Level = NormalizeZoom(value); else _defaultZoomLevel = NormalizeZoom(value); }
    }
    private double ZoomScale => ZoomLevel > 0 ? ZoomLevel / 100d : CurrentZoom?.Scale ?? 1;
    private string ZoomToken => CurrentZoom is { } zoom ? $"{zoom.Id}:{CurrentPage}" : "";
    private string ZoomText => ZoomLevel switch { -1 => "Fit Width", 0 => "Fit Page", _ => $"{ZoomLevel}%" };
    private string ZoomStatus => ZoomLevel > 0 ? ZoomText : $"{ZoomText} ({ZoomScale * 100:0}%)";
    private static int NormalizeZoom(int level) => level is -1 or 0 or (>= 25 and <= 400) ? level : 100;
    private sealed record ReportZoomChoice(string Text);
    private static readonly int[] ZoomSteps = [25, 50, 75, 100, 125, 150, 200, 300, 400];
    private static readonly ReportZoomChoice[] ZoomChoices = [new("Fit Width"), new("Fit Page"), .. ZoomSteps.Select(n => new ReportZoomChoice($"{n}%"))];

    private async Task ChangeZoomTextAsync(string? text)
    {
        var input = text?.Trim() ?? "";
        int level;
        if (input.Equals("Fit Width", StringComparison.OrdinalIgnoreCase)) level = -1;
        else if (input.Equals("Fit Page", StringComparison.OrdinalIgnoreCase)) level = 0;
        else if (!int.TryParse(input.EndsWith('%') ? input[..^1].Trim() : input, NumberStyles.None, CultureInfo.InvariantCulture, out level)
            || level is < 25 or > 400)
        {
            if (CurrentZoom is { } view && StatusMessage != ZoomValidationMessage) view.StatusBeforeValidation = StatusMessage;
            StatusMessage = ZoomValidationMessage;
            _zoomInputVersion++;
            return;
        }
        ZoomLevel = level;
        await ChromeOnZoomChanged();
    }

    private async Task ChromeOnZoomChanged()
    {
        _defaultZoomLevel = ZoomLevel;
        if (CurrentZoom is { StatusBeforeValidation: { } previous } view)
        {
            if (StatusMessage == ZoomValidationMessage) StatusMessage = previous;
            view.StatusBeforeValidation = null;
        }
        try { await ViewerSettings.SetZoomLevelAsync(ZoomLevel); }
        catch (Exception error) { Logger.LogWarning(error, "Could not save report zoom preference"); }
    }

    private async Task ChromeOnZoomIn()
    {
        ZoomLevel = ZoomSteps.FirstOrDefault(n => n > Math.Round(ZoomScale * 100, 4), 400);
        await ChromeOnZoomChanged();
    }

    private async Task ChromeOnZoomOut()
    {
        ZoomLevel = ZoomSteps.LastOrDefault(n => n < Math.Round(ZoomScale * 100, 4), 25);
        await ChromeOnZoomChanged();
    }

    private async Task UpdateViewerZoomAsync()
    {
        if (_dotNetRef is null) return;
        var module = await GetReportJsModuleAsync();
        if (module is null) return; // Static/headless rendering retains the unscaled page.
        try
        {
            if (ShowChrome && ActiveTab is not null)
                await module.InvokeVoidAsync("updateViewerZoom", _reportRootElement, _dotNetRef, ZoomToken, ZoomLevel);
            else
                await module.InvokeVoidAsync("detachViewerZoom", _reportRootElement);
        }
        catch (JSDisconnectedException) { }
    }

    [JSInvokable]
    public Task OnViewerZoomMeasured(string token, int level, double scale)
    {
        if (token != ZoomToken || level != ZoomLevel || !double.IsFinite(scale) || scale <= 0 || CurrentZoom is not { } zoom)
            return Task.CompletedTask;
        if (Math.Abs(zoom.Scale - scale) < .0001) return Task.CompletedTask;
        zoom.Scale = scale;
        return InvokeAsync(StateHasChanged);
    }
}
