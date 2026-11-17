using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.Migration;

/// <summary>What a plan step describes.</summary>
public enum MigrationPlanStepKind
{
    File,
    Module,
    Risk,
    Assumption,
    Manual
}

/// <summary>One editable step. Disabled steps stay in the plan and are left out of the prompts.</summary>
public sealed class MigrationPlanStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
    public MigrationPlanStepKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Module { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> Risks { get; set; } = [];
    public List<string> Assumptions { get; set; } = [];
}

/// <summary>A migration plan for one uploaded inventory. The host edits it before conversion.</summary>
public sealed class MigrationPlan
{
    public string ProjectName { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string TargetLabel { get; set; } = "";
    public List<string> Assumptions { get; set; } = [];
    public List<MigrationPlanStep> Steps { get; set; } = [];
}

/// <summary>The prompt a converter receives for one enabled step.</summary>
public sealed record MigrationPlanPrompt(string StepId, string Title, string Text);

/// <summary>Builds a deterministic plan. An optional narrator may rewrite step text when a model is configured.</summary>
public static class MigrationPlanner
{
    public delegate string? Narrator(string stepTitle, string deterministicDetail);

    public static MigrationPlan FromInventory(LegacyProjectInventory inventory, string targetLabel = "Blazor", Narrator? narrator = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var project = inventory.PrimaryWebFormsProject ?? inventory.Projects.FirstOrDefault();
        var plan = new MigrationPlan
        {
            ProjectName = project?.Name ?? Path.GetFileName(inventory.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            SourceLabel = project?.Kind.ToString() ?? "Legacy",
            TargetLabel = targetLabel
        };
        plan.Assumptions.Add("The plan is rule-based. A configured language model may rewrite the step text; it does not add files.");
        plan.Assumptions.Add("Disabled steps are kept for the record and are not sent to the converter.");
        if (project?.UsesSqlServer == true)
            plan.Assumptions.Add("SQL Server and LocalDB are not opened on this host. Data moves to SQLite.");
        plan.Assumptions.Add("The original site is not executed here unless the host supplies a remote URL.");

        if (project is null)
        {
            plan.Steps.Add(Step(MigrationPlanStepKind.Manual, "No project was classified",
                "Inventory did not find a project file. Add the missing project or stop.", ""));
            return Finish(plan, narrator);
        }

        plan.Steps.Add(Step(MigrationPlanStepKind.Module, "Convert " + project.Name, 
            $"{project.Kind} project ({project.Language}, {project.TargetFramework ?? "unspecified framework"}) becomes a {targetLabel} project.",
            project.Name, project.ProjectFile, TargetProject(project)));

        foreach (var master in project.Masters)
            plan.Steps.Add(FileStep(project, "Master", master.Path, "Components/Layout/" + RazorName(master.Path),
                "Shared chrome. Content placeholders become @Body."));
        foreach (var page in project.Pages)
            plan.Steps.Add(FileStep(project, "Page", page.Path, RazorName(page.Path),
                "Page markup and its code-behind become one Razor component."));
        foreach (var control in project.UserControls)
            plan.Steps.Add(FileStep(project, "User control", control.Path, "Components/" + RazorName(control.Path),
                "User control becomes a child component."));
        foreach (var data in project.DataAccess)
            plan.Steps.Add(FileStep(project, "Data access", data, Path.GetFileName(data),
                "Data access is rewritten for the target data API. SQL built by concatenation is called out as a risk."));
        if (project.SeedData.Count > 0)
            plan.Steps.Add(Step(MigrationPlanStepKind.Module, "Seed the target database",
                "Seed from " + string.Join(", ", project.SeedData.Select(seed => seed.Kind + " " + seed.Path)) + ".",
                project.Name));
        if (project.UsesSqlServer)
            plan.Steps.Add(Risk(project, "SQL Server connection",
                "A connection string names SQL Server or LocalDB. The converted app uses SQLite and does not contact that server.",
                "CWE-1188"));

        foreach (var path in project.Pages.Select(page => page.Path).Concat(project.CodeBehind).Concat(project.DataAccess).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path);
            if (Regex.IsMatch(text, @"\beval\s*\(|Process\.Start|ExecuteNonQuery\s*\(\s*""", RegexOptions.IgnoreCase))
                plan.Steps.Add(Risk(project, "Risky construct in " + Path.GetFileName(path),
                    "The file contains eval, a process launch, or a SQL string that the converter must not widen.",
                    path));
        }

        return Finish(plan, narrator);
    }

