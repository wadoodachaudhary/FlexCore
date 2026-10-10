using FlexCore.PreviewHarness.Components;
using Fx.ControlKit.Preview;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddFlexCoreProgramPreview(options =>
{
    options.FrameRoutePrefix = ProgramPreviewHttp.DefaultRoutePrefix;
    options.DisplayWidth = 640;
    options.DisplayHeight = 420;
    options.FrameInterval = TimeSpan.FromMilliseconds(150);
});

var app = builder.Build();
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
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
