using Fx.ControlKit;
using Fx.ControlKit.Grid;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Forms;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;

internal static class TreeChecks
{
    private static ParameterView P(params (string Name, object? Value)[] values) =>
        ParameterView.FromDictionary(values.ToDictionary(v => v.Name, v => v.Value));
    private sealed class Draft { public string Text { get; set; } = ""; }
    public sealed record Record(int Id, int? ParentId, string Name, string Team, decimal Budget);
    private static void Reject(Action action, Action<bool, string> check, string name)
    {
        try { action(); } catch (ArgumentException) { check(true, name); return; }
        catch (InvalidOperationException) { check(true, name); return; }
        throw new Exception(name);
    }

    public static async Task Run(HtmlRenderer renderer, List<IComponent> components, Action<bool, string> check)
    {
        var draft = new Draft(); var context = new EditContext(draft); var fieldChanges = 0;
        context.OnFieldChanged += (_, _) => fieldChanges++;
        var inputCount = components.OfType<InputText>().Count();
        await renderer.RenderComponentAsync<CascadingValue<EditContext>>(P(("Value", context), ("ChildContent", (RenderFragment)(b =>
        {
            b.OpenComponent<TextBoxControl>(0);
            b.AddAttribute(1, "Value", draft.Text);
            b.AddAttribute(2, "ValueChanged", EventCallback.Factory.Create<string?>(new object(), value => draft.Text = value ?? ""));
            b.AddAttribute(3, "ValueExpression", (Expression<Func<string?>>)(() => draft.Text));
            b.AddAttribute(4, "UpdateOnInput", true);
            b.CloseComponent();
        }))));
        var textbox = components.OfType<TextBoxControl>().Last();
        check(components.OfType<InputText>().Count() == inputCount, "bound TextBox UpdateOnInput uses the input-event path");
        await (Task)typeof(TextBoxControl).GetMethod("OnNativeInput", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(textbox, ["Every character"] )!;
        check(draft.Text == "Every character" && fieldChanges == 1 && context.IsModified(), "live bound TextBox updates its model and notifies EditContext once");

        var flat = new[] { new Record(2, 1, "Child", "A", 2), new Record(1, null, "Root", "A", 0), new Record(3, 99, "Orphan", "B", 3) };
        var nodes = TreeViewData.FromFlat(flat, r => r.Id.ToString(), r => r.ParentId?.ToString(), r => r.Name);
        check(nodes.Count == 2 && nodes[0].Children.Single().Id == "2" && ReferenceEquals(nodes[0].Tag, flat[1]), "flat tree mapping handles children before parents and orphans with original item tags");
        Reject(() => TreeViewData.FromFlat(new[] { flat[0], flat[0] }, r => r.Id.ToString(), r => r.ParentId?.ToString(), r => r.Name), check, "flat tree rejects duplicate IDs");
        Reject(() => TreeViewData.FromFlat(new[] { flat[0], flat[1] with { ParentId = 2 } }, r => r.Id.ToString(), r => r.ParentId?.ToString(), r => r.Name), check, "flat tree rejects disconnected parent cycles");
        var parent = new TreeNode { Id = "p", Text = "Parent", Children = [new() { Id = "a" }, new() { Id = "b" }, new() { Id = "disabled", IsDisabled = true }] };
        nodes = [parent, new() { Id = "target", Text = "Target" }];
        TreeViewData.SetChecked(nodes, parent.Children[0], true, true);
        check(parent.IsIndeterminate && !parent.IsChecked, "tree mixed state rolls up a partially checked branch");
        TreeViewData.SetChecked(nodes, parent, true, true);
        check(parent.IsChecked && parent.Children[1].IsChecked && !parent.Children[2].IsChecked, "tree cascades through enabled descendants only");
        TreeViewData.SetChecked(nodes, parent.Children[0], false, false);
        check(parent.IsChecked && !parent.Children[0].IsChecked && !parent.IsIndeterminate, "independent checkbox mode preserves parent flags");
        var before = TreeViewData.Flatten(nodes).Select(n => n.Node.Id).ToArray();
        Reject(() => TreeViewData.Move(nodes, "p", "a", TreeDropPosition.Inside), check, "reparent rejects descendant cycles");
        check(TreeViewData.Flatten(nodes).Select(n => n.Node.Id).SequenceEqual(before), "rejected reparent leaves tree intact");
        TreeViewData.Move(nodes, "a", "target", TreeDropPosition.Inside);
        check(nodes[1].Children.Single().Id == "a" && nodes[1].IsExpanded && parent.Children.Count == 2, "move transfers subtree and expands new parent");
        TreeViewData.Move(nodes, "a", "p", TreeDropPosition.Before);
        check(nodes[0].Id == "a" && nodes.Count == 3, "before move can promote child to root");
        var sorted = TreeViewData.Flatten(nodes, Comparer<TreeNode>.Create((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id)));
        check(sorted[0].Position == 1 && sorted[0].SetSize == 3 && sorted[2].Parent?.Id == "p", "custom sibling comparer preserves hierarchy metadata");

        check(TreeGridQuery.Matches(20m, ColumnType.Number, new("Budget", TextFilterOperator.GreaterThan, "3")), "tree numeric filter compares values rather than lexical strings");
        check(!TreeGridQuery.Matches(20m, ColumnType.Number, new("Budget", TextFilterOperator.GreaterThan, "invalid")), "invalid numeric criteria match no rows");
        check(TreeGridQuery.Matches(20m, ColumnType.Number, new("Budget", TextFilterOperator.GreaterThan, "10", TextFilterOperator.LessThan, "30")), "tree dual numeric filter applies AND");
        check(TreeGridQuery.Matches(3m, ColumnType.Number, new("Budget", TextFilterOperator.Contains, null, TextFilterOperator.Equals, "3", LogicalFilterOperator.Or))
            && !TreeGridQuery.Matches(4m, ColumnType.Number, new("Budget", TextFilterOperator.Contains, null, TextFilterOperator.Equals, "3", LogicalFilterOperator.Or)), "a lone second filter condition is applied even with OR");
        check(TreeGridQuery.Matches(3m, ColumnType.Number, new("Budget", TextFilterOperator.Equals, "3", TextFilterOperator.Equals, "100", LogicalFilterOperator.Or)), "tree dual numeric filter applies OR");
        check(TreeGridQuery.Matches(new DateTime(2026, 9, 6, 15, 0, 0), ColumnType.Date, new("Due", TextFilterOperator.Equals, "2026-09-06")), "tree date equality ignores time of day");
        check(TreeGridQuery.Matches(true, ColumnType.Boolean, new("Active", TextFilterOperator.Equals, "true")), "tree boolean criteria compare typed values");
        check(TreeGridQuery.Matches(null, ColumnType.Text, new("Name", TextFilterOperator.IsEmpty)) && !TreeGridQuery.Matches(null, ColumnType.Number, new("Budget", TextFilterOperator.Equals, "0")), "blank filters distinguish null from zero");
        check(TreeGridQuery.Matches("Alpha", ColumnType.Text, new("Name", TextFilterOperator.Contains, "alpha")) && !TreeGridQuery.Matches("Alpha", ColumnType.Text, new("Name", TextFilterOperator.Contains, "alpha"), true), "tree text matching supports explicit case sensitivity");

        var lazy = new TreeNode { Id = "lazy", Text = "<em>Lazy</em>", HasUnloadedChildren = true, IsChecked = true };
        var calls = 0;
        Func<TreeNode, CancellationToken, Task<IReadOnlyList<TreeNode>>> loader = (_, _) =>
        {
            calls++;
            if (calls == 1) throw new InvalidOperationException("Retry me");
            return Task.FromResult<IReadOnlyList<TreeNode>>([new() { Id = "child", Text = "Child" }]);
        };
        var treeHtml = await renderer.RenderComponentAsync<TreeViewControl>(P(("Nodes", new List<TreeNode> { lazy }), ("LoadChildren", loader), ("AutoCheck", true), ("AllowEditing", true)));
        var tree = components.OfType<TreeViewControl>().Last();
        await tree.SetExpandedAsync("lazy", true);
        check(lazy.HasUnloadedChildren && !lazy.IsExpanded && treeHtml.ToHtmlString().Contains("Retry me"), "failed TreeView child load exposes retryable error");
        await tree.SetExpandedAsync("lazy", true);
        check(!lazy.HasUnloadedChildren && lazy.Children.Single().IsChecked && lazy.IsExpanded, "TreeView lazy children inherit checked parent");
        await tree.SetExpandedAsync("lazy", false); await tree.SetExpandedAsync("lazy", true);
        check(calls == 2, "TreeView lazy success is cached across expansion");
        check(!treeHtml.ToHtmlString().Contains("<em>Lazy</em>") && treeHtml.ToHtmlString().Contains("&lt;em&gt;Lazy"), "TreeView labels are encoded by default");
        await tree.AddNodeAsync(new() { Id = "added", Text = "Added" }, "lazy");
        await tree.SelectNodesAsync(["added"]); await tree.RemoveNodeAsync("added");
        check(tree.SelectedNode is null && lazy.Children.Count == 1, "removing selected subtree clears primary selection");
        var edited = 0;
        tree.NodeEditing = EventCallback.Factory.Create<TreeNodeEditEventArgs>(new object(), e => e.Text = "Renamed");
        tree.NodeEditCommitting = EventCallback.Factory.Create<TreeNodeEditEventArgs>(new object(), e => e.Error = "Rejected");
        tree.NodeEdited = EventCallback.Factory.Create<TreeNodeEditEventArgs>(new object(), e => { edited++; check(e.Node.Text == e.Text, "TreeView edited event observes committed model"); });
        await tree.BeginEditAsync("child"); await tree.CommitEditAsync();
        check(lazy.Children[0].Text == "Child" && edited == 0, "rename validation veto retains old model and skips committed event");
        tree.NodeEditCommitting = default; await tree.CommitEditAsync();
        check(lazy.Children[0].Text == "Renamed" && edited == 1, "rename commit updates once after validation");

        await tree.SetParametersAsync(P(("FilterText", "no matches")));
        await tree.BeginEditAsync("child");
        check(treeHtml.ToHtmlString().Contains("Clear the tree filter before editing"), "hidden selection cannot start an invisible tree editor");
        await tree.SetParametersAsync(P(("FilterText", "")));
        var completion = new TaskCompletionSource<IReadOnlyList<TreeNode>>();
        tree.LoadChildren = (_, _) => completion.Task;
        var pending = new TreeNode { Id = "pending", HasUnloadedChildren = true };
        await tree.AddNodeAsync(pending);
        var expansion = tree.SetExpandedAsync("pending", true);
        await tree.SetExpandedAsync("pending", false);
        completion.SetResult([new() { Id = "pending-child" }]);
        await expansion;
        check(!pending.IsExpanded && !pending.HasUnloadedChildren && pending.Children.Count == 1, "collapse during TreeView loading preserves the final expansion intent");

        var rows = new[] { new Record(1, null, "Root", "A", 100), new Record(2, 1, "Task B", "A", 20), new Record(3, 1, "Task A", "A", 3), new Record(4, 1, "Task C", "A", 20), new Record(5, null, "Other", "B", 0) };
        var gridHtml = await renderer.RenderComponentAsync<TreeGridControl<Record>>(P(("DataSource", rows), ("IdMapping", "Id"), ("ParentIdMapping", "ParentId"), ("EnableCollapseAll", true), ("AllowSorting", true), ("AllowFiltering", true), ("AllowPaging", true), ("PageSize", 2)));
        var grid = components.OfType<TreeGridControl<Record>>().Last();
        grid.AddColumn(new() { Field = "Name" }); grid.AddColumn(new() { Field = "Team" }); grid.AddColumn(new() { Field = "Budget", Type = ColumnType.Number });
        check(grid.GetVisibleRecords().Select(r => r.Id).SequenceEqual([1, 5]), "collapsed TreeGrid starts with roots only");
        await grid.SetFilterAsync(new("Name", TextFilterOperator.Contains, "Task"));
        check(grid.GetVisibleRecords().Select(r => r.Id).SequenceEqual([1, 2, 3, 4]), "TreeGrid filter reveals matching collapsed descendants and ancestors");
        grid.FilterHierarchyMode = TreeGridFilterHierarchyMode.None;
        await grid.SetFilterAsync(new("Budget", TextFilterOperator.GreaterThan, "10"));
        check(grid.GetVisibleRecords().Select(r => r.Id).SequenceEqual([2, 4]), "TreeGrid combines typed filters with no relatives");
        await grid.ClearFiltersAsync(); await grid.ExpandAllAsync();
        await grid.SetSortsAsync([new("Team", SortDirection.Ascending), new("Budget", SortDirection.Descending)]);
        check(grid.GetVisibleRecords().Select(r => r.Id).SequenceEqual([1, 2, 4, 3, 5]), "TreeGrid stable multi-sort preserves tied sibling input order");
        await grid.GoToPageAsync(2);
        var state = JsonSerializer.Deserialize<TreeGridViewState>(JsonSerializer.Serialize(grid.CaptureState()))!;
        await grid.CollapseAllAsync(); await grid.SetSortsAsync([]); await grid.SetFilterAsync(new("Name", TextFilterOperator.Contains, "absent"));
        await grid.RestoreStateAsync(state);
        check(grid.CurrentPage == 2 && grid.GetVisibleRecords().Select(r => r.Id).SequenceEqual([1, 2, 4, 3, 5]), "TreeGrid JSON state restores page, expansion, sorts and filters");
        var export = grid.Export(GridExportFormat.Csv);
        check(Encoding.UTF8.GetString(export.Bytes).Contains("Task A") && Encoding.UTF8.GetString(export.Bytes).Contains("Other"), "TreeGrid exports the complete visible query across pages");
        check(gridHtml.ToHtmlString().Contains("role=\"treegrid\"") && gridHtml.ToHtmlString().Contains("aria-level=\"2\""), "TreeGrid exposes row levels and treegrid semantics");
        await grid.SetFilterAsync(new("Budget", TextFilterOperator.IsEmpty)); await grid.ClearFiltersAsync();
        check(grid.GetFilters().Count == 0 && grid.GetVisibleRecords().Count == 5, "clear filters removes valueless blank operators too");
        var badProvider = 0;
        Func<Record, CancellationToken, Task<IReadOnlyList<Record>>> gridLoader = (_, _) =>
        {
            badProvider++;
            return Task.FromResult<IReadOnlyList<Record>>([new Record(9, badProvider == 1 ? 88 : 5, "Loaded", "B", 9)]);
        };
        grid.TreatAsParent = r => r.Id == 5; grid.LoadChildren = gridLoader;
        // Rebuild after changing the parent hint, then explicitly expand.
        await grid.SetSortsAsync([]); await grid.SetExpandedAsync(rows[4], false); await grid.SetExpandedAsync(rows[4], true);
        check(gridHtml.ToHtmlString().Contains("requested parent ID") && !grid.GetVisibleRecords().Any(r => r.Id == 9), "TreeGrid rejects provider children mapped to another parent");
        await grid.SetExpandedAsync(rows[4], true);
        check(grid.GetVisibleRecords().Any(r => r.Id == 9) && badProvider == 2, "TreeGrid child provider can retry after rejected data");
        var validIds = grid.GetVisibleRecords().Select(r => r.Id).ToArray();
        grid.DataSource = rows.Concat([rows[0]]);
        try { await grid.SetSortsAsync([]); throw new Exception("Duplicate tree ID accepted"); } catch (ArgumentException) { }
        check(grid.GetVisibleRecords().Select(r => r.Id).SequenceEqual(validIds), "invalid TreeGrid source does not discard the last valid hierarchy");
    }
}