    /// <summary>A plan for a folder of source files when there is no Web Forms inventory.</summary>
    public static MigrationPlan FromFiles(string projectName, string sourceLabel, string targetLabel, IReadOnlyList<(string Path, string Content)> files, Narrator? narrator = null)
    {
        var plan = new MigrationPlan
        {
            ProjectName = projectName,
            SourceLabel = sourceLabel,
            TargetLabel = targetLabel,
            Assumptions = ["Each enabled file is one converter prompt.", "Rule-based risks come from the source text."]
        };
        var modules = files.Select(file => ModuleOf(file.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
            plan.Steps.Add(Step(MigrationPlanStepKind.Module, "Module " + module, $"Convert the {module} module to {targetLabel} before the files that depend on it.", module));
        foreach (var file in files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            var risks = RiskyConstructs(file.Content);
            plan.Steps.Add(new MigrationPlanStep
            {
                Kind = MigrationPlanStepKind.File,
                Title = "Convert " + Path.GetFileName(file.Path),
                Detail = $"Translate {file.Path} into {targetLabel}. Keep behaviour; do not invent features.",
                Module = ModuleOf(file.Path),
                SourcePath = file.Path,
                TargetPath = TargetFor(file.Path, targetLabel),
                Risks = risks
            });
            foreach (var risk in risks)
                plan.Steps.Add(Risk(null, risk + " in " + Path.GetFileName(file.Path), risk + " must be preserved as a finding, not silently dropped.", file.Path));
        }
        return Finish(plan, narrator);
    }

    private static MigrationPlan Finish(MigrationPlan plan, Narrator? narrator)
    {
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            plan.Steps[i].Order = i + 1;
            if (narrator is null) continue;
            var rewritten = narrator(plan.Steps[i].Title, plan.Steps[i].Detail);
            if (!string.IsNullOrWhiteSpace(rewritten))
                plan.Steps[i].Detail = rewritten.Trim();
        }
        return plan;
    }

    private static MigrationPlanStep FileStep(LegacyProject project, string kind, string path, string target, string detail) => new()
    {
        Kind = MigrationPlanStepKind.File,
        Title = kind + " " + Path.GetFileName(path),
        Detail = detail,
        Module = project.Name,
        SourcePath = path,
        TargetPath = target
    };

    private static MigrationPlanStep Risk(LegacyProject? project, string title, string detail, string path) => new()
    {
        Kind = MigrationPlanStepKind.Risk,
        Title = title,
        Detail = detail,
        Module = project?.Name ?? "",
        SourcePath = path,
        Risks = [title]
    };

    private static MigrationPlanStep Step(MigrationPlanStepKind kind, string title, string detail, string module, string source = "", string target = "") => new()
    {
        Kind = kind,
        Title = title,
        Detail = detail,
        Module = module,
        SourcePath = source,
        TargetPath = target
    };

    private static string TargetProject(LegacyProject project) => project.Name + ".csproj";

    private static string RazorName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name + ".razor";
    }

    private static string ModuleOf(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^2] : "root";
    }

    private static string TargetFor(string path, string targetLabel)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = targetLabel.Contains("Python", StringComparison.OrdinalIgnoreCase) ? ".py" : ".cs";
        return name + ext;
    }

    private static List<string> RiskyConstructs(string content)
    {
        var risks = new List<string>();
        void Hit(string pattern, string name)
        {
            if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase)) risks.Add(name);
        }
        Hit(@"\beval\b|\bexec\b|`", "eval or command execution");
        Hit(@"system\s*\(|Process\.Start|subprocess", "process launch");
        Hit(@"SELECT\s+.+\+|INSERT\s+.+\+|execute\s*\(\s*[""'].*\%", "SQL built by concatenation");
        Hit(@"password\s*=\s*[""'][^""']+[""']|api[_-]?key\s*=\s*[""']", "hardcoded secret");
        return risks;
    }
}

