namespace Fx.ControlKit.Charts;
internal static class TimelineScale
{
    internal static (double Left, double Width) Bar(double start, double end, double minimum, double maximum, double width)
    {
        var span = Math.Max(double.Epsilon, maximum - minimum);
        var left = (start - minimum) / span * width;
        return (left, Math.Max(2, (end - start) / span * width));
    }
}
