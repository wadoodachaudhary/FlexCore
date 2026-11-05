using FlexCore.ShellSample;
using FlexCore.ShellSample.Components;
using Fx.ControlKit.Llm;
using Fx.ControlKit.Llm.CodeTools;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<ILlmClient, SampleLlmClient>();
builder.Services.AddFlexCoreCodeTools(configure: options => options.Model = "openai:sample");

var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