/// <summary>Edits a plan in place and keeps step order contiguous.</summary>
public static class MigrationPlanEditor
{
    public static MigrationPlanStep? Find(MigrationPlan plan, string id) =>
        plan.Steps.FirstOrDefault(step => step.Id == id);

    public static void UpdateText(MigrationPlan plan, string id, string? title, string? detail)
    {
        var step = Require(plan, id);
        if (title is not null) step.Title = title.Trim();
        if (detail is not null) step.Detail = detail.Trim();
    }

    public static void SetNotes(MigrationPlan plan, string id, string notes) => Require(plan, id).Notes = notes ?? "";

    public static void SetEnabled(MigrationPlan plan, string id, bool enabled) => Require(plan, id).Enabled = enabled;

    public static bool Move(MigrationPlan plan, string id, int delta)
    {
        var index = plan.Steps.FindIndex(step => step.Id == id);
        if (index < 0) return false;
        var target = index + delta;
        if (target < 0 || target >= plan.Steps.Count) return false;
        (plan.Steps[index], plan.Steps[target]) = (plan.Steps[target], plan.Steps[index]);
        Renumber(plan);
        return true;
    }

    public static MigrationPlanStep Add(MigrationPlan plan, string title, string detail, MigrationPlanStepKind kind = MigrationPlanStepKind.Manual)
    {
        var step = new MigrationPlanStep { Kind = kind, Title = title.Trim(), Detail = detail.Trim(), Order = plan.Steps.Count + 1 };
        plan.Steps.Add(step);
        Renumber(plan);
        return step;
    }

    public static bool Remove(MigrationPlan plan, string id)
    {
        var removed = plan.Steps.RemoveAll(step => step.Id == id) > 0;
        if (removed) Renumber(plan);
        return removed;
    }

    private static MigrationPlanStep Require(MigrationPlan plan, string id) =>
        Find(plan, id) ?? throw new InvalidOperationException("No plan step has id " + id);

    private static void Renumber(MigrationPlan plan)
    {
        for (var i = 0; i < plan.Steps.Count; i++) plan.Steps[i].Order = i + 1;
    }
}

/// <summary>Turns an approved plan into one prompt per enabled step.</summary>
public static class MigrationPlanPrompts
{
    public static IReadOnlyList<MigrationPlanPrompt> Create(MigrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var prompts = new List<MigrationPlanPrompt>();
        foreach (var step in plan.Steps.Where(step => step.Enabled).OrderBy(step => step.Order))
        {
            var text = new StringBuilder();
            text.Append("Convert ").Append(plan.SourceLabel).Append(" to ").Append(plan.TargetLabel).AppendLine(".");
            text.Append("Project: ").AppendLine(plan.ProjectName);
            text.Append("Step ").Append(step.Order).Append(": ").AppendLine(step.Title);
            if (step.Detail.Length > 0) text.AppendLine(step.Detail);
            if (step.SourcePath.Length > 0) text.Append("Source: ").AppendLine(step.SourcePath);
            if (step.TargetPath.Length > 0) text.Append("Target: ").AppendLine(step.TargetPath);
            if (step.Notes.Length > 0) text.Append("Notes from the reviewer: ").AppendLine(step.Notes);
            if (step.Risks.Count > 0) text.Append("Risks to preserve, not to widen: ").AppendLine(string.Join("; ", step.Risks));
            if (plan.Assumptions.Count > 0) text.Append("Assumptions: ").AppendLine(string.Join("; ", plan.Assumptions));
            text.Append("Do not invent behaviour that is not in the source.");
            prompts.Add(new MigrationPlanPrompt(step.Id, step.Title, text.ToString().Trim()));
        }
        return prompts;
    }
}

