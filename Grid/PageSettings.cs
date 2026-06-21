namespace Fx.ControlKit.Grid;

public class PageSettings
{
    public int PageSize { get; set; } = 10;
    public int[] PageSizes { get; set; } = [5, 10, 20, 50];
    public int PageCount { get; set; } = 5;
}
