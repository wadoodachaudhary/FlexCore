using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Container for TreeGridColumn definitions. Children are rendered hidden and register
/// themselves with the parent TreeGridControl via CascadingParameter.
/// </summary>
public class TreeGridColumns : ComponentBase
{
    [Parameter] public RenderFragment? ChildContent { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.AddContent(0, ChildContent);
    }
}
