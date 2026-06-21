
namespace Fx.ControlKit.Mdi;

public sealed class MdiWindow
{
    public string Id { get; }

    public string Route { get; set; }

    public Type PageType { get; }

    public IDictionary<string, object> Parameters { get; }

    public string Title { get; set; }

    public object? ComponentInstance { get; internal set; }

    public bool IsSingleton { get; }

    public MdiWindow(
        string id,
        string route,
        Type pageType,
        IDictionary<string, object> parameters,
        string title,
        bool isSingleton)
    {
        Id = id;
        Route = route;
        PageType = pageType;
        Parameters = parameters;
        Title = title;
        IsSingleton = isSingleton;
    }
}
