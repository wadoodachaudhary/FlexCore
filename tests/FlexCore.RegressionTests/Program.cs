using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Fx.ControlKit;
using Fx.ControlKit.Grid;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using ZXing;

CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
var activation = new Components();
using var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime, NoJs>()
    .AddSingleton<IComponentActivator>(activation).BuildServiceProvider();
await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
var checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
void Invalid(Action action, string name) { try { action(); } catch (System.FormatException) { Check(true, name); return; } throw new Exception(name); }
object? Member(object value, string name) => value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value)
    ?? value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value);
object? Call(object c, string name, params object?[] args) {
    var method = c.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .First(m => m.Name == name && m.GetParameters().Length >= args.Length && m.GetParameters().Skip(args.Length).All(p => p.HasDefaultValue)
            && m.GetParameters().Take(args.Length).Select((p,i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(x => x));
    return method.Invoke(c, args.Concat(method.GetParameters().Skip(args.Length).Select(p => p.DefaultValue)).ToArray());
}
Task Dispatch(IComponent c, string name, params object?[] args) => EventCallback.Factory.Create(c, async () => { if (Call(c, name, args) is Task task) await task; }).InvokeAsync();
ParameterView Parameters(params (string Key, object? Value)[] pairs) => ParameterView.FromDictionary(pairs.ToDictionary(p => p.Key, p => p.Value));
XElement Svg(string html) => XElement.Parse(Regex.Replace(Regex.Match(html, "<svg.*?</svg>", RegexOptions.Singleline).Value, @" b-[a-z0-9]+(?=[ >])", ""));
// Rasterize the actual rendered SVG geometry, then decode it. This catches malformed paths,
// quiet-zone errors and disconnected parameters that a writer-only round trip would miss.
string? Decode(string html) {
    var svg = Svg(html); var box = svg.Attribute("viewBox")!.Value.Split(' ').Select(int.Parse).ToArray();
    const int scale = 4; int width = box[2] * scale, height = box[3] * scale;
    var pixels = Enumerable.Repeat((byte)255, width * height * 3).ToArray();
    void Rect(int x,int y,int w,int h) { for(int row=y*scale;row<(y+h)*scale;row++) for(int col=x*scale;col<(x+w)*scale;col++) {
        var offset = (row*width+col)*3; pixels[offset]=pixels[offset+1]=pixels[offset+2]=0;
    }}
    foreach (var rect in svg.Elements("rect").Where(r => r.Attribute("x") != null))
        Rect(int.Parse(rect.Attribute("x")!.Value),int.Parse(rect.Attribute("y")!.Value),int.Parse(rect.Attribute("width")!.Value),int.Parse(rect.Attribute("height")!.Value));
    foreach(var path in svg.Elements("path"))
        foreach(Match run in Regex.Matches(path.Attribute("d")!.Value,@"M(\d+),(\d+)h(\d+)v1h-\d+z"))
            Rect(int.Parse(run.Groups[1].Value),int.Parse(run.Groups[2].Value),int.Parse(run.Groups[3].Value),1);
    var reader = new BarcodeReaderGeneric { Options = new() { TryHarder = true, CharacterSet = "UTF-8" } };
    return reader.Decode(new RGBLuminanceSource(pixels,width,height,RGBLuminanceSource.BitmapFormat.RGB24))?.Text;
}
await renderer.Dispatcher.InvokeAsync(async () => {
    foreach(var example in new (BarcodeType Type,string Text,string Expected)[] {
        (BarcodeType.Code128,"Abc-012345","Abc-012345"),(BarcodeType.Code39,"ABC-123","ABC-123"),
        (BarcodeType.Code93,"CODE93","CODE93"),(BarcodeType.Ean8,"96385074","96385074"),
        (BarcodeType.Ean13,"5901234123457","5901234123457"),(BarcodeType.UpcA,"036000291452","036000291452"),
        (BarcodeType.UpcE,"04252614","04252614"),(BarcodeType.Itf,"12345678","12345678"),(BarcodeType.Codabar,"A12345B","12345") }) {
        var root = await renderer.RenderComponentAsync<BarcodeControl>(Parameters(("Value",example.Text),("Type",example.Type),("BarHeight",73)));
        Check(Decode(root.ToHtmlString()) == example.Expected, $"rendered {example.Type} decodes to payload");
        Check(Svg(root.ToHtmlString()).Elements("rect").Where(r=>r.Attribute("x")!=null).All(r=>r.Attribute("height")!.Value=="73"),"BarHeight controls SVG bars");
    }
    var invalid = await renderer.RenderComponentAsync<BarcodeControl>(Parameters(("Type",BarcodeType.Ean13),("Value","5901234123450")));
    Check(invalid.ToHtmlString().Contains("role=\"alert\"") && !invalid.ToHtmlString().Contains("<svg"),"bad EAN checksum reports error without symbol");
    var encoded = await renderer.RenderComponentAsync<BarcodeControl>(Parameters(("Value","<script>")));
    Check(!encoded.ToHtmlString().Contains("<script>") && Decode(encoded.ToHtmlString()) == "<script>","barcode text HTML is encoded");
    foreach(var level in Enum.GetValues<QrErrorCorrectionLevel>()) {
        const string text="https://example.test/日本語?q=café&x=42";
        var root=await renderer.RenderComponentAsync<QrCodeControl>(Parameters(("Value",text),("ErrorCorrection",level)));
        Check(Decode(root.ToHtmlString())==text,$"rendered UTF-8 QR decodes at {level} correction");
    }
    var tooLong=await renderer.RenderComponentAsync<QrCodeControl>(Parameters(("Value",new string('x',10000))));
    Check(tooLong.ToHtmlString().Contains("role=\"alert\"")&&!tooLong.ToHtmlString().Contains("<svg"),"oversized QR reports error without symbol");
    var empty=await renderer.RenderComponentAsync<QrCodeControl>(Parameters(("Value","")));
    Check(!empty.ToHtmlString().Contains("<svg"),"empty QR clears previous symbol");
    var qr=activation.All.OfType<QrCodeControl>().Last();await qr.SetParametersAsync(Parameters(("Value","replacement")));
    Check(Decode(empty.ToHtmlString())=="replacement","replacing QR value regenerates rendered symbol");

    var plainText = await renderer.RenderComponentAsync<TextBoxControl>();
    var plain = activation.All.OfType<TextBoxControl>().Last();
    Check(Member(plain,"KeyDownDispatch") is EventCallback<KeyboardEventArgs> { HasDelegate: false }, "plain text box omits empty keyboard handler");
    var keyCount=0;
    await plain.SetParametersAsync(Parameters(("OnKeyDown",EventCallback.Factory.Create<KeyboardEventArgs>(new object(),_=>keyCount++))));
    await ((EventCallback<KeyboardEventArgs>)Member(plain,"KeyDownDispatch")!).InvokeAsync(new(){Key="Enter"});
    Check(keyCount==1,"supplied text box keyboard handler still dispatches");

    var schema=new List<FilterProperty> {
        new(){Property="Name"},new(){Property="Number",Type=typeof(int)},new(){Property="Amount",Type=typeof(decimal?)},
        new(){Property="Date",Type=typeof(DateTime)},new(){Property="Active",Type=typeof(bool)},new(){Property="Status",Type=typeof(Status)},
        new(){Property="Child.Name"}
    };
    FilterCondition C(string field,GridFilterOperator op,string value="") => new(){Field=field,Operator=op,Value=value};
    FilterGroup G(params FilterCondition[] conditions)=>new(){Conditions=conditions.ToList()};
    var rows=new List<Row> {
        new(){Name="Alpha",Number=2,Amount=12.5m,Date=new(2026,1,1),Active=true,Status=Status.Open,Child=new(){Name="child"}},
        new(){Name="Beta",Number=10,Amount=null,Date=new(2026,10,1),Active=false,Status=Status.Closed}
    };
    Func<Row,bool> Predicate(FilterGroup group)=>FilterEvaluator.BuildPredicate<Row>(group,schema,CultureInfo.GetCultureInfo("en-US"));
    Check(rows.Where(Predicate(G(C("Number",GridFilterOperator.GreaterThan,"3")))).Single().Name=="Beta","numeric comparison uses values rather than text");
    Check(rows.Where(Predicate(G(C("Date",GridFilterOperator.GreaterThan,"2/1/2026")))).Single().Name=="Beta","date comparison is chronological");
    Check(rows.Where(Predicate(G(C("Active",GridFilterOperator.Equals,"True"),C("Status",GridFilterOperator.Equals,"Open")))).Single().Name=="Alpha","boolean and enum predicates");
    Check(rows.Where(Predicate(G(C("Amount",GridFilterOperator.IsNull)))).Single().Name=="Beta","nullable field IsNull");
    Check(rows.Where(Predicate(G(C("Child.Name",GridFilterOperator.IsNull)))).Single().Name=="Beta","null intermediate property is handled");
    var nested=G(C("Name",GridFilterOperator.Contains,"ALP"));nested.Groups.Add(new(){LogicalOperator=LogicalFilterOperator.Or,Conditions=[C("Number",GridFilterOperator.Equals,"2"),C("Active",GridFilterOperator.Equals,"False")]});
    Check(rows.Where(Predicate(nested)).Single().Name=="Alpha","nested AND/OR and case-insensitive matching");
    var snapshot=Predicate(nested);nested.Conditions[0].Value="Beta";
    Check(rows.Where(snapshot).Single().Name=="Alpha","compiled predicate is a snapshot of filter values");
    Check(!FilterEvaluator.BuildPredicate<Row>(G(C("Name",GridFilterOperator.Contains,"ALP")),schema,caseSensitive:true)(rows[0]),"case sensitivity is configurable");
    Check(FilterEvaluator.BuildPredicate<Row>(G(C("Amount",GridFilterOperator.Equals,"12,5")),schema,CultureInfo.GetCultureInfo("de-DE"))(rows[0]),"numeric values use requested culture");
    Invalid(()=>Predicate(G(C("Number",GridFilterOperator.Equals,"bad"))),"invalid number fails before enumeration");
    Invalid(()=>Predicate(G(C("Number",GridFilterOperator.Equals,"2147483648"))),"overflow is not silently truncated");
    Invalid(()=>Predicate(G(C("Number",GridFilterOperator.Contains,"2"))),"invalid typed operator is rejected");
    Invalid(()=>Predicate(G(C("Unknown",GridFilterOperator.Equals,"value"))),"unknown field is rejected");
    var cycle=new FilterGroup();cycle.Groups.Add(cycle);Invalid(()=>Predicate(cycle),"cyclic filter state is rejected");
    Check(rows.All(Predicate(new(){LogicalOperator=LogicalFilterOperator.Or,Groups=[new()]})),"empty groups do not exclude every row");
    var dictSchema=new[]{new FilterProperty{Property="Name"}};
    Check(FilterEvaluator.BuildPredicate<Dictionary<string,object?>>(G(C("Name",GridFilterOperator.IsNull)),dictSchema)(new(){{"Name",null}}),"dictionary null values work");
    var filterHtml=await renderer.RenderComponentAsync<DataFilterControl>(Parameters(("Properties",schema)));
    var filter=activation.All.OfType<DataFilterControl>().Last();
    Check(!filterHtml.ToHtmlString().Contains("<select"),"filter builder uses FlexCore selectors");
    Check(filter.RootGroup.Conditions[0].Operator==GridFilterOperator.Contains,"text field starts with Contains");
    var condition=filter.RootGroup.Conditions[0];await Dispatch(filter,"OnConditionFieldChanged",condition,"Number");
    Check(condition.Operator==GridFilterOperator.Equals&&condition.Value=="","field change resets incompatible operator and value");
    await Dispatch(filter,"OnConditionValueChanged",condition,"-12");Check(condition.Value=="-12","numeric input draft survives changes");
    await Dispatch(filter,"OnConditionValueChanged",condition,"bad");
    Check(filterHtml.ToHtmlString().Contains("Enter a valid Int32")&&condition.Value=="bad","invalid typed value is visible and retained");
    Check(filter.BuildExpression(G(C("Name",GridFilterOperator.Contains,"abc"))) == "Name contains \"abc\"", "display expressions preserve established operator spelling");
    var quotedExpression=filter.BuildExpression(G(C("Name",GridFilterOperator.Equals,"a\"b\\c")));
    Check(quotedExpression.Contains("\\u0022")&&quotedExpression.Contains("\\\\"),"expression display escapes quotes and backslashes");
    var replacement=G(C("Active",GridFilterOperator.Equals,"True"));await filter.SetParametersAsync(Parameters(("Value",replacement)));
    Check(ReferenceEquals(filter.RootGroup,replacement)&&activation.All.OfType<DropDownListControl<string,string>>().Any(c=>c.AriaLabel=="Filter value"&&c.Value=="True"),"replacement state reaches boolean editor");
    await filter.SetParametersAsync(Parameters(("Value",G(C("Date",GridFilterOperator.Equals,"1/1/2026")))));
    Check(activation.All.OfType<DatePickerControl>().Any(),"date fields render existing DatePickerControl");
    await filter.SetParametersAsync(Parameters(("Value",null)));
    Check(filter.RootGroup.Conditions.Single().Value=="","null replacement resets filter state");
    var gate=new TaskCompletionSource();var completed=false;
    filter.ValueChanged=EventCallback.Factory.Create<FilterGroup>(new object(),async _=>{await gate.Task;completed=true;});
    var pending=Dispatch(filter,"OnConditionValueChanged",filter.RootGroup.Conditions[0],"awaited");
    Check(!pending.IsCompleted,"ValueChanged is awaited");gate.SetResult();await pending;Check(completed,"async callback completes before input event finishes");

    var pivotHtml=await renderer.RenderComponentAsync<PivotControl<Row>>(Parameters(("DataSource",rows),("RowFields",new[]{"Number"}),("ValueField","Amount"),("ShowGrandTotals",true)));
    var pivot=activation.All.OfType<PivotControl<Row>>().Last();
    string[][] PivotRows()=>((IEnumerable)Member(pivot,"_displayRows")!).Cast<object>().Select(r=>((IEnumerable)Member(r,"Cells")!).Cast<object>().Select(c=>(string)Member(c,"DisplayValue")!).ToArray()).ToArray();
    Check(PivotRows()[0][0]=="2"&&PivotRows()[1][0]=="10","pivot defaults to numeric dimension order");
    await pivot.SortByAsync("Number",SortDirection.Descending);Check(PivotRows()[0][0]=="10","pivot descending changes displayed row order");
    Check(PivotRows()[^1][0]=="Grand Total","grand total stays last after sort");
    await pivot.SortByAsync("Number",null);Check(PivotRows()[0][0]=="2","removing sort restores typed default order");
    await pivot.SetParametersAsync(Parameters(("RowFields",new[]{"Date"})));await pivot.SortByAsync("Date",SortDirection.Descending);
    Check(PivotRows()[0][0].Contains("10/1/2026"),"date dimensions sort chronologically");
    await pivot.SetParametersAsync(Parameters(("RowFields",new[]{"Name"}),("ColumnFields",new[]{"Number"}),("ShowGrandTotals",false)));
    var columns=((IEnumerable)Member(pivot,"_valueColumns")!).Cast<object>().ToArray();
    Check(((string)Member(columns[0],"Header")!).Contains("2"),"numeric column keys default to numeric order");
    await pivot.SortByAsync("Number",SortDirection.Descending);
    columns=((IEnumerable)Member(pivot,"_valueColumns")!).Cast<object>().ToArray();
    Check(((string)Member(columns[0],"Header")!).Contains("10"),"column sorting changes value-column order");
    var export=Call(pivot,"CreateExportTable")!;var exportedColumns=((IEnumerable)Member(export,"Columns")!).Cast<object>().ToArray();
    Check(((string)Member(exportedColumns[1],"Header")!).Contains("10"),"export column order follows pivot sort");
    await pivot.SetParametersAsync(Parameters(("ColumnFields",Array.Empty<string>()),("SortDescriptors",new[]{new PivotSortDescriptor("Name",SortDirection.Descending)})));
    Check(PivotRows()[0][0]=="Beta","external pivot sort state applies");
    await pivot.SetParametersAsync(Parameters(("SortDescriptors",null)));
    Check(PivotRows()[0][0]=="Alpha","clearing external sort state resets order");
    var valueColumn=((IEnumerable)Member(pivot,"_valueColumns")!).Cast<object>().First();
    await pivot.SortByAsync((string)Member(valueColumn,"Field")!,SortDirection.Descending);
    Check(PivotRows()[0][0]=="Alpha","aggregate sort descending orders by actual values");
    await pivot.SortByAsync((string)Member(valueColumn,"Field")!,SortDirection.Ascending);
    Check(PivotRows()[0][0]=="Beta","aggregate sort ascending reverses actual row order");

    var matrixData = (from region in new[]{"North","South"} from number in new[]{2,10}
                      from year in new[]{2025,2026} from month in new[]{2,10}
                      select new PivotRow{Region=region,Number=number,Year=year,Month=month,Amount=number*100+year+month}).ToArray();
    var matrixHtml=await renderer.RenderComponentAsync<PivotControl<PivotRow>>(Parameters(("DataSource",matrixData),("RowFields",new[]{"Region","Number"}),
        ("ColumnFields",new[]{"Year","Month"}),("ValueField","Amount"),("ShowGrandTotals",true),("ShowSubTotals",true)));
    var matrix=activation.All.OfType<PivotControl<PivotRow>>().Last();
    await matrix.SortByAsync("Year",SortDirection.Descending);
    await matrix.SortByAsync("Month",SortDirection.Ascending,true);
    await matrix.SortByAsync("Number",SortDirection.Descending,true);
    var levels=((IEnumerable)Member(matrix,"_headerLevels")!).Cast<IEnumerable>().ToArray();
    var topHeaders=levels[0].Cast<object>().Select(c=>(string)Member(c,"Text")!).ToArray();
    var leafHeaders=levels[1].Cast<object>().Select(c=>(string)Member(c,"Text")!).ToArray();
    Check(topHeaders[2]=="2026"&&topHeaders[3]=="2025"&&leafHeaders.SequenceEqual(new[]{"2","10","2","10"}),"multi-axis sorts preserve grouped header order");
    var displayRows=((IEnumerable)Member(matrix,"_displayRows")!).Cast<object>().ToArray();
    var firstCells=((IEnumerable)Member(displayRows[0],"Cells")!).Cast<object>().ToArray();
    Check((string)Member(firstCells[0],"DisplayValue")! == "North"&&(string)Member(firstCells[1],"DisplayValue")! == "10", "nested row sort keeps parent groups contiguous");
    Check((string)Member(firstCells[2],"DisplayValue")! == "3028"&&(string)Member(firstCells[3],"DisplayValue")! == "3036", "sorted pivot headers align with aggregate cells");
    Check(displayRows.Count(r=>((string)Member(r,"CssClass")!).Contains("subtotal"))==2,"subtotals remain present after multi-axis sort");
    Check(((string)Member(displayRows[^1],"CssClass")!).Contains("grand"),"multi-axis grand total stays pinned");
    await matrix.SortByAsync("Year",SortDirection.Ascending,true);
    var levelsState=((IEnumerable)Member(matrix,"_sortDescriptors")!).Cast<PivotSortDescriptor>().ToArray();
    Check(levelsState.Select(s=>s.Field).SequenceEqual(new[]{"Year","Month","Number"}),"updating sort direction preserves descriptor priority");
    await matrix.SortByAsync("Month",null);
    Check(((IEnumerable)Member(matrix,"_sortDescriptors")!).Cast<PivotSortDescriptor>().Select(s=>s.Field).SequenceEqual(new[]{"Year","Number"}),"removing a sort preserves other descriptors");
    var sortCount=0;matrix.SortDescriptorsChanged=EventCallback.Factory.Create<IReadOnlyList<PivotSortDescriptor>>(new object(),_=>sortCount++);
    await matrix.ClearSortingAsync();Check(sortCount==1,"sort clear emits one state callback");

    var gridRows=new List<Row>{new(){Name="First",Number=1},new(){Name="Second",Number=2},new(){Name="Third",Number=3}};
    var edits=new EditSettings{AllowDeleting=true,AllowEditing=true,Mode=EditMode.Batch,ShowConfirmDialog=true};
    var deleting=0;var deleted=0;
    var gridHtml=await renderer.RenderComponentAsync<GridControl<Row>>(Parameters(("DataSource",gridRows),("AutoGenerateColumns",true),("EditSettingsRef",edits)));
    var grid=activation.All.OfType<GridControl<Row>>().Last();
    grid.EventsRef=new(){RowDeleting=EventCallback.Factory.Create<RowEditEventArgs<Row>>(new object(),args=>{deleting++;if(args.Data!.Name=="Third")args.Cancel=true;}),
        RowDeleted=EventCallback.Factory.Create<RowEditEventArgs<Row>>(new object(),args=>deleted++)};
    await Dispatch(grid,"DeleteRow",gridRows[0],0);
    Check(gridRows.Count==3&&deleting==0&&gridHtml.ToHtmlString().Contains("Delete this record?"),"delete stages confirmation before callbacks or mutation");
    Check(gridHtml.ToHtmlString().Contains("role=\"dialog\"")&&gridHtml.ToHtmlString().Contains("aria-modal=\"true\"")&&gridHtml.ToHtmlString().Contains("aria-labelledby="),"confirmation has labelled modal dialog semantics");
    await Dispatch(grid,"CancelRowDeletion");Check(gridRows.Count==3&&deleted==0&&!gridHtml.ToHtmlString().Contains("Delete this record?"),"Cancel preserves rows and closes confirmation");
    await Dispatch(grid,"DeleteRow",gridRows[0],0);await Dispatch(grid,"ConfirmRowDeletionAsync");
    Check(gridRows.Count==2&&deleted==1&&deleting==1,"confirm deletes once and raises callbacks once");
    await Dispatch(grid,"ConfirmRowDeletionAsync");Check(deleted==1,"repeated confirm cannot duplicate deletion");
    var selected=Member(grid,"_selectedItems")!;Call(selected,"Add",gridRows[0]);Call(selected,"Add",gridRows[1]);
    await Dispatch(grid,"OnToolbarClick","delete");Check(gridHtml.ToHtmlString().Contains("Delete these 2 records?"),"toolbar selection gets one batch confirmation");
    await Dispatch(grid,"ConfirmRowDeletionAsync");Check(gridRows.Count==1&&gridRows[0].Name=="Third"&&deleted==2,"row veto preserves record within confirmed batch");
    Check(gridHtml.ToHtmlString().Contains("Third")&&!gridHtml.ToHtmlString().Contains(">Second<"),"deleted rows disappear from rendered view");
    edits.ShowConfirmDialog=false;grid.EventsRef=null;await Dispatch(grid,"DeleteRow",gridRows[0],0);
    Check(gridRows.Count==0&&!gridHtml.ToHtmlString().Contains("fx-grid-delete-dialog"),"confirmation opt-out deletes immediately");
    gridRows.Add(new(){Name="F2",Number=4});await grid.RefreshAsync();
    var col=grid.Columns.ToList().FindIndex(c=>c.Field=="Name");await Dispatch(grid,"HandleCellClick",gridRows[0],0,col,new MouseEventArgs());
    await Dispatch(grid,"HandleKeyDown",new KeyboardEventArgs{Key="F2"});
    Check(Member(grid,"_batchEditItem")!=null&& (string?)Member(grid,"_batchEditField")=="Name","F2 starts active editable cell");
    await Dispatch(grid,"HandleBatchEditKeyDown",gridRows[0],"Name",new KeyboardEventArgs{Key="Escape"});
    grid.Columns.Single(c=>c.Field=="Number").AllowEditing=false;
    await Dispatch(grid,"HandleCellClick",gridRows[0],0,grid.Columns.ToList().FindIndex(c=>c.Field=="Number"),new MouseEventArgs());
    await Dispatch(grid,"HandleKeyDown",new KeyboardEventArgs{Key="F2"});
    Check(Member(grid,"_batchEditItem")==null,"F2 respects read-only columns");
});
Console.WriteLine($"All {checks} regression checks passed.");
public class PivotRow { public string Region {get;set;}="";public int Number{get;set;}public int Year{get;set;}public int Month{get;set;}public decimal Amount{get;set;} }
public enum Status { Open, Closed }
public class Row { public string Name {get;set;}="";public int Number{get;set;}public decimal? Amount{get;set;}public DateTime Date{get;set;}public bool Active{get;set;}public Status Status{get;set;}public Row? Child{get;set;} }
public class Components:IComponentActivator { public List<IComponent> All=[]; public IComponent CreateInstance(Type type){var c=(IComponent)Activator.CreateInstance(type)!;All.Add(c);return c;} }
public class NoJs:IJSRuntime {public ValueTask<T> InvokeAsync<T>(string id,object?[]? args)=>ValueTask.FromResult(default(T)!);public ValueTask<T> InvokeAsync<T>(string id,CancellationToken token,object?[]? args)=>ValueTask.FromResult(default(T)!);}
