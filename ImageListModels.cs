namespace Fx.ControlKit;

public enum ImageListSize { Small, Medium, Large }

public class ImageListItem
{
    public string? ImageUrl { get; set; }
    public string? IconCss { get; set; }
    public string? Caption { get; set; }
    public string? Tooltip { get; set; }
    public object? Tag { get; set; }

    public ImageListItem() { }
    public ImageListItem(string? imageUrl, string? caption = null)
    {
        ImageUrl = imageUrl;
        Caption = caption;
    }
}
