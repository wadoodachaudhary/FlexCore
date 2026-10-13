using FlexCore.PreviewHarness.Components;
using Fx.ControlKit.Preview;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddFlexCoreProgramPreview(options =>
{
    options.FrameRoutePrefix = ProgramPreviewHttp.DefaultRoutePrefix;
    options.MaxConcurrentSessions = 8;
    options.MaxSessionsPerOwner = 8;
    options.DisplayWidth = 640;
    options.DisplayHeight = 420;
    options.FrameInterval = TimeSpan.FromMilliseconds(150);
});

var app = builder.Build();
app.UseWebSockets();
app.UseAntiforgery();
app.MapGet(ProgramPreviewHttp.DefaultRoutePrefix + "/{sessionId}/frame",
    async (string sessionId, string? token, IProgramPreviewHost host, HttpContext http) =>
    {
        var frame = host.ReadFrame(sessionId, token);
        http.Response.StatusCode = frame.StatusCode;
        http.Response.ContentType = frame.ContentType;
        http.Response.Headers.CacheControl = "no-store";
        await http.Response.Body.WriteAsync(frame.Body);
    });
app.Map(ProgramPreviewHttp.DefaultRoutePrefix + "/{sessionId}/app/{**path}", async (HttpContext http, string sessionId, IProgramPreviewHost host) =>
{
    var token = ProgramPreviewHttp.TokenFrom(http.Request.Query["token"], http.Request.Headers.Cookie);
    var prefix = ProgramPreviewHttp.AppPrefix(ProgramPreviewHttp.DefaultRoutePrefix, sessionId);
    var pathAndQuery = UpstreamPath(http.Request);
    if (http.WebSockets.IsWebSocketRequest)
    {
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        await ProgramPreviewProxy.ForwardWebSocketAsync(host, sessionId, token, pathAndQuery, socket, http.RequestAborted);
        return;
    }

    var headers = http.Request.Headers.Select(header =>
        new KeyValuePair<string, IEnumerable<string>>(header.Key, header.Value.Select(value => value ?? "").ToArray()));
    await ProgramPreviewProxy.ForwardHttpAsync(
        host,
        sessionId,
        token,
        http.Request.Method,
        pathAndQuery,
        headers,
        http.Request.Body,
        prefix,
        http.Response.Body,
        (status, responseHeaders) =>
        {
            http.Response.StatusCode = status;
            foreach (var header in responseHeaders)
                http.Response.Headers.Append(header.Key, header.Value.ToArray());
        },
        http.RequestAborted);
});
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

static string UpstreamPath(HttpRequest request)
{
    var query = QueryString.Empty;
    foreach (var pair in request.Query)
    {
        if (pair.Key.Equals("token", StringComparison.OrdinalIgnoreCase)) continue;
        foreach (var value in pair.Value)
            query = query.Add(pair.Key, value ?? "");
    }
    return (request.Path.Value ?? "/") + query.Value;
}
