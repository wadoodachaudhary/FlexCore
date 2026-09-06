using System.Diagnostics;
using EditorBrowser;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, EnvironmentName = "Development" });
builder.WebHost.UseStaticWebAssets();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddRazorComponents().AddInteractiveServerComponents(o => o.DetailedErrors = true);
await using var app = builder.Build();
// ASP.NET emits a relative CSS preload Link header. WebKit resolves that
// hint against the deep document route, unlike the actual stylesheet import.
// Root the test host's hint; keep loading and validating the real CSS asset.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Response.Headers.TryGetValue("Link", out var link))
            context.Response.Headers["Link"] = link.ToString().Replace("<_content/", "</_content/", StringComparison.Ordinal);
        return Task.CompletedTask;
    });
    await next(context);
});
app.UseStaticFiles(); app.UseAntiforgery(); app.MapStaticAssets();
app.MapRazorComponents<TestShell>().AddInteractiveServerRenderMode();
await app.StartAsync();
try
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    var start = new ProcessStartInfo("python3") { UseShellExecute = false };
    start.ArgumentList.Add(Path.Combine(Directory.GetCurrentDirectory(), "verify.py")); start.ArgumentList.Add(address);
    using var process = Process.Start(start)!;
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
    try { await process.WaitForExitAsync(timeout.Token); Environment.ExitCode = process.ExitCode; }
    catch { process.Kill(true); throw; }
}
finally { await app.StopAsync(); }
