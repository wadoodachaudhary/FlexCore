using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using Fx.ControlKit.Migration;
using Fx.ControlKit.Preview;

internal static class LegacyMigrationChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "flexcore-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RejectsZipSlip(check, root);
            RejectsOversizedEntry(check, root);
            ClassifiesProjectKinds(check, root);
            await ConvertedShopServesCatalog(check, root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static void RejectsZipSlip(Action<bool, string> check, string root)
    {
        var destination = Path.Combine(root, "slip");
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("../outside.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("nope");
        }
        stream.Position = 0;
        var rejected = false;
        try { LegacyProjectIntake.Extract(stream, destination); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected && !File.Exists(Path.Combine(Directory.GetParent(destination)!.FullName, "outside.txt")), "zip-slip entries are rejected");

        stream.Position = 0;
        stream.SetLength(0);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("nested/ok.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("ok");
        }
        stream.Position = 0;
        var extracted = LegacyProjectIntake.Extract(stream, Path.Combine(root, "ok"));
        check(extracted.Files.Count == 1 && File.ReadAllText(extracted.Files[0]) == "ok", "a normal archive entry is extracted");
    }

    private static void RejectsOversizedEntry(Action<bool, string> check, string root)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("big.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(new string('x', 50));
        }
        stream.Position = 0;
        var rejected = false;
        try { LegacyProjectIntake.Extract(stream, Path.Combine(root, "big"), new LegacyArchiveLimits { MaxEntryBytes = 10, MaxArchiveBytes = 10_000, MaxExtractedBytes = 10_000, MaxEntries = 10 }); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "an entry above the size limit is rejected");
    }

    private static void ClassifiesProjectKinds(Action<bool, string> check, string root)
    {
        var folder = Path.Combine(root, "kinds");
        WriteWinForms(Path.Combine(folder, "win"));
        WriteWpf(Path.Combine(folder, "wpf"));
        WriteMvc(Path.Combine(folder, "mvc"));
        var inventory = LegacyProjectIntake.Inspect(folder);
        check(inventory.Projects.Any(project => project.Kind == LegacyProjectKind.WinForms), "a UseWindowsForms project is WinForms");
        check(inventory.Projects.Any(project => project.Kind == LegacyProjectKind.Wpf), "a UseWPF project is WPF");
        check(inventory.Projects.Any(project => project.Kind == LegacyProjectKind.Mvc), "controllers and views are MVC");
    }

    private static async Task ConvertedShopServesCatalog(Action<bool, string> check, string root)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "eShopLegacyWebForms.zip");
        check(File.Exists(fixture), "the eShopLegacyWebForms fixture is vendored");
        var extracted = LegacyProjectIntake.ExtractFile(fixture, Path.Combine(root, "eshop"));
        check(extracted.Files.Any(file => file.EndsWith("Default.aspx", StringComparison.OrdinalIgnoreCase)), "the archive contains Default.aspx");
        var inventory = LegacyProjectIntake.Inspect(extracted.Destination);
        var project = inventory.PrimaryWebFormsProject;
        check(project is not null && project.Language == "C#" && project.TargetFramework == "v4.7.2", "eShop is classified as C# Web Forms on 4.7.2");
        check(project!.Pages.Any(page => page.Path.Contains("Catalog" + Path.DirectorySeparatorChar + "Create.aspx", StringComparison.OrdinalIgnoreCase)), "the inventory lists the create page");
        check(project.Masters.Count >= 1 && project.UserControls.Count >= 1, "the inventory lists master pages and user controls");
        check(project.Models.Any(path => path.EndsWith("CatalogItem.cs", StringComparison.OrdinalIgnoreCase)), "the inventory lists catalog models");
        check(project.DataAccess.Any(path => path.Contains("CatalogDBContext", StringComparison.OrdinalIgnoreCase)), "the inventory lists the EF6 context");
        check(project.ConnectionStrings.Any(item => item.Value.Contains("localdb", StringComparison.OrdinalIgnoreCase)), "the inventory lists the LocalDB connection string");
        check(project.AppSettings.Any(item => item.Key == "UseMockData" && item.Value == "true"), "UseMockData is read from Web.config");
        check(project.SeedData.Any(item => item.Kind == LegacySeedKind.Csv) && project.SeedData.Any(item => item.Kind == LegacySeedKind.PreconfiguredData) && project.SeedData.Any(item => item.Kind == LegacySeedKind.MockService), "CSV, preconfigured data, and the mock service are seed sources");
        check(project.StaticAssets.Any(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)), "static images are inventoried");

        var unavailable = LegacyAppLauncher.Plan(project);
        check(unavailable.Kind == LegacyHostKind.Unavailable && !unavailable.RendersOnThisHost && unavailable.Launch is null, "without xsp4 or a remote URL the legacy app is not pretended to render");
        check(unavailable.Reasons.Any(reason => reason.Contains("4.7.2", StringComparison.Ordinal)) && unavailable.Reasons.Any(reason => reason.Contains("Entity Framework", StringComparison.OrdinalIgnoreCase)), "the Mono report names the framework and EF6");
        var remote = LegacyAppLauncher.Plan(project, new LegacyLaunchOptions { RemoteUrl = "http://127.0.0.1:8088/eshop" });
        check(remote.Kind == LegacyHostKind.RemoteUrl && remote.Launch is { Kind: ProgramPreviewKind.Web, FileName: null } && remote.Launch.Url == "http://127.0.0.1:8088/eshop", "a remote IIS Express URL is a web preview with no local process");

        var conversion = WebFormsBlazorConverter.Convert(inventory, Path.Combine(root, "out"));
        check(conversion.Succeeded && File.Exists(conversion.ProjectFile), "conversion writes a Blazor project");
        check(conversion.Todos.Any(todo => todo.Message.Contains("SQLite", StringComparison.OrdinalIgnoreCase)), "LocalDB replacement is reported");
        check(conversion.Todos.Any(todo => todo.Message.Contains("ScriptManager", StringComparison.Ordinal) || todo.Message.Contains("Web Forms script", StringComparison.Ordinal)), "unconverted Web Forms scripts are reported");
        var createPage = File.ReadAllText(Directory.EnumerateFiles(conversion.ProjectDirectory!, "Create.razor", SearchOption.AllDirectories).Single());
        check(createPage.Contains("@onclick=\"Create_Click\"", StringComparison.Ordinal) && createPage.Contains("GetBrands()", StringComparison.Ordinal), "the create page binds the button and the brand list");
        var listPage = File.ReadAllText(Directory.EnumerateFiles(conversion.ProjectDirectory!, "Default.razor", SearchOption.AllDirectories).Single());
        check(listPage.Contains("@foreach", StringComparison.Ordinal) && listPage.Contains("item.Name", StringComparison.Ordinal), "the catalog list becomes a foreach over the item template");

        var dotnet = FindDotnet();
        var build = await Run(dotnet, ["build", conversion.ProjectFile!, "--nologo"], conversion.ProjectDirectory!, TimeSpan.FromMinutes(3));
        check(build.ExitCode == 0, "the converted eShop Blazor project builds");

        var crud = await Run(dotnet, ["run", "--project", conversion.ProjectFile!, "--no-build", "--urls", "http://127.0.0.1:0"], conversion.ProjectDirectory!, TimeSpan.FromMinutes(2),
            new Dictionary<string, string> { ["FLEXCORE_MIGRATION_CHECK"] = "crud" });
        check(crud.ExitCode == 0 && crud.Output.Contains("FLEXCORE_MIGRATION_CHECK=ok", StringComparison.Ordinal), "create, update, and delete round-trip through SQLite");

        var port = FreePort();
        using var server = Process.Start(new ProcessStartInfo
        {
            FileName = dotnet,
            ArgumentList = { "run", "--project", conversion.ProjectFile!, "--no-build", "--urls", $"http://127.0.0.1:{port}" },
            WorkingDirectory = conversion.ProjectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var home = await WaitFor(http, $"http://127.0.0.1:{port}/");
            check(home.Contains(".NET Bot Black Hoodie", StringComparison.Ordinal) && home.Contains("Create New", StringComparison.Ordinal), "the catalog list is served from SQLite");
            var details = await http.GetStringAsync($"http://127.0.0.1:{port}/Catalog/Details/1");
            check(details.Contains(".NET Bot Black Hoodie", StringComparison.Ordinal) && details.Contains("Edit", StringComparison.Ordinal), "the details page is served");
            var create = await http.GetStringAsync($"http://127.0.0.1:{port}/Catalog/Create");
            check(create.Contains("Name", StringComparison.Ordinal) && create.Contains("[ Create ]", StringComparison.Ordinal), "the create page is served");
            var edit = await http.GetStringAsync($"http://127.0.0.1:{port}/Catalog/Edit/1");
            check(edit.Contains(".NET Bot Black Hoodie", StringComparison.Ordinal) && edit.Contains("[ Save ]", StringComparison.Ordinal), "the edit page is served");
            var delete = await http.GetStringAsync($"http://127.0.0.1:{port}/Catalog/Delete/1");
            check(delete.Contains("Are you sure you want to delete this?", StringComparison.Ordinal) && delete.Contains("[ Delete ]", StringComparison.Ordinal), "the delete page is served");
            var picture = await http.GetAsync($"http://127.0.0.1:{port}/Pics/1.png");
            check(picture.StatusCode == HttpStatusCode.OK && picture.Content.Headers.ContentType?.MediaType == "image/png", "catalog pictures are served");
        }
        finally
        {
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
                await server.WaitForExitAsync();
            }
        }

        var launch = ConvertedAppLauncher.Create(conversion);
        check(launch.Kind == ProgramPreviewKind.Web && launch.Arguments.Contains("--urls") && launch.Url is not null && launch.Url.Contains("{port}", StringComparison.Ordinal), "the converted app launches as a proxied web preview");

        var grid = ConvertGrid(Path.Combine(root, "grid"));
        var gridPage = grid.ProjectDirectory is null ? "" : File.ReadAllText(Directory.EnumerateFiles(grid.ProjectDirectory, "*.razor", SearchOption.AllDirectories).First(path => path.EndsWith("Default.razor", StringComparison.Ordinal)));
        check(grid.Succeeded && gridPage.Contains("<table", StringComparison.Ordinal) && gridPage.Contains("<th>Name</th>", StringComparison.Ordinal) && gridPage.Contains("@onclick", StringComparison.Ordinal), "a GridView page converts columns, a text box, and a button");
    }

    private static WebFormsConversionResult ConvertGrid(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Sample.csproj"), """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <ProjectTypeGuids>{349c5851-65df-11da-9384-00065b846f21};{fae04ec0-301f-11d3-bf4b-00c04f79efbc}</ProjectTypeGuids>
                <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
                <OutputType>Library</OutputType>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="System.Web" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(folder, "Web.config"), """
            <configuration>
              <connectionStrings>
                <add name="App" connectionString="Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=Grid" providerName="System.Data.SqlClient" />
              </connectionStrings>
              <appSettings>
                <add key="Title" value="Grid" />
              </appSettings>
            </configuration>
            """);
        File.WriteAllText(Path.Combine(folder, "Default.aspx"), """
            <%@ Page Language="C#" AutoEventWireup="true" CodeBehind="Default.aspx.cs" Inherits="Sample.Default" %>
            <html><body>
            <asp:TextBox ID="Name" runat="server" />
            <asp:RequiredFieldValidator runat="server" ControlToValidate="Name" ErrorMessage="Name is required." />
            <asp:Button ID="Save" runat="server" Text="Save" OnClick="Save_Click" />
            <asp:GridView ID="Items" runat="server" AutoGenerateColumns="false" ItemType="Sample.Row">
              <Columns>
                <asp:BoundField DataField="Name" HeaderText="Name" />
                <asp:BoundField DataField="Price" HeaderText="Price" />
              </Columns>
            </asp:GridView>
            </body></html>
            """);
        File.WriteAllText(Path.Combine(folder, "Default.aspx.cs"), """
            using System;
            using System.Collections.Generic;
            namespace Sample
            {
                public partial class Default : System.Web.UI.Page
                {
                    protected void Page_Load(object sender, EventArgs e)
                    {
                        Items.DataSource = new List<Row> { new Row { Name = "One", Price = 1 } };
                    }
                    protected void Save_Click(object sender, EventArgs e)
                    {
                        if (this.ModelState.IsValid) { Response.Redirect("~"); }
                    }
                }
                public class Row { public string Name { get; set; } public int Price { get; set; } }
            }
            """);
        return WebFormsBlazorConverter.Convert(LegacyProjectIntake.Inspect(folder), Path.Combine(folder, "out"));
    }

    private static void WriteWinForms(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Win.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net472</TargetFramework>
                <UseWindowsForms>true</UseWindowsForms>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(folder, "MainForm.cs"), "using System.Windows.Forms;\npublic class MainForm : Form { }\n");
    }

    private static void WriteWpf(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Wpf.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net472</TargetFramework>
                <UseWPF>true</UseWPF>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(folder, "MainWindow.xaml"), "<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"></Window>");
    }

    private static void WriteMvc(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "Controllers"));
        Directory.CreateDirectory(Path.Combine(folder, "Views"));
        File.WriteAllText(Path.Combine(folder, "Mvc.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net472</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="System.Web.Mvc" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(folder, "Controllers", "HomeController.cs"), "public class HomeController { }\n");
        File.WriteAllText(Path.Combine(folder, "Views", "Index.cshtml"), "<h1>Home</h1>");
    }

    private static async Task<string> WaitFor(HttpClient http, string url)
    {
        var deadline = DateTime.UtcNow.AddSeconds(40);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try { return await http.GetStringAsync(url); }
            catch (Exception ex) { last = ex; await Task.Delay(300); }
        }
        throw last ?? new TimeoutException(url);
    }

    private static string FindDotnet()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory, "dotnet");
                if (File.Exists(candidate)) return candidate;
            }
        }
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
        return File.Exists(home) ? home : "dotnet";
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(int ExitCode, string Output)> Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var pair in environment) process.StartInfo.Environment[pair.Key] = pair.Value;
        }
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancel = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cancel.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await stdout + await stderr);
    }
}
