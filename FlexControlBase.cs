using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit;

/// <summary>
/// Base class for all FlexKit controls. Provides common parameters shared
/// across all controls: CSS class, inline style, visibility, enabled state,
/// DOM id, and unmatched attribute capture.
/// </summary>
public abstract class FlexControlBase : ComponentBase
{
    /// <summary>Additional CSS classes appended after the control's base class.</summary>
    [Parameter] public string? CssClass { get; set; }

    /// <summary>Inline style applied to the root element.</summary>
    [Parameter] public string? Style { get; set; }

    /// <summary>Controls visibility. When false, adds display:none to the resolved style.</summary>
    [Parameter] public bool Visible { get; set; } = true;

    /// <summary>Controls the enabled/disabled state of the control.</summary>
    [Parameter] public bool Enabled { get; set; } = true;

    /// <summary>Optional DOM id for the root element.</summary>
    [Parameter] public string? Id { get; set; }

    /// <summary>Captures any unmatched HTML attributes passed to the component.</summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>
    /// Override in subclasses to define the root CSS class (e.g. "fx-button").
    /// </summary>
    protected virtual string? BaseCssClass => null;

    /// <summary>
    /// Combines <see cref="BaseCssClass"/> with <see cref="CssClass"/> into a
    /// single class string suitable for the root element.
    /// </summary>
    protected string? ResolvedCssClass
    {
        get
        {
            var baseClass = BaseCssClass;
            var extra = CssClass?.Trim();
            if (string.IsNullOrEmpty(baseClass) && string.IsNullOrEmpty(extra))
                return null;
            if (string.IsNullOrEmpty(baseClass))
                return extra;
            if (string.IsNullOrEmpty(extra))
                return baseClass;
            return $"{baseClass} {extra}";
        }
    }

    /// <summary>
    /// Combines <see cref="Style"/> with "display:none" when <see cref="Visible"/> is false.
    /// </summary>
    protected string? ResolvedStyle
    {
        get
        {
            var hide = !Visible ? "display:none" : null;
            var userStyle = Style?.Trim();
            if (string.IsNullOrEmpty(hide) && string.IsNullOrEmpty(userStyle))
                return null;
            if (string.IsNullOrEmpty(hide))
                return userStyle;
            if (string.IsNullOrEmpty(userStyle))
                return hide;
            // Ensure the user style ends with semicolon before appending
            var normalized = userStyle.EndsWith(';') ? userStyle : $"{userStyle};";
            return $"{normalized} {hide}";
        }
    }
}
