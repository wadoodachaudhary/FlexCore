using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Fx.ControlKit;
using Fx.ControlKit.AI;
using Fx.ControlKit.Docking;
using Fx.ControlKit.Gantt;
using Fx.ControlKit.Charts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;

public static class CounterpartChecks
{
    private static ParameterView P(params (string Name,object? Value)[] values)=>ParameterView.FromDictionary(values.ToDictionary(x=>x.Name,x=>x.Value));
    private static async Task Dispatch(IComponent component,string name,params object?[] args)
    {
        await EventCallback.Factory.Create(component,async()=>
        {
            var method=component.GetType().GetMethod(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)!;
            if(method.Invoke(component,args) is Task task)await task;
        }).InvokeAsync();
    }
    public static async Task Run(HtmlRenderer renderer,List<IComponent> components,Action<bool,string> check)
    {
        var model=new FormRecord();var valid=0;var invalid=0;
        var html=await renderer.RenderComponentAsync<FormControl>(P(("Model",model),("OnValidSubmit",EventCallback.Factory.Create<EditContext>(new object(),_=>valid++)),("OnInvalidSubmit",EventCallback.Factory.Create<EditContext>(new object(),_=>invalid++))));
        var form=components.OfType<FormControl>().Last();
        var fields=components.OfType<FormItemControl>().Where(f=>ReferenceEquals(f.Context,form.CurrentEditContext)).ToArray();
        check(fields.Length==5,"form automatically generates supported annotated fields");
        var qty=fields.Single(f=>f.Field=="Quantity");
        await Dispatch(qty,"SetTextAsync","-");await form.SubmitAsync();
        check(model.Quantity==1&&valid==0&&invalid==1&&html.ToHtmlString().Contains("value=\"-\""),"invalid numeric draft survives and blocks form submit");
        await Dispatch(qty,"SetTextAsync","12");
        await Dispatch(fields.Single(f=>f.Field=="Name"),"SetTextAsync","abc");await form.SubmitAsync();
        check(model.Quantity==12&&valid==1,"corrected draft clears parsing errors and submits validated model");
        await Dispatch(qty,"SetTextAsync","0");await form.SubmitAsync();
        check(invalid==2&&html.ToHtmlString().Contains("between 1 and 20"),"DataAnnotations reject out-of-range values");
        await Dispatch(qty,"SetTextAsync","7");
        var date=components.OfType<DatePickerControl>().Last();
        await Dispatch(date,"OnInputTextChange",new ChangeEventArgs{Value="bad date"});await form.SubmitAsync();
        check(invalid==3&&html.ToHtmlString().Contains("bad date"),"invalid date draft blocks submit without resetting text");
        await Dispatch(date,"OnInputTextChange",new ChangeEventArgs{Value="09/07/2026"});await Dispatch(date,"OnInputBlur");await form.SubmitAsync();
        check(valid==2&&model.Date==new DateTime(2026,9,7),"corrected date commits through form binding");
        var replacement=new FormRecord{Name="new",Quantity=4};await form.SetParametersAsync(P(("Model",replacement)));
        check(ReferenceEquals(form.CurrentEditContext!.Model,replacement)&&html.ToHtmlString().Contains("value=\"new\""),"replacement form model rebuilds EditContext and editors");

        var calendarHtml=await renderer.RenderComponentAsync<CalendarControl>(P(("Value",DateTime.MinValue)));
        var calendar=components.OfType<CalendarControl>().Last();
        check(calendarHtml.ToHtmlString().Contains("January 0001"),"standalone calendar renders DateTime minimum safely");
        await calendar.SetParametersAsync(P(("Value",DateTime.MaxValue.Date)));await calendar.NavigateAsync(1);
        check(calendarHtml.ToHtmlString().Contains("December 9999"),"calendar navigation stays inside DateTime maximum");
        await calendar.SetParametersAsync(P(("SelectionMode",CalendarSelectionMode.Multiple),("Value",new DateTime(2026,9,1))));
        await calendar.SelectAsync(new(2026,9,2));await calendar.SelectAsync(new(2026,9,3));await calendar.SelectAsync(new(2026,9,2));
        check(calendar.Values.SequenceEqual(new[]{new DateTime(2026,9,3)}),"calendar multiple selection toggles individual days");
        await calendar.SetParametersAsync(P(("SelectionMode",CalendarSelectionMode.Range),("DisabledDate",(Func<DateTime,bool>)(d=>d.Day==5))));
        await calendar.SelectAsync(new(2026,9,3));await calendar.SelectAsync(new(2026,9,7));
        check(calendar.Range.End==null,"range selection cannot cross a disabled date");
        await calendar.SelectAsync(new(2026,9,1));check(calendar.Range.Start==new DateTime(2026,9,1)&&calendar.Range.End==new DateTime(2026,9,3),"reverse calendar range normalizes endpoints");

        await renderer.RenderComponentAsync<TimePickerControl>(P(("Value",new TimeOnly(9,0)),("Min",new TimeOnly(8,0)),("Max",new TimeOnly(17,0))));
        var time=components.OfType<TimePickerControl>().Last();await Dispatch(time,"CommitAsync","25:90");
        check(time.ErrorMessage!=null&&time.Value==new TimeOnly(9,0),"time picker keeps previous value for malformed input");
        await Dispatch(time,"CommitAsync","7:00 AM");check(time.ErrorMessage!=null,"time picker rejects out-of-range times");
        await Dispatch(time,"CommitAsync","1:30 PM");check(time.Value==new TimeOnly(13,30)&&time.ErrorMessage==null,"time picker parses clock time and clears error");

        var selected=0;await renderer.RenderComponentAsync<ToggleButtonControl>(P(("SelectedChanged",EventCallback.Factory.Create<bool>(new object(),v=>selected+=v?1:-1))));
        var toggle=components.OfType<ToggleButtonControl>().Last();await Dispatch(toggle,"ToggleAsync",new MouseEventArgs());await Dispatch(toggle,"ToggleAsync",new MouseEventArgs());
        check(selected==0&&!toggle.Selected,"toggle button publishes both selected states");
        await toggle.SetParametersAsync(P(("Enabled",false)));await Dispatch(toggle,"ToggleAsync",new MouseEventArgs());check(!toggle.Selected,"disabled toggle does not change state");

        var gaugeHtml=await renderer.RenderComponentAsync<LinearGaugeControl>(P(("Min",10d),("Max",30d),("Value",20d),("Ranges",new[]{new GaugeRange(20,30,"#ff0000")}), ("Pointers",new[]{new GaugePointer(25,"#00ff00")})));
        check(gaugeHtml.ToHtmlString().Contains("width=\"130\"")&&gaugeHtml.ToHtmlString().Contains("#ff0000")&&gaugeHtml.ToHtmlString().Contains("#00ff00"),"gauge renders configured domain, range and pointer");
        check(gaugeHtml.ToHtmlString().Contains("aria-valuenow=\"20\""),"dedicated gauge exposes accessible value");
        var stockData=Enumerable.Range(1,10).Select(i=>ChartDataPoint.Financial(i.ToString(),i,i+2,i-1,i+1,new DateTime(2026,9,i))).ToArray();
        var ranges=new List<StockChartRange>();await renderer.RenderComponentAsync<StockChartControl>(P(("DataSource",stockData),("RangeChanged",EventCallback.Factory.Create<StockChartRange>(new object(),r=>ranges.Add(r)))));
        var stock=components.OfType<StockChartControl>().Last();await stock.SetRangeAsync(new(2026,9,3),new(2026,9,5));await stock.PanAsync(1);
        check(ranges[^1]==new StockChartRange(new(2026,9,4),new(2026,9,6)),"financial navigator pans without changing range length");
        await stock.ZoomAsync(.5);check(ranges[^1].End-ranges[^1].Start==TimeSpan.FromDays(1),"financial zoom changes selected interval");

        var oldRead=new TaskCompletionSource<IEnumerable<string>>();
        var autoHtml=await renderer.RenderComponentAsync<AutoCompleteControl<string>>(P(("DebounceDelay",0),("OnRead",(Func<string,CancellationToken,Task<IEnumerable<string>>>)(
            (query,_)=>query=="old"?oldRead.Task:Task.FromResult<IEnumerable<string>>(["fresh suggestion"])))));
        var auto=components.OfType<AutoCompleteControl<string>>().Last();var oldSearch=auto.SearchAsync("old");await auto.SearchAsync("fresh");oldRead.SetResult(["stale suggestion"]);await oldSearch;
        check(autoHtml.ToHtmlString().Contains("fresh suggestion")&&!autoHtml.ToHtmlString().Contains("stale suggestion"),"autocomplete ignores stale remote completions even when provider ignores cancellation");
        await auto.SetParametersAsync(P(("Enabled",false)));check(!autoHtml.ToHtmlString().Contains("role=\"option\""),"disabling autocomplete closes suggestions");
        await renderer.RenderComponentAsync<StepperControl>(P(("Steps",new[]{new WizardStepDescriptor{Title="One"},new WizardStepDescriptor{Title="Two"},new WizardStepDescriptor{Title="Three"}}),("InvalidSteps",new[]{0})));
        var stepper=components.OfType<StepperControl>().Last();await stepper.SelectAsync(1);check(stepper.Value==0,"linear stepper blocks forward navigation from invalid step");
        await stepper.SetParametersAsync(P(("InvalidSteps",Array.Empty<int>()),("CanNavigate",(Func<int,int,Task<bool>>)((_,_)=>Task.FromResult(false)))));await stepper.SelectAsync(1);check(stepper.Value==0,"stepper awaits host navigation veto");
        await stepper.SetParametersAsync(P(("CanNavigate",null)));await stepper.SelectAsync(1);check(stepper.Value==1,"valid stepper navigation changes current step");

        var dockPanels=new[]{new DockPanel("a","First",b=>b.AddContent(0,"First pane")),new DockPanel("b","Second",b=>b.AddContent(0,"Second pane")),new DockPanel("fixed","Fixed",b=>b.AddContent(0,"Fixed pane"),false,false)};
        var dockHtml=await renderer.RenderComponentAsync<DockManagerControl>(P(("Panels",dockPanels)));
        var dock=components.OfType<DockManagerControl>().Last();var initial=DockLayoutState.FromJson(dock.SaveLayout());
        await dock.DockAsync("b",initial.Root.Id,DockPosition.Right);
        var split=DockLayoutState.FromJson(dock.SaveLayout());check(split.Root.Children.Count==2&&split.Root.Children[1].Panels.SequenceEqual(new[]{"b"}),"docking to an edge creates a nested split");
        check(dockHtml.ToHtmlString().Contains("fx-splitter"),"dock split renders existing splitter control");
        await dock.FloatAsync("b");check(DockLayoutState.FromJson(dock.SaveLayout()).Floating.Single().Panel=="b","floating removes panel from its dock location");
        var saved=dock.SaveLayout();await dock.UnpinAsync("b");check(DockLayoutState.FromJson(dock.SaveLayout()).AutoHidden.SequenceEqual(new[]{"b"}),"unpinning moves pane to auto-hide strip");
        await dock.RestoreLayoutAsync(saved);check(DockLayoutState.FromJson(dock.SaveLayout()).Floating.Single().Panel=="b","saved layout restores floating pane");
        await dock.CloseAsync("fixed");await dock.FloatAsync("fixed");check(dock.SaveLayout()==saved,"dock panel close and float permissions are enforced");
        var invalidState=DockLayoutState.FromJson(saved);invalidState.Closed.Add("a");
        try{await dock.RestoreLayoutAsync(invalidState.ToJson());throw new Exception("duplicate dock panel accepted");}catch(ArgumentException){check(dock.SaveLayout()==saved,"invalid saved dock state is rejected without changing layout");}
        var cycle=new DockLayoutState();cycle.Root.Children=[cycle.Root,new()];
        try{cycle.Validate([]);throw new Exception("dock cycle accepted");}catch(ArgumentException){check(true,"cyclic dock state is rejected before serialization");}

        IReadOnlyList<GanttTask> schedule=[new(){Id="a",Title="First",Start=new(2026,9,1),End=new(2026,9,3)},new(){Id="b",Title="Second",Start=new(2026,9,3),End=new(2026,9,5),Predecessors="a"}];
        var published=new List<IReadOnlyList<GanttTask>>();await renderer.RenderComponentAsync<GanttControl>(P(("Tasks",schedule),("TasksChanged",EventCallback.Factory.Create<IReadOnlyList<GanttTask>>(new object(),t=>published.Add(t)))));
        var gantt=components.OfType<GanttControl>().Last();var early=schedule[1].Clone();early.Start=new(2026,9,2);
        check(!await gantt.ChangeAsync(early,GanttChangeAction.Update)&&published.Count==0&&gantt.ErrorMessage!.Contains("must start after"),"Gantt dependency constraints reject invalid moves without committing");
        var later=schedule[1].Clone();later.End=new(2026,9,6);check(await gantt.ChangeAsync(later,GanttChangeAction.Update)&&published[^1][1].End==later.End,"Gantt valid task change commits updated schedule");
        check(schedule[1].End==new DateTime(2026,9,5),"Gantt editing does not mutate caller-owned task drafts");
        check(!await gantt.ChangeAsync(schedule[0],GanttChangeAction.Delete),"Gantt refuses deletion while dependency references remain");
        var cyclic=schedule[0].Clone();cyclic.Predecessors="b";await gantt.SetParametersAsync(P(("EnforceDependencies",false)));
        check(!await gantt.ChangeAsync(cyclic,GanttChangeAction.Update)&&gantt.ErrorMessage!.Contains("cycle"),"Gantt rejects dependency cycles even when date enforcement is disabled");
        await gantt.SetParametersAsync(P(("TaskChanging",EventCallback.Factory.Create<GanttTaskChangingArgs>(new object(),e=>e.Cancel=true))));
        var count=published.Count;check(!await gantt.ChangeAsync(later,GanttChangeAction.Update)&&published.Count==count,"Gantt host veto prevents commit");

        var pending=new TaskCompletionSource<string>();
        var promptHtml=await renderer.RenderComponentAsync<AIPromptControl>(P(("Prompt","First"),("Generate",(Func<AIPromptRequest,CancellationToken,Task<string>>)((_,_)=>pending.Task))));
        var prompt=components.OfType<AIPromptControl>().Last();var request=prompt.GenerateAsync();await prompt.CancelAsync();pending.SetResult("late response");await request;
        check(prompt.History.Count==0&&!promptHtml.ToHtmlString().Contains("late response"),"cancelled AI requests cannot publish stale results");
        await prompt.SetParametersAsync(P(("Generate",(Func<AIPromptRequest,CancellationToken,Task<string>>)((r,_)=>Task.FromResult("Response to "+r.Prompt)))));await prompt.GenerateAsync();
        check(prompt.History.Count==1&&promptHtml.ToHtmlString().Contains("Response to First"),"AI provider result uses existing result card");
        await prompt.SetParametersAsync(P(("Generate",(Func<AIPromptRequest,CancellationToken,Task<string>>)((_,_)=>throw new InvalidOperationException("provider unavailable")))));await prompt.GenerateAsync();
        check(prompt.ErrorMessage=="provider unavailable"&&!prompt.IsBusy,"AI provider errors remain visible and release busy state");
    }
}
public sealed class FormRecord
{
    [Required,MinLength(2)]public string Name{get;set;}="ok";
    [Range(1,20)]public int Quantity{get;set;}=1;
    public DateTime Date{get;set;}=new(2026,9,1);
    public bool Active{get;set;}
    public TimeOnly Time{get;set;}=new(9,0);
}
