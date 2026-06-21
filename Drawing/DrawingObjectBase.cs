using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Drawing;

public abstract class DrawingObjectBase : FlexControlBase
{
    [Parameter, EditorRequired] public DrawingShape Shape { get; set; } = new();

    [Parameter] public bool Selected { get; set; }

    protected string BoxStyle =>
        $"position:absolute; left:{Px(Shape.Left)}; top:{Px(Shape.Top)}; " +
        $"width:{Px(Shape.AbsWidth)}; height:{Px(Shape.AbsHeight)}; pointer-events:none;";

    protected string SelStyle => Selected ? " outline:1px dashed #316ac5; outline-offset:2px;" : "";

    protected string BubbleStyle(double left, double top) =>
        $"position:absolute; left:{Px(left)}; top:{Px(top)}; pointer-events:none; " +
        $"background:#ffffe0; border:1.5px solid {Shape.Color}; border-radius:5px; padding:2px 6px; " +
        $"font:600 13px 'Segoe UI',Tahoma,sans-serif; white-space:pre; max-width:340px; " +
        $"color:{(string.IsNullOrWhiteSpace(Shape.Text) ? "#999" : "#222")};";

    protected static string Px(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px";
    protected static string Num(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
