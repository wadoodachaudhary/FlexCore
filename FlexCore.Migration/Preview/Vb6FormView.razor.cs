using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Preview;

/// <summary>
/// Draws a parsed VB6 form. The host owns the text, checks and button behavior
/// so a fallback pane can run the original calculation without a Windows VM.
/// </summary>
public partial class Vb6FormView
{
    [Parameter] public Vb6FormLayout? Layout { get; set; }

    /// <summary>Current text by control name. Missing names use the designer text.</summary>
    [Parameter] public IReadOnlyDictionary<string, string>? Texts { get; set; }

    [Parameter] public EventCallback<(string Name, string Value)> TextChanged { get; set; }

    /// <summary>Checked state by control name. Missing names use the designer value (1 = checked).</summary>
    [Parameter] public IReadOnlyDictionary<string, bool>? Checks { get; set; }

    [Parameter] public EventCallback<(string Name, bool Value)> CheckChanged { get; set; }

    [Parameter] public EventCallback<string> ButtonClick { get; set; }

    /// <summary>Label rendered as the form heading. Defaults to the first label.</summary>
    [Parameter] public string TitleName { get; set; } = "";

    private string WindowStyle => Layout is null ? "" : "width:" + (Layout.ClientWidthPixels + 8) + "px";
    private string ClientStyle => Layout is null ? "" : "width:" + Layout.ClientWidthPixels + "px;height:" + Layout.ClientHeightPixels + "px";

    private string TextOf(Vb6FormControl control)
        => Texts is not null && Texts.TryGetValue(control.Name, out var text) ? text : control.Text;

    private bool Checked(Vb6FormControl control)
        => Checks is not null && Checks.TryGetValue(control.Name, out var value) ? value : control.Value == 1;

    private string Shown(Vb6FormControl control)
        => Texts is not null && Texts.TryGetValue(control.Name, out var text) ? text : control.Caption;

    private Task OnText(string name, ChangeEventArgs change)
        => TextChanged.InvokeAsync((name, change.Value?.ToString() ?? ""));

    private Task OnCheck(string name, ChangeEventArgs change)
        => CheckChanged.InvokeAsync((name, change.Value is true));

    private static string Box(Vb6FormControl control)
    {
        var left = control.Left / Vb6FormLayout.TwipsPerPixel;
        var top = control.Top / Vb6FormLayout.TwipsPerPixel;
        var width = Math.Max(8, control.Width / Vb6FormLayout.TwipsPerPixel);
        var height = Math.Max(8, control.Height / Vb6FormLayout.TwipsPerPixel);
        return FormattableString.Invariant($"left:{left}px;top:{top}px;width:{width}px;height:{height}px");
    }
}