/// <summary>JSON and Markdown for a plan saved with the project.</summary>
public static class MigrationPlanDocument
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string ToJson(MigrationPlan plan) => JsonSerializer.Serialize(plan, JsonOptions);

    public static MigrationPlan FromJson(string json) =>
        JsonSerializer.Deserialize<MigrationPlan>(json, JsonOptions) ?? throw new InvalidDataException("The plan JSON was empty.");

    public static string ToMarkdown(MigrationPlan plan)
    {
        var text = new StringBuilder();
        text.Append("# Migration plan: ").AppendLine(plan.ProjectName);
        text.Append("Source: ").AppendLine(plan.SourceLabel);
        text.Append("Target: ").AppendLine(plan.TargetLabel);
        text.AppendLine();
        text.AppendLine("## Assumptions");
        foreach (var assumption in plan.Assumptions) text.Append("- ").AppendLine(assumption);
        text.AppendLine();
        text.AppendLine("## Steps");
        foreach (var step in plan.Steps.OrderBy(step => step.Order))
        {
            text.Append("### ").Append(step.Order).Append(". ").AppendLine(step.Title);
            text.Append("- id: ").AppendLine(step.Id);
            text.Append("- kind: ").AppendLine(step.Kind.ToString());
            text.Append("- enabled: ").AppendLine(step.Enabled ? "true" : "false");
            text.Append("- module: ").AppendLine(step.Module);
            text.Append("- source: ").AppendLine(step.SourcePath);
            text.Append("- target: ").AppendLine(step.TargetPath);
            text.Append("- notes: ").AppendLine(step.Notes);
            if (step.Risks.Count > 0) text.Append("- risks: ").AppendLine(string.Join("; ", step.Risks));
            text.AppendLine(step.Detail);
            text.AppendLine();
        }
        return text.ToString();
    }

    public static MigrationPlan FromMarkdown(string markdown)
    {
        var plan = new MigrationPlan();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        MigrationPlanStep? current = null;
        var assumptions = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("# Migration plan:", StringComparison.Ordinal))
            {
                plan.ProjectName = line["# Migration plan:".Length..].Trim();
                assumptions = false;
                continue;
            }
            if (line.StartsWith("Source:", StringComparison.Ordinal) && current is null)
            {
                plan.SourceLabel = line["Source:".Length..].Trim();
                continue;
            }
            if (line.StartsWith("Target:", StringComparison.Ordinal) && current is null && plan.TargetLabel.Length == 0)
            {
                plan.TargetLabel = line["Target:".Length..].Trim();
                continue;
            }
            if (line.Equals("## Assumptions", StringComparison.Ordinal)) { assumptions = true; current = null; continue; }
            if (line.Equals("## Steps", StringComparison.Ordinal)) { assumptions = false; continue; }
            if (assumptions && line.StartsWith("- ", StringComparison.Ordinal))
            {
                plan.Assumptions.Add(line[2..].Trim());
                continue;
            }
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                current = new MigrationPlanStep();
                var title = line[4..];
                var dot = title.IndexOf(". ", StringComparison.Ordinal);
                if (dot > 0 && int.TryParse(title[..dot], out var order))
                {
                    current.Order = order;
                    current.Title = title[(dot + 2)..].Trim();
                }
                else current.Title = title.Trim();
                plan.Steps.Add(current);
                continue;
            }
            if (current is null) continue;
            if (Take(line, "- id: ", out var id)) current.Id = id;
            else if (Take(line, "- kind: ", out var kind) && Enum.TryParse<MigrationPlanStepKind>(kind, out var parsed)) current.Kind = parsed;
            else if (Take(line, "- enabled: ", out var enabled)) current.Enabled = enabled.Equals("true", StringComparison.OrdinalIgnoreCase);
            else if (Take(line, "- module: ", out var module)) current.Module = module;
            else if (Take(line, "- source: ", out var source)) current.SourcePath = source;
            else if (Take(line, "- target: ", out var target)) current.TargetPath = target;
            else if (Take(line, "- notes: ", out var notes)) current.Notes = notes;
            else if (Take(line, "- risks: ", out var risks)) current.Risks = risks.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            else if (line.Length > 0 && !line.StartsWith("- ", StringComparison.Ordinal)) current.Detail = current.Detail.Length == 0 ? line : current.Detail + "\n" + line;
        }
        return plan;
    }

    private static bool Take(string line, string prefix, out string value)
    {
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) { value = ""; return false; }
        value = line[prefix.Length..].Trim();
        return true;
    }
}
