using System;

namespace Fx.ControlKit;

/// <summary>
/// Application-wide text zoom shared by every FlexKit control on the circuit.
/// PageControl publishes the factor as the --fx-zoom CSS variable on its root
/// element and the control stylesheets derive their font sizes from it, so text
/// scales while control dimensions stay fixed. Default is 100%.
/// Register scoped per circuit: builder.Services.AddScoped&lt;ZoomService&gt;();
/// </summary>
public class ZoomService
{
    public const int DefaultPercent = 100;
    public const int MinPercent = 50;
    public const int MaxPercent = 200;
    public const int StepPercent = 10;

    public int ZoomPercent { get; private set; } = DefaultPercent;

    /// <summary>Multiplier form of <see cref="ZoomPercent"/> (1.0 at 100%).</summary>
    public double ZoomFactor => ZoomPercent / 100.0;

    public event Action? OnZoomChanged;

    public bool CanZoomIn => ZoomPercent < MaxPercent;
    public bool CanZoomOut => ZoomPercent > MinPercent;

    public void ZoomIn() => SetZoom(ZoomPercent + StepPercent);
    public void ZoomOut() => SetZoom(ZoomPercent - StepPercent);
    public void Reset() => SetZoom(DefaultPercent);

    public void SetZoom(int percent)
    {
        var clamped = Math.Clamp(percent, MinPercent, MaxPercent);
        if (clamped == ZoomPercent)
            return;
        ZoomPercent = clamped;
        OnZoomChanged?.Invoke();
    }

    /// <summary>
    /// Inline style a zoom-scope ROOT publishes: the --fx-zoom variable that
    /// explicit calc() font rules consume, plus a scaled base font so text that
    /// merely inherits (labels, inputs normalized to font:inherit) scales too.
    /// Publish it on the outermost scope only — a nested scope inherits both,
    /// and re-declaring the em-based size there would compound the factor.
    /// </summary>
    public string ZoomScopeStyle =>
        $"--fx-zoom: {ZoomFactor.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}; font-size: calc(1em * var(--fx-zoom, 1))";
}
