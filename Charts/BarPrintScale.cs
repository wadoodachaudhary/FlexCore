namespace Fx.ControlKit.Charts;

internal static class BarPrintScale
{
    internal static (double Minimum, double Maximum, int Ticks) Range(double minimum, double maximum, int targetIntervals = 6)
    {
        var interval = (maximum - minimum) / targetIntervals;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(interval)));
        var fraction = interval / magnitude;
        var step = (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 4 ? 4 : fraction <= 5 ? 5 : 10) * magnitude;
        var start = Math.Floor(minimum / step) * step;
        var end = Math.Ceiling(maximum / step) * step;
        return (start, end, 1 + (int)Math.Round((end - start) / step));
    }
}
