# Legacy project intake and Web Forms conversion

FlexCore owns the reusable path from an uploaded legacy project archive to a Blazor app and a pair of preview launches. Mutarjim calls these types and does not reimplement them.

## Intake

```csharp
using Fx.ControlKit.Migration;

var extracted = LegacyProjectIntake.ExtractFile(archivePath, destination);
var inventory = LegacyProjectIntake.Inspect(extracted.Destination);
```

`Extract` rejects absolute paths, `..` segments, symbolic links, and archives that exceed `LegacyArchiveLimits` (100 MB compressed, 32 MB per entry, 5,000 entries, 200 MB extracted, by default).

`Inspect` reads `.sln`, `.csproj`, and `.vbproj` files plus the files beside them. A project is classified as Web Forms, WinForms, WPF, MVC, Web API, Razor Pages, Blazor, a class library, or a console app. The inventory lists pages, masters, user controls, WinForms forms, code-behind, models, data-access files, config, connection strings, app settings, seed files (CSV, SQL, initializers, preconfigured data, mock services), and static assets.

## Convert

```csharp
var conversion = WebFormsBlazorConverter.Convert(inventory, outputDirectory);
```

The converter targets `net10.0` unless `WebFormsConversionOptions.TargetFramework` says otherwise. It:

- Turns `.aspx`, `.ascx`, and `.master` markup into Razor components and a layout. Master content placeholders become `@Body`. The chrome text `(Web Forms)` becomes `(Blazor)`.
- Maps `GridView`, `ListView`, `Repeater`, `FormView`, `DetailsView`, `TextBox`, `DropDownList`, `Button`, `Label`, `Literal`, `HyperLink`, `Image`, `CheckBox`, and the common validators. `SelectMethod`, `Insert`/`Update`/`Delete` click handlers, and `ModelBinding`-style method names on the code-behind are carried into the component.
- Rewrites an EF6 `DbContext` to EF Core, and a service that holds that context so each call opens a context from `IDbContextFactory`.
- Replaces a SQL Server `NEXT VALUE FOR` HiLo helper with `MAX(Id)+1`, because SQLite has no sequences.
- Copies CSS, scripts, fonts, and images into `wwwroot`. Style bundles from `BundleConfig` become `<link>` tags. `Web.config` `appSettings` become `appsettings.json`.
- When the connection string is LocalDB or SQL Server, the generated app uses SQLite. Seed order follows `UseCustomizationData`: preconfigured data (the same source the mock service uses when that flag is false) or CSV files when the flag is true. `UseMockData=true` does not keep the catalog in memory; create, edit, and delete are persisted.
- Writes every gap to `MIGRATION.md` and returns it on `WebFormsConversionResult.Todos`. Nothing that failed to convert is dropped silently. VB code-behind is not translated.

`FLEXCORE_MIGRATION_CHECK=crud` on the eShop-shaped output creates, updates, and deletes one row, then exits. That is how the regression test proves the data path.

## Launch

```csharp
var legacy = LegacyAppLauncher.Plan(project, new LegacyLaunchOptions
{
    Mode = LegacyLaunchMode.Auto,
    RemoteUrl = "http://windows-vm:8080/"
});
var converted = ConvertedAppLauncher.Create(conversion);

var panes = new ProgramPreviewPane[]
{
    new() { Label = "Legacy", Chrome = ProgramPreviewChrome.Windows, Launch = legacy.Launch },
    new() { Label = "Blazor", Chrome = ProgramPreviewChrome.Browser, Launch = converted }
};
```

Both launches use `ProgramPreviewKind.Web`, so the preview host proxies them through `/_flexcore/program-preview/{sessionId}/app/{**path}` when it started the process, or shows an absolute remote URL directly. The Windows chrome is the frame around that web pane. It is not a WinForms host.

### What actually runs the legacy app

Mono `xsp4` does **not** render this class of app, and it is not installed on the Linux image used here (`apt` has no `mono-xsp` package). `LegacyAppLauncher` still knows how to start `xsp4 --port {port}` when the binary exists, but `RendersOnThisHost` stays false for ASP.NET 4.7.2 Web Forms that reference EF6, ship `packages.config`, and have no prebuilt `bin`. Reasons are on `LegacyLaunchPlan.Report`.

The path that works is a remote URL: IIS Express on a Windows VM (UTM on a Mac is the setup this was written for). Pass that address as `LegacyLaunchOptions.RemoteUrl`. Auto mode uses it whenever Mono cannot render. The preview does not start a process for that launch.

The converted Blazor app is the process the host starts: `dotnet` plus the built assembly, or `dotnet run` when the assembly is not built yet. `{port}` is filled by the preview host.

## Known gaps

- Web Forms script libraries (`WebForms.js`, validators, MsAjax) are not part of the Blazor page. `ScriptManager` is reported as a TODO.
- Autofac, log4net, Application Insights, session variables, and Friendly URLs view switching are not converted.
- SQL Server sequence scripts and `CatalogDBInitializer` are not executed. SQLite seeding replaces them.
- The converter covers the server controls listed above and the code-behind patterns in the eShop catalog (route data, list binding, click handlers, validators). Other Web Forms controls and arbitrary code-behind stay as TODO comments.
- VB projects are detected and refused by the converter rather than half-translated.
- `MapStaticAssets` is not used. Its precompressed endpoints returned empty bodies to browsers that send `Accept-Encoding: gzip`, which left the Blazor circuit unable to start. Static files, including `blazor.web.js`, are served by `UseStaticFiles`.
