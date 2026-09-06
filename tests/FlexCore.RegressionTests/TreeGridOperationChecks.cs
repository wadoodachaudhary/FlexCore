using System.ComponentModel.DataAnnotations;
using Fx.ControlKit;
using Fx.ControlKit.Grid;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Forms;
using System.Text.Json;
internal static class TreeGridOperationChecks
{
    public sealed class Row
    {
        public int Id { get; set; }
        public int? ParentId { get; set; }
        [Required, MinLength(2)] public string Name { get; set; } = "Task";
        [Range(1, 100)] public decimal Amount { get; set; } = 5;
        public Row Copy() => new() { Id=Id, ParentId=ParentId, Name=Name, Amount=Amount };
    }
    static ParameterView P(params (string Name, object? Value)[] pairs) => ParameterView.FromDictionary(pairs.ToDictionary(p=>p.Name,p=>p.Value));
    public static async Task Run(HtmlRenderer renderer, List<IComponent> components, Action<bool,string> check)
    {
        var rows = new[] { new Row {Id=1}, new Row {Id=2,ParentId=1}, new Row {Id=3,ParentId=2},new Row {Id=4},new Row {Id=5,ParentId=1},new Row {Id=9} };
        var ordered = TreeGridHierarchy.Move(rows,2,4,TreeDropPosition.Inside,r=>r.Id,r=>r.ParentId,r=>r.Copy(),(r,p)=>r.ParentId=(int?)p);
        check(ordered.Select(r=>r.Id).SequenceEqual([1,5,4,2,3,9]) && ordered.Single(r=>r.Id==2).ParentId==4 && rows[1].ParentId==1, "reparent moves entire subtree without changing original source");
        try { TreeGridHierarchy.Move(rows,1,3,TreeDropPosition.Inside,r=>r.Id,r=>r.ParentId,r=>r.Copy(),(r,p)=>r.ParentId=(int?)p); throw new Exception("cycle allowed"); } catch(InvalidOperationException) { check(true,"descendant reparent is rejected"); }
        var after = TreeGridHierarchy.Move(rows,2,4,TreeDropPosition.After,r=>r.Id,r=>r.ParentId,r=>r.Copy(),(r,p)=>r.ParentId=(int?)p);
        check(after.Single(r=>r.Id==2).ParentId==null && after.Select(r=>r.Id).SequenceEqual([1,5,4,2,3,9]), "after move promotes a subtree to a root");
        var settings = new EditSettings {AllowEditing=true,AllowAdding=true,AllowDeleting=true,Mode=EditMode.Batch};
        var changes = new List<TreeGridDataChangeEventArgs<Row>>();
        var html = await renderer.RenderComponentAsync<TreeGridControl<Row>>(P(("DataSource",rows),("IdMapping","Id"),("ParentIdMapping","ParentId"),("EditSettingsRef",settings),("ShowCheckboxes",true),("EnableCollapseAll",true),("AllowRowDragAndDrop",true),("CanCheckRow",(Func<Row,bool>)(r=>r.Id!=5)),("DataChanged",EventCallback.Factory.Create<TreeGridDataChangeEventArgs<Row>>(new object(),e=>changes.Add(e)))));
        var grid = components.OfType<TreeGridControl<Row>>().Last();
        grid.AddColumn(new(){Field="Id",IsPrimaryKey=true}); grid.AddColumn(new(){Field="Name"}); grid.AddColumn(new(){Field="Amount",Type=ColumnType.Number});
        await grid.ExpandAllAsync(); await grid.CheckAllAsync();
        check(grid.GetCheckedRecords().Select(r=>r.Id).SequenceEqual([1,2,3,4,9]),"hierarchy check-all skips disabled nodes");
        await grid.SetRowCheckedAsync(3,false);
        check(!grid.GetCheckedRecords().Any(r=>r.Id is 1 or 2 or 3) && html.ToHtmlString().Contains("aria-checked=\"mixed\""),"unchecking grandchild rolls mixed state up all ancestors");
        await grid.SetRowCheckedAsync(1,true); await grid.CollapseAllAsync(); await grid.SetFilterAsync(new("Name",TextFilterOperator.Contains,"absent"));
        check(grid.GetCheckedRecords().Any(r=>r.Id==3),"collapse and filters do not lose checked descendants");
        await grid.ClearFiltersAsync(); await grid.ExpandAllAsync();
        check(await grid.BeginEditAsync(2,"Name"),"batch starts independent editable draft");
        ((Row)grid.CurrentEditContext!.Model).Name="First edit";
        check(rows[1].Name=="Task", "draft input leaves source unchanged");
        check(await grid.BeginEditAsync(4,"Amount"),"batch stages a row while entering another");
        ((Row)grid.CurrentEditContext!.Model).Amount=0;
        check(!await grid.SaveChangesAsync() && changes.Count==0 && grid.HasPendingChanges,"invalid batch atomically prevents all writes");
        ((Row)grid.CurrentEditContext.Model).Amount=10;
        grid.DataChanging=EventCallback.Factory.Create<TreeGridDataChangeEventArgs<Row>>(new object(),e=>e.Cancel=true);
        check(!await grid.SaveChangesAsync() && grid.HasPendingChanges,"cancellable data event retains drafts");
        grid.DataChanging=EventCallback.Factory.Create<TreeGridDataChangeEventArgs<Row>>(new object(),e=>{ e.Changes.First(c=>c.Data?.Id==2).Data!.Name=""; });
        check(!await grid.SaveChangesAsync(),"host-amended drafts revalidate before source commit");
        grid.DataChanging=default;
        // Repair the first draft and then commit both records.
        await grid.BeginEditAsync(2,"Name"); ((Row)grid.CurrentEditContext!.Model).Name="First edit";
        check(await grid.SaveChangesAsync() && changes.Single().Changes.Count==2 && !grid.HasPendingChanges,"valid batch emits one complete two-row transaction");
        check(grid.GetCurrentRecords().Single(r=>r.Id==2).Name=="First edit" && rows[1].Name=="Task", "unbound TreeGrid owns replacement records without mutating caller objects");
        await grid.BeginEditAsync(4,"Name"); ((Row)grid.CurrentEditContext!.Model).Name="Discard"; await grid.CancelChangesAsync();
        check(grid.GetCurrentRecords().Single(r=>r.Id==4).Name=="Task", "cancel discards draft changes");
        await grid.DeleteRecordsAsync([2]);
        check(!grid.GetVisibleRecords().Any(r=>r.Id is 2 or 3) && grid.GetCurrentRecords().Any(r=>r.Id==3),"batch delete stages the complete subtree");
        await grid.CancelChangesAsync();
        check(grid.GetVisibleRecords().Any(r=>r.Id==3),"cancel restores deleted descendants");
        await grid.AddRecordAsync(new(){Id=10,Name="New child"},4); await grid.SaveChangesAsync();
        check(grid.GetCurrentRecords().Single(r=>r.Id==10).ParentId==4,"add child commits mapped parent with unique ID");
        check(!await grid.MoveRowAsync(1,3) && await grid.MoveRowAsync(2,4),"TreeGrid move rejects cycles and accepts valid destination");
        check(grid.GetCurrentRecords().Single(r=>r.Id==2).ParentId==4 && grid.GetCurrentRecords().Single(r=>r.Id==3).ParentId==2,"TreeGrid move preserves descendants");
        check(await grid.OutdentAsync(2) && grid.GetCurrentRecords().Single(r=>r.Id==2).ParentId==null,"outdent promotes to parent sibling");
        await grid.FreezeColumnAsync("Name",FrozenColumnPosition.Left); await grid.FreezeColumnAsync("Amount",FrozenColumnPosition.Right);
        var saved=JsonSerializer.Deserialize<TreeGridViewState>(JsonSerializer.Serialize(grid.CaptureState()))!;
        await grid.CheckAllAsync(false); await grid.FreezeColumnAsync("Name",null); await grid.RestoreStateAsync(saved);
        check(grid.CaptureState().FrozenPositions["Name"]==FrozenColumnPosition.Left && grid.GetCheckedRecords().Count>0,"view state roundtrips checked IDs and left/right frozen columns");
        var loads=0; grid.TreatAsParent=r=>r.Id==9;
        grid.LoadChildren=(_,_)=> {loads++; return Task.FromResult<IReadOnlyList<Row>>([new(){Id=91,ParentId=9}]);};
        await grid.SetSortsAsync([]); await grid.SetRowCheckedAsync(9,true);
        await grid.SetExpandedAsync(grid.GetCurrentRecords().Single(r=>r.Id==9),true);
        check(grid.GetCheckedRecords().Any(r=>r.Id==91),"lazy children inherit checked parent in TreeGrid");
        await grid.MoveRowAsync(91,4); await grid.SetExpandedAsync(grid.GetCurrentRecords().Single(r=>r.Id==9),true);
        check(loads==1 && grid.GetCurrentRecords().Count(r=>r.Id==91)==1,"moving loaded children does not refetch or duplicate provider rows");
        var gate = new TaskCompletionSource();
        grid.DataChanging = EventCallback.Factory.Create<TreeGridDataChangeEventArgs<Row>>(new object(), async _ => await gate.Task);
        await grid.BeginEditAsync(4, "Name"); ((Row)grid.CurrentEditContext!.Model).Name = "Stale save";
        var pendingSave = grid.SaveChangesAsync();
        await grid.SetParametersAsync(P(("DataSource", new[] { new Row { Id=100, Name="Replacement" } })));
        gate.SetResult();
        check(!await pendingSave && grid.GetCurrentRecords().Single().Id==100 && !grid.HasPendingChanges,"source replacement rejects a pending commit and discards stale drafts");
        grid.DataChanging=default;
        var editGate = new TaskCompletionSource();
        grid.EditStarting=EventCallback.Factory.Create<TreeGridEditEventArgs<Row>>(new object(),async _=>await editGate.Task);
        var pendingEdit=grid.BeginEditAsync(100,"Name");
        await grid.SetParametersAsync(P(("DataSource",new[]{new Row {Id=101,Name="Current"}})));
        editGate.SetResult();
        check(!await pendingEdit && !grid.HasPendingChanges,"source replacement rejects pending edit initialization");
        var addGate = new TaskCompletionSource();
        grid.EditStarting=EventCallback.Factory.Create<TreeGridEditEventArgs<Row>>(new object(),async _=>await addGate.Task);
        var pendingAdd=grid.AddRecordAsync(new(){Id=102,Name="Stale addition"});
        await grid.SetParametersAsync(P(("DataSource",new[]{new Row {Id=103,Name="Latest"}})));
        addGate.SetResult();
        check(!await pendingAdd && grid.GetCurrentRecords().Single().Id==103,"source replacement rejects pending addition without resurrecting stale rows");

    }
}
