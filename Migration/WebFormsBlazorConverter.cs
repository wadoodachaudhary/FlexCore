using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.Migration;

/// <summary>
/// Converts a Web Forms project described by <see cref="LegacyProjectIntake"/> into a Blazor web app.
/// Mutarjim calls <see cref="Convert(LegacyProjectInventory, string, WebFormsConversionOptions?)"/>.
/// </summary>
public static class WebFormsBlazorConverter
{
    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".js", ".map", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".webp",
        ".woff", ".woff2", ".ttf", ".eot", ".otf", ".csv", ".zip"
    };

    public static WebFormsConversionResult Convert(LegacyProjectInventory inventory, string outputDirectory, WebFormsConversionOptions? options = null)
    {
        if (inventory is null) throw new ArgumentNullException(nameof(inventory));
        var project = inventory.PrimaryWebFormsProject;
        if (project is null)
        {
            return new WebFormsConversionResult
            {
                Succeeded = false,
                Report = "No Web Forms project was found. WinForms, WPF, MVC, and the other detected kinds are inventoried but not converted by this converter."
            };
        }
        return Convert(project, outputDirectory, options);
    }

    public static WebFormsConversionResult Convert(LegacyProject project, string outputDirectory, WebFormsConversionOptions? options = null)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("Output directory is required.", nameof(outputDirectory));
        options ??= new WebFormsConversionOptions();
        var todos = new List<MigrationTodo>();
        if (project.Kind != LegacyProjectKind.WebForms)
        {
            return Fail(project, outputDirectory, $"{project.Name} is {project.Kind}, not Web Forms.");
        }
        if (!project.Language.Equals("C#", StringComparison.OrdinalIgnoreCase))
        {
            todos.Add(new MigrationTodo(project.ProjectFile, "VB Web Forms markup can be inventoried, but code-behind is not translated to C#. No project was written."));
            return new WebFormsConversionResult
            {
                Succeeded = false,
                Source = project,
                Todos = todos,
                Report = string.Join(Environment.NewLine, todos.Select(todo => "TODO: " + todo.Message))
            };
        }

        var assembly = project.Name + ".Blazor";
        var root = Path.Combine(Path.GetFullPath(outputDirectory), assembly);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        var written = new List<string>();

        var routes = ReadRoutes(project);
        var bundles = ReadBundles(project, todos);
        var copied = CopySources(project, root, todos, out var contextType, out var contextSets, out var serviceInterface, out var serviceClass);
        written.AddRange(copied);
        var staticFiles = CopyStatic(project, root);
        written.AddRange(staticFiles);

        var master = project.Masters.OrderByDescending(page => project.Pages.Count(item => (item.MasterPage ?? "").Contains(Path.GetFileName(page.Path), StringComparison.OrdinalIgnoreCase))).FirstOrDefault();
        string? headLinks = null;
        if (master is not null)
        {
            var masterDoc = AspxSyntax.Parse(File.ReadAllText(master.Path));
            var emitted = WebFormsRazorEmitter.EmitMaster(masterDoc, Rel(project, master.Path), RouteMap(routes), bundles);
            todos.AddRange(emitted.Todos);
            headLinks = emitted.HeadLinks;
            var masterCode = ReadCode(project, master);
            var masterControls = new List<ControlSymbol>();
            var layoutCode = WebFormsCodeBehind.Translate(masterCode, Rel(project, master.Path), masterControls, RouteMap(routes), [], todos);
            todos.AddRange(NoteLegacy(masterCode, Rel(project, master.Path)));
            var layout = new StringBuilder();
            layout.AppendLine("@inherits LayoutComponentBase");
            foreach (var service in layoutCode.Services)
                layout.Append("@inject ").Append(service.Type).Append(' ').AppendLine(service.Name);
            if (layoutCode.NeedsNavigation)
                layout.AppendLine("@inject NavigationManager Navigation");
            layout.AppendLine(emitted.Body.Replace("(Web Forms)", "(Blazor)", StringComparison.Ordinal));
            layout.AppendLine("@code {");
            layout.AppendLine(layoutCode.Code);
            layout.AppendLine("}");
            written.Add(Write(root, "MainLayout.razor", layout.ToString()));
            foreach (var extra in project.Masters.Where(item => item != master))
                todos.Add(new MigrationTodo(Rel(project, extra.Path), "This master page is not the default layout. Only the master used by the pages was converted."));
        }
        else
        {
            written.Add(Write(root, "MainLayout.razor", "@inherits LayoutComponentBase\n@Body\n"));
            todos.Add(new MigrationTodo(project.Name, "No master page was found. The layout is only @Body."));
        }

        foreach (var page in project.Pages)
            written.Add(WritePage(project, root, page, routes, bundles, todos));
        foreach (var control in project.UserControls)
            written.Add(WriteControl(project, root, control, routes, todos));

        written.Add(Write(root, "App.razor", AppRazor(headLinks)));
        written.Add(Write(root, "Routes.razor", """
            <Router AppAssembly="typeof(App).Assembly">
                <Found Context="routeData">
                    <RouteView RouteData="routeData" DefaultLayout="typeof(MainLayout)" />
                    <FocusOnNavigate RouteData="routeData" Selector="h1" />
                </Found>
                <NotFound>
                    <PageTitle>Not found</PageTitle>
                    <LayoutView Layout="typeof(MainLayout)">
                        <p role="alert">Sorry, there's nothing at this address.</p>
                    </LayoutView>
                </NotFound>
            </Router>
            """));
        written.Add(Write(root, "_Imports.razor", Imports(project)));
        var sqlite = options.SqliteFileName;
        if (contextType is not null)
            written.Add(Write(root, "MigrationSeed.cs", SeedClass(project, contextType, contextSets, options, todos)));
        written.Add(Write(root, "Program.cs", Program(project, contextType, contextSets, serviceInterface, serviceClass, options, todos)));
        written.Add(Write(root, "appsettings.json", AppSettings(project, sqlite)));
        written.Add(Write(root, assembly + ".csproj", ProjectFile(assembly, options.TargetFramework, contextType is not null)));
        foreach (var setting in project.AppSettings)
        {
            if (setting.Key.Equals("UseMockData", StringComparison.OrdinalIgnoreCase) && setting.Value.Equals("true", StringComparison.OrdinalIgnoreCase))
                todos.Add(new MigrationTodo("Web.config", "UseMockData is true. The converted app still stores the catalog in SQLite so create, edit, and delete survive a restart. The rows come from the same preconfigured data the mock service used, unless UseCustomizationData is true, in which case CSV files are loaded. CatalogServiceMock remains in the project."));
        }
        if (project.UsesSqlServer)
            todos.Add(new MigrationTodo("Web.config", "The SQL Server / LocalDB connection string cannot be opened on this host. It was replaced with SQLite (" + sqlite + ")."));
        if (File.Exists(Path.Combine(project.Directory, "packages.config")))
            todos.Add(new MigrationTodo("packages.config", "NuGet packages.config entries were not copied. The Blazor project references EF Core SQLite and the ASP.NET Core shared framework."));
        todos.Add(new MigrationTodo(project.Name, "Application Insights, log4net, Autofac, and ScriptManager Web Forms scripts are not part of the Blazor app."));

        var distinct = todos
            .GroupBy(todo => todo.Path + "|" + todo.Message)
            .Select(group => group.First())
            .ToList();
        var report = Report(project, assembly, distinct, contextType);
        var reportPath = Write(root, "MIGRATION.md", report);
        written.Add(reportPath);
        return new WebFormsConversionResult
        {
            Succeeded = true,
            ProjectDirectory = root,
            ProjectFile = Path.Combine(root, assembly + ".csproj"),
            AssemblyName = assembly,
            SqlitePath = Path.Combine(root, sqlite),
            ReportPath = reportPath,
            OutputFiles = written,
            Todos = distinct,
            Report = report,
            Source = project
        };
    }

    private static string WritePage(LegacyProject project, string root, LegacyPage page, Dictionary<string, (string Template, string Page)> routes, Dictionary<string, IReadOnlyList<string>> bundles, List<MigrationTodo> todos)
    {
        var relative = Rel(project, page.Path);
        var codePath = CodePath(project, page);
        var code = codePath is null ? "" : File.ReadAllText(codePath);
        var document = AspxSyntax.Parse(File.ReadAllText(page.Path));
        var emitted = WebFormsRazorEmitter.EmitPage(document, relative, code, RouteMap(routes));
        todos.AddRange(emitted.Todos);
        var templates = routes.Where(pair => PageMatches(pair.Value.Page, page)).Select(pair => pair.Value.Template).ToList();
        if (templates.Count == 0)
            templates.Add(Path.ChangeExtension(relative, null).Replace('\\', '/'));
        var translated = WebFormsCodeBehind.Translate(code, relative, emitted.Controls, RouteMap(routes), templates, todos);
        todos.AddRange(NoteLegacy(code, relative));
        var razor = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Path.GetFileName(page.Path).Equals("Default.aspx", StringComparison.OrdinalIgnoreCase) && seen.Add("/"))
            razor.AppendLine("@page \"/\"");
        foreach (var template in templates)
        {
            var blazor = ToBlazorRoute(template, translated.Parameters);
            if (seen.Add(blazor))
                razor.Append("@page \"").Append(blazor).AppendLine("\"");
        }
        razor.AppendLine("@rendermode InteractiveServer");
        foreach (var usingName in Usings(code))
            razor.Append("@using ").AppendLine(usingName);
        foreach (var service in translated.Services)
            razor.Append("@inject ").Append(service.Type).Append(' ').AppendLine(service.Name);
        if (translated.NeedsNavigation)
            razor.AppendLine("@inject NavigationManager Navigation");
        var title = page.Title ?? Path.GetFileNameWithoutExtension(page.Path);
        razor.Append("<PageTitle>").Append(title).AppendLine("</PageTitle>");
        if (translated.NeedsValidation)
        {
            razor.AppendLine("@if (migrationErrors.Count > 0)");
            razor.AppendLine("{");
            razor.AppendLine("    <div class=\"text-danger\" role=\"alert\">");
            razor.AppendLine("    @foreach (var migrationError in migrationErrors)");
            razor.AppendLine("    {");
            razor.AppendLine("        <div>@migrationError</div>");
            razor.AppendLine("    }");
            razor.AppendLine("    </div>");
            razor.AppendLine("}");
        }
        razor.AppendLine(emitted.Markup);
        razor.AppendLine("@code {");
        if (emitted.Markup.Contains("@Title", StringComparison.Ordinal))
            razor.Append("    private string Title { get; } = ").Append(CSharpIds.Quote(title)).AppendLine(";");
        foreach (var parameter in translated.Parameters)
            razor.Append("    [Parameter] public ").Append(parameter.ClrType).Append(' ').Append(parameter.Name).AppendLine(" { get; set; }");
        razor.AppendLine(translated.Code);
        razor.AppendLine("}");
        var output = Path.Combine("Pages", Path.ChangeExtension(relative, ".razor"));
        return Write(root, output, razor.ToString());
    }

    private static string WriteControl(LegacyProject project, string root, LegacyPage control, Dictionary<string, (string Template, string Page)> routes, List<MigrationTodo> todos)
    {
        var relative = Rel(project, control.Path);
        var code = ReadCode(project, control);
        var document = AspxSyntax.Parse(File.ReadAllText(control.Path));
        var emitted = WebFormsRazorEmitter.EmitPage(document, relative, code, RouteMap(routes));
        todos.AddRange(emitted.Todos);
        todos.Add(new MigrationTodo(relative, "User control converted to a Razor component. Friendly-URL view switching and other System.Web APIs are TODO items when the code-behind used them."));
        var translated = WebFormsCodeBehind.Translate(code, relative, emitted.Controls, RouteMap(routes), [], todos);
        var razor = new StringBuilder();
        foreach (var service in translated.Services)
            razor.Append("@inject ").Append(service.Type).Append(' ').AppendLine(service.Name);
        razor.AppendLine(emitted.Markup);
        razor.AppendLine("@code {");
        razor.AppendLine(translated.Code);
        razor.AppendLine("}");
        return Write(root, Path.Combine("Components", Path.GetFileNameWithoutExtension(control.Path) + ".razor"), razor.ToString());
    }

    private static List<string> CopySources(LegacyProject project, string root, List<MigrationTodo> todos, out string? contextType, out List<(string Type, string Name)> sets, out string? serviceInterface, out string? serviceClass)
    {
        contextType = null;
        sets = [];
        serviceInterface = null;
        serviceClass = null;
        var written = new List<string>();
        var contextFile = project.DataAccess.FirstOrDefault(path => File.ReadAllText(path).Contains(": DbContext", StringComparison.Ordinal) || File.ReadAllText(path).Contains(": System.Data.Entity.DbContext", StringComparison.Ordinal));
        if (contextFile is not null)
        {
            var source = File.ReadAllText(contextFile);
            contextType = Regex.Match(source, @"class\s+(\w+)\s*:\s*(?:System\.Data\.Entity\.)?DbContext").Groups[1].Value;
            if (contextType.Length == 0) contextType = null;
            else
            {
                sets = Regex.Matches(source, @"DbSet<(\w+)>\s+(\w+)").Select(match => (match.Groups[1].Value, match.Groups[2].Value)).ToList();
                var rewritten = EnsureExplicitKeys(EfCoreRewriter.RewriteDbContext(source, todos, Rel(project, contextFile)), sets);
                written.Add(Write(root, Rel(project, contextFile), rewritten));
            }
        }
        var dbSet = sets.FirstOrDefault().Name ?? "";
        foreach (var file in Directory.EnumerateFiles(project.Directory, "*.cs", SearchOption.AllDirectories))
        {
            if (IsSkippedTree(project.Directory, file) || IsGeneratedSkip(file)) continue;
            if (contextFile is not null && string.Equals(file, contextFile, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Rel(project, file);
            var source = File.ReadAllText(file);
            if (contextType is not null && (Path.GetFileName(file).Contains("HiLo", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(source, @"\bint\s+GetNextSequenceValue\s*\(")))
            {
                written.Add(Write(root, relative, EfCoreRewriter.RewriteHiLo(source, contextType, dbSet, todos, relative)));
                continue;
            }
            if (contextType is not null && Regex.IsMatch(source, @"private\s+(?:readonly\s+)?" + Regex.Escape(contextType) + @"\s+\w+\s*;"))
            {
                var match = Regex.Match(source, @"class\s+(\w+)\s*:\s*(\w+)");
                if (match.Success && match.Groups[2].Value.StartsWith('I'))
                {
                    serviceClass = match.Groups[1].Value;
                    serviceInterface = match.Groups[2].Value;
                }
                written.Add(Write(root, relative, EfCoreRewriter.RewriteService(source, contextType, todos, relative)));
                continue;
            }
            var stripped = EfCoreRewriter.StripUsings(source);
            if (EfCoreRewriter.StillLegacy(stripped))
            {
                todos.Add(new MigrationTodo(relative, "This C# file still depends on System.Web, EF6, Autofac, log4net, or ConfigurationManager. It was not added to the Blazor project."));
                continue;
            }
            if (file.EndsWith("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)) continue;
            written.Add(Write(root, relative, stripped));
        }
        if (serviceInterface is null)
        {
            var iface = Directory.EnumerateFiles(project.Directory, "I*.cs", SearchOption.AllDirectories)
                .FirstOrDefault(path => !IsSkippedTree(project.Directory, path) && File.ReadAllText(path).Contains("interface I", StringComparison.Ordinal));
            if (iface is not null)
                serviceInterface = Regex.Match(File.ReadAllText(iface), @"interface\s+(I\w+)").Groups[1].Value;
        }
        return written;
    }

    private static List<string> CopyStatic(LegacyProject project, string root)
    {
        var written = new List<string>();
        foreach (var file in Directory.EnumerateFiles(project.Directory, "*", SearchOption.AllDirectories))
        {
            if (IsSkippedTree(project.Directory, file)) continue;
            var extension = Path.GetExtension(file);
            if (!AssetExtensions.Contains(extension)) continue;
            if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                written.Add(Copy(file, Path.Combine(root, Rel(project, file))));
                continue;
            }
            written.Add(Copy(file, Path.Combine(root, "wwwroot", Rel(project, file))));
        }
        return written;
    }

    private static string SeedClass(LegacyProject project, string contextType, List<(string Type, string Name)> sets, WebFormsConversionOptions options, List<MigrationTodo> todos)
    {
        var models = sets.Select(set => new EntityModel(set.Type, set.Name, Properties(project, set.Type))).ToList();
        var preconfigured = FindPreconfigured(project, models);
        var csv = MatchCsv(project, models);
        var builder = new StringBuilder();
        builder.AppendLine("using System.Globalization;");
        builder.AppendLine("using System.Text.RegularExpressions;");
        var ns = models.Select(model => NamespaceOf(project, model.Type)).FirstOrDefault(value => value is not null);
        if (ns is not null) builder.Append("using ").Append(ns).AppendLine(";");
        var preNs = preconfigured.Count > 0 ? NamespaceOfFile(preconfigured[0].File) : null;
        if (preNs is not null && preNs != ns) builder.Append("using ").Append(preNs).AppendLine(";");
        builder.AppendLine();
        builder.AppendLine("internal static class MigrationSeed");
        builder.AppendLine("{");
        builder.Append("    public static void Ensure(").Append(contextType).AppendLine(" db, string contentRoot, bool useCustomization)");
        builder.AppendLine("    {");
        builder.AppendLine("        db.Database.EnsureCreated();");
        if (sets.Count > 0)
            builder.Append("        if (").Append(string.Join(" || ", sets.Select(set => "db." + set.Name + ".Any()"))).AppendLine(") return;");
        builder.AppendLine("        if (!useCustomization)");
        builder.AppendLine("        {");
        if (preconfigured.Count > 0)
        {
            foreach (var call in preconfigured.OrderBy(call => models.FirstOrDefault(model => model.Set == call.Set)?.Properties.Count(prop => prop.Name.EndsWith("Id", StringComparison.Ordinal) && prop.Name != "Id") ?? 0))
                builder.Append("            foreach (var row in ").Append(call.TypeName).Append('.').Append(call.Method).Append("()) db.").Append(call.Set).AppendLine(".Add(row);");
            builder.AppendLine("            db.SaveChanges();");
            builder.AppendLine("            return;");
        }
        else
        {
            builder.AppendLine("            // TODO: no preconfigured seed methods were found.");
            todos.Add(new MigrationTodo("MigrationSeed.cs", "No preconfigured seed method was found. CSV files are used when they match a DbSet."));
        }
        builder.AppendLine("        }");
        if (csv.Count == 0)
            builder.AppendLine("        // TODO: no CSV seed files matched the DbSets.");
        foreach (var file in csv)
        {
            builder.Append("        Import").Append(file.Entity.Type).Append("(db, Path.Combine(contentRoot, ").Append(CSharpIds.Quote(Rel(project, file.Path).Replace('\\', '/'))).AppendLine("));");
            builder.AppendLine("        db.SaveChanges();");
        }
        builder.AppendLine("    }");
        foreach (var file in csv)
            builder.Append(CsvMethod(file));
        builder.AppendLine();
        builder.AppendLine("""
            private static string[] SplitCsv(string row)
            {
                var cells = new List<string>();
                var current = new System.Text.StringBuilder();
                var quoted = false;
                foreach (var ch in row)
                {
                    if (ch == '"') { quoted = !quoted; continue; }
                    if (ch == ',' && !quoted) { cells.Add(current.ToString().Trim()); current.Clear(); continue; }
                    current.Append(ch);
                }
                cells.Add(current.ToString().Trim());
                return cells.ToArray();
            }
        """);
        builder.AppendLine("}");
        if (options.PreferCsvSeed == true)
            todos.Add(new MigrationTodo("MigrationSeed.cs", "PreferCsvSeed is set. CSV files are loaded even when UseCustomizationData is false."));
        return builder.ToString();
    }

    private static string CsvMethod(CsvSeed file)
    {
        var builder = new StringBuilder();
        builder.AppendLine();
        builder.Append("    private static void Import").Append(file.Entity.Type).Append('(').Append(file.Context).AppendLine(" db, string path)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (!File.Exists(path)) return;");
        builder.AppendLine("        var lines = File.ReadAllLines(path);");
        builder.AppendLine("        if (lines.Length == 0) return;");
        builder.AppendLine("        var headers = lines[0].Split(',').Select(cell => cell.Trim().Trim('\\\"').ToLowerInvariant()).ToArray();");
        builder.Append("        var index = db.").Append(file.Entity.Set).AppendLine(".Select(row => (int?)row.Id).Max() ?? 0;");
        builder.AppendLine("        for (var line = 1; line < lines.Length; line++)");
        builder.AppendLine("        {");
        builder.AppendLine("            if (string.IsNullOrWhiteSpace(lines[line])) continue;");
        builder.AppendLine("            var cells = SplitCsv(lines[line]);");
        builder.Append("            var row = new ").Append(file.Entity.Type).AppendLine("();");
        builder.AppendLine("            row.Id = ++index;");
        foreach (var column in file.Columns)
        {
            builder.Append("            { var i = Array.IndexOf(headers, ").Append(CSharpIds.Quote(column.Header)).AppendLine(");");
            builder.AppendLine("            if (i >= 0 && i < cells.Length) {");
            if (column.Lookup is not null)
            {
                builder.Append("                var match = db.").Append(column.Lookup.Set).Append(".ToList().FirstOrDefault(item => string.Equals(item.").Append(column.Lookup.Property).AppendLine(", cells[i], StringComparison.OrdinalIgnoreCase));");
                builder.Append("                if (match != null) row.").Append(column.Property).AppendLine(" = match.Id;");
            }
            else if (column.ClrType is "int")
                builder.Append("                if (int.TryParse(cells[i], out var parsed)) row.").Append(column.Property).AppendLine(" = parsed;");
            else if (column.ClrType is "decimal")
                builder.Append("                if (decimal.TryParse(cells[i], NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)) row.").Append(column.Property).AppendLine(" = parsed;");
            else if (column.ClrType is "bool")
                builder.Append("                if (bool.TryParse(cells[i], out var parsed)) row.").Append(column.Property).AppendLine(" = parsed;");
            else
                builder.Append("                row.").Append(column.Property).AppendLine(" = cells[i];");
            builder.AppendLine("            } }");
        }
        builder.Append("            db.").Append(file.Entity.Set).AppendLine(".Add(row);");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        return builder.ToString();
    }

    private static string Program(LegacyProject project, string? context, List<(string Type, string Name)> sets, string? serviceInterface, string? serviceClass, WebFormsConversionOptions options, List<MigrationTodo> todos)
    {
        var builder = new StringBuilder();
        builder.AppendLine("using Microsoft.EntityFrameworkCore;");
        builder.Append("using ").Append(SanitizeRoot(project.Name + ".Blazor")).AppendLine(";");
        foreach (var name in ProgramUsings(project))
            builder.Append("using ").Append(name).AppendLine(";");
        builder.AppendLine("var builder = WebApplication.CreateBuilder(args);");
        builder.AppendLine("var cultureName = builder.Configuration[\"Culture\"] ?? \"en-US\";");
        builder.AppendLine("var culture = System.Globalization.CultureInfo.GetCultureInfo(cultureName);");
        builder.AppendLine("System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;");
        builder.AppendLine("System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;");
        builder.AppendLine("builder.Services.AddRazorComponents().AddInteractiveServerComponents();");
        if (context is not null)
        {
            builder.Append("builder.Services.AddDbContextFactory<").Append(context).Append(">(options => options.UseSqlite(\"Data Source=\" + Path.Combine(builder.Environment.ContentRootPath, ")
                .Append(CSharpIds.Quote(options.SqliteFileName)).AppendLine(")));");
            var hilo = project.DataAccess.Concat(project.Models).FirstOrDefault(path => Path.GetFileName(path).Contains("HiLo", StringComparison.OrdinalIgnoreCase));
            if (hilo is not null)
            {
                var hiloType = Regex.Match(File.ReadAllText(hilo), @"class\s+(\w+)").Groups[1].Value;
                if (hiloType.Length > 0)
                    builder.Append("builder.Services.AddScoped<").Append(hiloType).AppendLine(">();");
            }
            if (serviceInterface is not null && serviceClass is not null)
                builder.Append("builder.Services.AddScoped<").Append(serviceInterface).Append(", ").Append(serviceClass).AppendLine(">();");
        }
        builder.AppendLine("var app = builder.Build();");
        if (context is not null)
        {
            builder.AppendLine("using (var scope = app.Services.CreateScope())");
            builder.AppendLine("{");
            builder.Append("    using var database = scope.ServiceProvider.GetRequiredService<IDbContextFactory<").Append(context).AppendLine(">>().CreateDbContext();");
            builder.Append("    var customize = ").Append(options.PreferCsvSeed == true ? "true" : "bool.TryParse(builder.Configuration[\"UseCustomizationData\"], out var customization) && customization").AppendLine(";");
            builder.AppendLine("    MigrationSeed.Ensure(database, app.Environment.ContentRootPath, customize);");
            if (HasCatalogShape(project))
            {
                builder.AppendLine("    if (string.Equals(Environment.GetEnvironmentVariable(\"FLEXCORE_MIGRATION_CHECK\"), \"crud\", StringComparison.OrdinalIgnoreCase))");
                builder.AppendLine("    {");
                builder.AppendLine("        MigrationCrudCheck(scope.ServiceProvider);");
                builder.AppendLine("        return;");
                builder.AppendLine("    }");
            }
            builder.AppendLine("}");
        }
        builder.AppendLine("app.UseStaticFiles();");
        builder.AppendLine("app.UseAntiforgery();");
        builder.AppendLine("app.MapRazorComponents<App>().AddInteractiveServerRenderMode();");
        builder.AppendLine("app.Run();");
        if (context is not null && HasCatalogShape(project) && serviceInterface is not null)
            builder.Append(CrudCheck(context, serviceInterface));
        return builder.ToString();
    }

    private static string CrudCheck(string context, string serviceInterface)
    {
        return $$"""

            static void MigrationCrudCheck(IServiceProvider services)
            {
                using var db = services.GetRequiredService<IDbContextFactory<{{context}}>>().CreateDbContext();
                var service = services.GetRequiredService<{{serviceInterface}}>();
                var before = db.CatalogItems.Count();
                if (before < 1) throw new InvalidOperationException("SQLite seed did not insert catalog items.");
                var brandId = db.CatalogBrands.Select(row => row.Id).First();
                var typeId = db.CatalogTypes.Select(row => row.Id).First();
                var item = new CatalogItem
                {
                    Name = "FlexCore migration probe",
                    Description = "created by the converted catalog",
                    Price = 1.25m,
                    CatalogBrandId = brandId,
                    CatalogTypeId = typeId,
                    AvailableStock = 3,
                    PictureFileName = "1.png"
                };
                service.CreateCatalogItem(item);
                if (item.Id == 0) throw new InvalidOperationException("Create did not assign an id.");
                var found = service.FindCatalogItem(item.Id);
                if (found == null || found.Name != item.Name) throw new InvalidOperationException("Find after create failed.");
                service.UpdateCatalogItem(new CatalogItem
                {
                    Id = item.Id,
                    Name = "FlexCore migration probe edited",
                    Description = found.Description,
                    Price = 2.5m,
                    CatalogBrandId = brandId,
                    CatalogTypeId = typeId,
                    AvailableStock = 4,
                    RestockThreshold = found.RestockThreshold,
                    MaxStockThreshold = found.MaxStockThreshold,
                    PictureFileName = found.PictureFileName ?? "1.png"
                });
                var edited = service.FindCatalogItem(item.Id);
                if (edited == null || edited.Name != "FlexCore migration probe edited" || edited.AvailableStock != 4)
                    throw new InvalidOperationException("Update was not saved.");
                service.RemoveCatalogItem(edited);
                if (service.FindCatalogItem(item.Id) != null) throw new InvalidOperationException("Delete left the row in place.");
                Console.WriteLine("FLEXCORE_MIGRATION_CHECK=ok items=" + before);
            }
            """;
    }

    private static bool HasCatalogShape(LegacyProject project) =>
        project.Models.Any(path => Path.GetFileName(path).Equals("CatalogItem.cs", StringComparison.OrdinalIgnoreCase))
        && project.Models.Any(path => Path.GetFileName(path).Contains("CatalogBrand", StringComparison.OrdinalIgnoreCase));

    private static string AppSettings(LegacyProject project, string sqlite)
    {
        var settings = new Dictionary<string, object?>
        {
            ["ConnectionStrings"] = new Dictionary<string, string> { ["Catalog"] = "Data Source=" + sqlite },
            ["Culture"] = project.ConfigFiles.Select(ReadCulture).FirstOrDefault(value => value is not null) ?? "en-US"
        };
        foreach (var setting in project.AppSettings)
            settings[setting.Key] = setting.Value;
        var original = project.ConnectionStrings.ToDictionary(item => item.Name, item => item.Value);
        settings["Migration"] = new { OriginalConnectionStrings = original, Store = "SQLite replaces SQL Server / LocalDB." };
        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? ReadCulture(string path)
    {
        if (!path.EndsWith(".config", StringComparison.OrdinalIgnoreCase)) return null;
        var match = Regex.Match(File.ReadAllText(path), @"culture\s*=\s*""([^""]+)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string SanitizeRoot(string name)
    {
        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('.', parts.Select(part => char.IsDigit(part[0]) ? "_" + part : part));
    }

    private static string ProjectFile(string assembly, string tfm, bool ef)
    {
        var packages = ef
            ? """
                  <ItemGroup>
                    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.0" />
                  </ItemGroup>
              """
            : "";
        return $"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>{tfm}</TargetFramework>
                <Nullable>disable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AssemblyName>{assembly}</AssemblyName>
                <RootNamespace>{SanitizeRoot(assembly)}</RootNamespace>
              </PropertyGroup>
            {packages}  <ItemGroup>
                <Content Include="Setup\**\*" CopyToOutputDirectory="PreserveNewest" />
              </ItemGroup>
              <Target Name="CopyBlazorWebScript" AfterTargets="Build">
                <ItemGroup>
                  <_BlazorWebJs Include="$(NuGetPackageRoot)microsoft.aspnetcore.app.internal.assets/**/blazor.web.js" />
                </ItemGroup>
                <MakeDir Directories="$(MSBuildProjectDirectory)/wwwroot/_framework" />
                <Copy SourceFiles="@(_BlazorWebJs)" DestinationFolder="$(MSBuildProjectDirectory)/wwwroot/_framework" SkipUnchangedFiles="true" />
              </Target>
            </Project>
            """;
    }

    private static string AppRazor(string? headLinks) => """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <base href="/" />
        """ + (headLinks ?? "") + """
            <link rel="icon" href="/favicon.ico" />
            <HeadOutlet />
        </head>
        <body>
            <Routes />
            <script src="/_framework/blazor.web.js"></script>
        </body>
        </html>
        """;

    private static string Imports(LegacyProject project)
    {
        var namespaces = new HashSet<string>(StringComparer.Ordinal)
        {
            "Microsoft.AspNetCore.Components.Web",
            "Microsoft.AspNetCore.Components.Routing",
            "System.Linq"
        };
        foreach (var file in project.Models.Concat(project.DataAccess).Concat(project.CodeBehind))
        {
            if (!File.Exists(file) || IsGeneratedSkip(file)) continue;
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"^namespace\s+([\w\.]+)", RegexOptions.Multiline))
                namespaces.Add(match.Groups[1].Value);
        }
        var builder = new StringBuilder();
        builder.AppendLine("@using static Microsoft.AspNetCore.Components.Web.RenderMode");
        foreach (var name in namespaces.OrderBy(name => name, StringComparer.Ordinal))
            builder.Append("@using ").AppendLine(name);
        return builder.ToString();
    }

    private static string Report(LegacyProject project, string assembly, List<MigrationTodo> todos, string? context)
    {
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(assembly);
        builder.AppendLine();
        builder.Append("Converted from ").Append(project.Name).Append(" (").Append(project.Kind).Append(", ").Append(project.TargetFramework).AppendLine(").");
        builder.AppendLine();
        builder.Append("Pages: ").AppendLine(project.Pages.Count.ToString());
        builder.Append("Masters: ").AppendLine(project.Masters.Count.ToString());
        builder.Append("Data: ").AppendLine(context ?? "none");
        builder.AppendLine();
        builder.AppendLine("## TODO");
        foreach (var todo in todos)
            builder.Append("- ").Append(todo.Path).Append(": ").AppendLine(todo.Message);
        return builder.ToString();
    }

    private static Dictionary<string, (string Template, string Page)> ReadRoutes(LegacyProject project)
    {
        var map = new Dictionary<string, (string Template, string Page)>(StringComparer.OrdinalIgnoreCase);
        var file = Directory.EnumerateFiles(project.Directory, "RouteConfig.cs", SearchOption.AllDirectories).FirstOrDefault();
        if (file is null) return map;
        foreach (Match match in Regex.Matches(File.ReadAllText(file), @"MapPageRoute\(\s*""(?<name>[^""]*)""\s*,\s*""(?<template>[^""]*)""\s*,\s*""(?<page>[^""]*)""", RegexOptions.Singleline))
            map[match.Groups["name"].Value] = (match.Groups["template"].Value, match.Groups["page"].Value);
        return map;
    }

    private static Dictionary<string, string> RouteMap(Dictionary<string, (string Template, string Page)> routes) =>
        routes.Where(pair => pair.Key.Length > 0).ToDictionary(pair => pair.Key, pair => pair.Value.Template, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, IReadOnlyList<string>> ReadBundles(LegacyProject project, List<MigrationTodo> todos)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var file = Directory.EnumerateFiles(project.Directory, "BundleConfig.cs", SearchOption.AllDirectories).FirstOrDefault();
        if (file is null) return map;
        var text = File.ReadAllText(file);
        foreach (Match match in Regex.Matches(text, @"new\s+(?:Script|Style)Bundle\(\s*""(?<path>[^""]+)""\s*\)\.Include\((?<body>.*?)\)\s*;", RegexOptions.Singleline))
        {
            var files = new List<string>();
            foreach (Match include in Regex.Matches(match.Groups["body"].Value, @"""(~/[^""]+)"""))
                files.AddRange(Expand(project, include.Groups[1].Value));
            map[match.Groups["path"].Value] = files;
            if (files.Count == 0)
                todos.Add(new MigrationTodo(Rel(project, file), "Bundle " + match.Groups["path"].Value + " did not match any files."));
        }
        return map;
    }

    private static IEnumerable<string> Expand(LegacyProject project, string virtualPath)
    {
        var relative = virtualPath.TrimStart('~', '/').Replace('/', Path.DirectorySeparatorChar);
        if (relative.Contains('*'))
        {
            var directory = Path.GetDirectoryName(relative) ?? "";
            var pattern = Path.GetFileName(relative);
            var full = Path.Combine(project.Directory, directory);
            if (!Directory.Exists(full)) yield break;
            foreach (var file in Directory.EnumerateFiles(full, pattern))
                yield return ("/" + Rel(project, file)).Replace('\\', '/');
            yield break;
        }
        if (File.Exists(Path.Combine(project.Directory, relative)))
            yield return "/" + relative.Replace('\\', '/');
    }

    private static List<Prop> Properties(LegacyProject project, string type)
    {
        var file = project.Models.FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).Equals(type, StringComparison.OrdinalIgnoreCase));
        if (file is null || !File.Exists(file)) return [];
        return Regex.Matches(File.ReadAllText(file), @"public\s+([\w\?]+)\s+(\w+)\s*\{\s*get;")
            .Select(match => new Prop(match.Groups[2].Value, match.Groups[1].Value.TrimEnd('?')))
            .ToList();
    }

    private static List<PreconfiguredCall> FindPreconfigured(LegacyProject project, List<EntityModel> models)
    {
        var calls = new List<PreconfiguredCall>();
        foreach (var file in project.SeedData.Where(seed => seed.Kind == LegacySeedKind.PreconfiguredData).Select(seed => seed.Path))
        {
            if (!File.Exists(file)) continue;
            var text = File.ReadAllText(file);
            var type = Regex.Match(text, @"class\s+(\w+)").Groups[1].Value;
            foreach (Match method in Regex.Matches(text, @"static\s+[\w<>,\.\?]+\s+(\w+)\s*\("))
            {
                var name = method.Groups[1].Value;
                var entity = models.FirstOrDefault(model => name.Contains(model.Type, StringComparison.OrdinalIgnoreCase));
                if (entity is not null)
                    calls.Add(new PreconfiguredCall(file, type, name, entity.Set));
            }
        }
        return calls;
    }

    private static List<CsvSeed> MatchCsv(LegacyProject project, List<EntityModel> models)
    {
        var files = new List<CsvSeed>();
        var contextName = models.Count == 0 ? "object" : FindContextName(project);
        foreach (var seed in project.SeedData.Where(item => item.Kind == LegacySeedKind.Csv))
        {
            var name = Path.GetFileNameWithoutExtension(seed.Path).Replace("_", "", StringComparison.Ordinal);
            var entity = models.FirstOrDefault(model => name.Contains(model.Type, StringComparison.OrdinalIgnoreCase) || Singular(name).Equals(model.Type, StringComparison.OrdinalIgnoreCase));
            if (entity is null) continue;
            var header = File.ReadLines(seed.Path).FirstOrDefault();
            if (header is null) continue;
            var columns = new List<CsvColumn>();
            foreach (var cell in header.Split(','))
            {
                var normalized = Norm(cell);
                var direct = entity.Properties.FirstOrDefault(prop => Norm(prop.Name) == normalized && prop.Name != "Id");
                if (direct is not null)
                {
                    columns.Add(new CsvColumn(normalized, direct.Name, direct.Type, null));
                    continue;
                }
                var lookup = models.FirstOrDefault(model => model.Type != entity.Type && normalized.StartsWith(Norm(model.Type), StringComparison.Ordinal));
                if (lookup is not null)
                {
                    var display = lookup.Properties.FirstOrDefault(prop => prop.Type == "string" && prop.Name != "Id");
                    var fk = entity.Properties.FirstOrDefault(prop => prop.Name.Equals(lookup.Type + "Id", StringComparison.OrdinalIgnoreCase));
                    if (display is not null && fk is not null)
                        columns.Add(new CsvColumn(normalized, fk.Name, fk.Type, new Lookup(lookup.Set, display.Name)));
                }
            }
            if (columns.Count > 0)
                files.Add(new CsvSeed(seed.Path, entity, columns, contextName));
        }
        files.Sort((left, right) => LookupRank(left).CompareTo(LookupRank(right)));
        return files;
    }

    private static int LookupRank(CsvSeed file) => file.Columns.Count(column => column.Lookup is not null);

    private static string FindContextName(LegacyProject project)
    {
        foreach (var file in project.DataAccess)
        {
            if (!File.Exists(file)) continue;
            var match = Regex.Match(File.ReadAllText(file), @"class\s+(\w+)\s*:\s*(?:System\.Data\.Entity\.)?DbContext");
            if (match.Success) return match.Groups[1].Value;
        }
        return "object";
    }

    private static string EnsureExplicitKeys(string source, List<(string Type, string Name)> sets)
    {
        var method = Regex.Match(source, @"OnModelCreating\s*\(\s*ModelBuilder\s+(?<name>\w+)\s*\)\s*\{");
        if (!method.Success || sets.Count == 0) return source;
        var open = method.Index + method.Length - 1;
        var builder = method.Groups["name"].Value;
        var insert = new StringBuilder();
        foreach (var set in sets)
            insert.Append("\n        ").Append(builder).Append(".Entity<").Append(set.Type).Append(">().Property(entity => entity.Id).ValueGeneratedNever();");
        return source.Insert(open + 1, insert.ToString());
    }

    private static IEnumerable<string> ProgramUsings(LegacyProject project)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in project.Models.Concat(project.DataAccess))
        {
            if (!File.Exists(file)) continue;
            var match = Regex.Match(File.ReadAllText(file), @"^namespace\s+([\w\.]+)", RegexOptions.Multiline);
            if (match.Success && !match.Groups[1].Value.Contains("Infrastructure", StringComparison.Ordinal))
                names.Add(match.Groups[1].Value);
        }
        var service = Directory.EnumerateFiles(project.Directory, "I*.cs", SearchOption.AllDirectories)
            .FirstOrDefault(path => !IsSkippedTree(project.Directory, path) && File.ReadAllText(path).Contains("interface I", StringComparison.Ordinal));
        if (service is not null)
        {
            var match = Regex.Match(File.ReadAllText(service), @"^namespace\s+([\w\.]+)", RegexOptions.Multiline);
            if (match.Success) names.Add(match.Groups[1].Value);
        }
        return names.OrderBy(name => name, StringComparer.Ordinal);
    }

    private static string? NamespaceOf(LegacyProject project, string type)
    {
        var file = project.Models.FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).Equals(type, StringComparison.OrdinalIgnoreCase));
        return file is null ? null : NamespaceOfFile(file);
    }

    private static string? NamespaceOfFile(string file)
    {
        var match = Regex.Match(File.ReadAllText(file), @"^namespace\s+([\w\.]+)", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static IEnumerable<MigrationTodo> NoteLegacy(string? source, string path)
    {
        if (string.IsNullOrEmpty(source)) yield break;
        if (source.Contains("log4net", StringComparison.Ordinal))
            yield return new MigrationTodo(path, "log4net calls were removed.");
        if (source.Contains("Session[", StringComparison.Ordinal) || source.Contains("HttpContext", StringComparison.Ordinal))
            yield return new MigrationTodo(path, "Session or HttpContext usage was not converted.");
    }

    private static IEnumerable<string> Usings(string source) =>
        Regex.Matches(source, @"^using\s+([\w\.]+)\s*;", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .Where(name => !name.StartsWith("System.Web", StringComparison.Ordinal) && name is not "log4net" and not "Autofac" and not "System.Data.Entity")
            .Distinct();

    private static bool PageMatches(string routePage, LegacyPage page)
    {
        var name = Path.GetFileName(routePage.Replace('/', Path.DirectorySeparatorChar).Replace("~", "", StringComparison.Ordinal));
        return name.Equals(Path.GetFileName(page.Path), StringComparison.OrdinalIgnoreCase);
    }

    private static string ToBlazorRoute(string template, List<(string Name, string ClrType, bool Optional)> parameters)
    {
        var path = "/" + template.TrimStart('/');
        return Regex.Replace(path, @"\{(\w+)\}", match =>
        {
            var name = match.Groups[1].Value;
            var parameter = parameters.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var token = parameter.Name ?? char.ToUpperInvariant(name[0]) + name[1..];
            var optional = parameter.Optional ? "?" : "";
            var constraint = (parameter.ClrType ?? "").StartsWith("int", StringComparison.Ordinal) ? ":int" : "";
            return "{" + token + constraint + optional + "}";
        });
    }

    private static string? CodePath(LegacyProject project, LegacyPage page)
    {
        if (string.IsNullOrEmpty(page.CodeBehind))
        {
            var guess = page.Path + ".cs";
            return File.Exists(guess) ? guess : null;
        }
        var combined = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(page.Path)!, page.CodeBehind.Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(combined) ? combined : null;
    }

    private static string ReadCode(LegacyProject project, LegacyPage page)
    {
        var path = CodePath(project, page);
        return path is null ? "" : File.ReadAllText(path);
    }

    private static bool IsGeneratedSkip(string path)
    {
        var name = Path.GetFileName(path);
        return name.Contains(".aspx.", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".master.", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".ascx.", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".designer.", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Global.asax.cs", StringComparison.OrdinalIgnoreCase)
            || name.Equals("RouteConfig.cs", StringComparison.OrdinalIgnoreCase)
            || name.Equals("BundleConfig.cs", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSkippedTree(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("packages", StringComparison.OrdinalIgnoreCase) || segment.Equals(".vs", StringComparison.OrdinalIgnoreCase));
    }

    private static string Rel(LegacyProject project, string path) => Path.GetRelativePath(project.Directory, path);

    private static string Write(string root, string relative, string contents)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static string Copy(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
        return to;
    }

    private static WebFormsConversionResult Fail(LegacyProject project, string output, string message) => new()
    {
        Succeeded = false,
        Source = project,
        ProjectDirectory = output,
        Report = message,
        Todos = [new MigrationTodo(project.Name, message)]
    };

    private static string Norm(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    private static string Singular(string value) => value.EndsWith('s') ? value[..^1] : value;

    private sealed record Prop(string Name, string Type);
    private sealed record EntityModel(string Type, string Set, List<Prop> Properties);
    private sealed record PreconfiguredCall(string File, string TypeName, string Method, string Set);
    private sealed record Lookup(string Set, string Property);
    private sealed record CsvColumn(string Header, string Property, string ClrType, Lookup? Lookup);
    private sealed record CsvSeed(string Path, EntityModel Entity, List<CsvColumn> Columns, string Context);
}
