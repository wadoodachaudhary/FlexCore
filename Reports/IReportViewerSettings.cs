namespace Fx.ControlKit.Reports;

public interface IReportViewerSettings
{
    Task<int> GetZoomLevelAsync();

    Task SetZoomLevelAsync(int zoomLevel);
}

public sealed class InMemoryReportViewerSettings : IReportViewerSettings
{
    private int _zoom = 100;
    public Task<int> GetZoomLevelAsync() => Task.FromResult(_zoom);
    public Task SetZoomLevelAsync(int zoomLevel) { _zoom = zoomLevel; return Task.CompletedTask; }
}
