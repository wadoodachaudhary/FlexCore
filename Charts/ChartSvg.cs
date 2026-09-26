using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fx.ControlKit.Charts;

/// <summary>
/// SVG markup produced by <see cref="ChartControl"/>. Report pagination is synchronous and cannot
/// host a component, so it renders that same control here. There is no second chart geometry.
/// </summary>
public static class ChartSvg
{
    private static readonly object Gate = new();
    private static ServiceProvider? _services;
    private static HtmlRenderer? _renderer;

    public static string Markup(ChartType type, IReadOnlyList<ChartSeries> series, string? title = null, bool showLegend = true, string? width = "100%", string? height = "300px")
    {
        var parameters = new Dictionary<string, object?>
        {
            ["Type"] = type,
            ["Series"] = series.ToList(),
            ["Title"] = title ?? "",
            ["Legend"] = new ChartLegend { Visible = showLegend },
            ["Width"] = width,
            ["Height"] = height,
            ["Animate"] = false
        };
        lock (Gate)
        {
            var renderer = Renderer();
            return renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<ChartControl>(ParameterView.FromDictionary(parameters!));
                return root.ToHtmlString();
            }).GetAwaiter().GetResult();
        }
    }

    private static HtmlRenderer Renderer()
    {
        if (_renderer is not null) return _renderer;
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        _services = services.BuildServiceProvider();
        return _renderer = new HtmlRenderer(_services, NullLoggerFactory.Instance);
    }
}
