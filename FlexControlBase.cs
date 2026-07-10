using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit;

public abstract class FlexControlBase : ComponentBase
{
    [Parameter] public string? CssClass { get; set; }

    [Parameter] public string? Style { get; set; }

    [Parameter] public bool Visible { get; set; } = true;

    [Parameter] public bool Enabled { get; set; } = true;

    [Parameter] public string? Id { get; set; }

    [Parameter] public string? Title { get; set; }

    [Parameter] public string? Tag { get; set; }

    [Parameter] public string? Data { get; set; }

    [Parameter] public FlexControlBase? Parent { get; set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public Dictionary<string, object>? AdditionalAttributes { get; set; }

    protected virtual string? BaseCssClass => null;

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
            var normalized = userStyle.EndsWith(';') ? userStyle : $"{userStyle};";
            return $"{normalized} {hide}";
        }
    }
}
