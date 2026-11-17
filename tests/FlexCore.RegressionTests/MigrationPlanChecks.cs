using Fx.ControlKit.Migration;

internal static class MigrationPlanChecks
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "fx-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var page = Path.Combine(root, "Create.aspx");
            File.WriteAllText(page, "<%@ Page Language=\"C#\" %><asp:Label runat=\"server\" />");
            var project = new LegacyProject
            {
                ProjectFile = Path.Combine(root, "Shop.csproj"),
                Directory = root,
                Name = "Shop",
                Kind = LegacyProjectKind.WebForms,
                Language = "C#",
                Pages = [new LegacyPage { Path = page, Title = "Create" }],
                ConnectionStrings = [new LegacyConnectionString("db", "Data Source=(localdb)\\v11.0;Initial Catalog=Shop", "System.Data.SqlClient", "Web.config")]
            };
            var plan = MigrationPlanner.FromInventory(new LegacyProjectInventory { Root = root, Projects = [project] });
            check(plan.Steps.Any(step => step.Kind == MigrationPlanStepKind.File && step.SourcePath == page), "a page becomes a plan step");
            check(plan.Steps.Any(step => step.Kind == MigrationPlanStepKind.Risk), "SQL Server is a risk step");
            check(plan.Assumptions.Any(item => item.Contains("SQLite", StringComparison.Ordinal)), "the plan assumes SQLite when SQL Server is named");

            var file = plan.Steps.First(step => step.SourcePath == page);
            MigrationPlanEditor.UpdateText(plan, file.Id, "Create page, edited", null);
            MigrationPlanEditor.SetNotes(plan, file.Id, "Keep the catalog grid.");
            MigrationPlanEditor.SetEnabled(plan, file.Id, false);
            var added = MigrationPlanEditor.Add(plan, "Review the seed", "Confirm CSV before convert.");
            check(MigrationPlanEditor.Move(plan, added.Id, -1), "a step can move up");
            check(plan.Steps.Select(step => step.Order).SequenceEqual(Enumerable.Range(1, plan.Steps.Count)), "orders stay contiguous after an edit");
            var prompts = MigrationPlanPrompts.Create(plan);
            check(prompts.All(prompt => prompt.StepId != file.Id), "a disabled step is left out of the prompts");
            check(prompts.Any(prompt => prompt.Text.Contains("Review the seed", StringComparison.Ordinal)), "an added step becomes a prompt");

            var json = MigrationPlanDocument.ToJson(plan);
            var markdown = MigrationPlanDocument.ToMarkdown(plan);
            var fromJson = MigrationPlanDocument.FromJson(json);
            var fromMarkdown = MigrationPlanDocument.FromMarkdown(markdown);
            check(fromJson.Steps.Count == plan.Steps.Count && fromJson.Steps.Any(step => step.Notes == "Keep the catalog grid."), "JSON keeps the edited note");
            check(fromMarkdown.ProjectName == "Shop" && fromMarkdown.Steps.Any(step => step.Title == "Create page, edited"), "Markdown round-trips the edited title");

            var narrated = MigrationPlanner.FromFiles("Tools", "Perl 5", "Python 3", [("lib/Csv.pm", "eval $code;")], (_, detail) => detail + " Reviewed.");
            check(narrated.Steps.Any(step => step.Detail.Contains("Reviewed.", StringComparison.Ordinal)), "a narrator can rewrite step text");
            check(narrated.Steps.Any(step => step.Kind == MigrationPlanStepKind.Risk && step.Title.Contains("eval", StringComparison.Ordinal)), "eval in a source file is a risk");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
