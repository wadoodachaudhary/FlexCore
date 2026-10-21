using Fx.ControlKit;
using Fx.ControlKit.Spreadsheet;
using Fx.ControlKit.Scheduling;
using Fx.ControlKit.Maps;
using System.Xml.Linq;
using System.Globalization;

internal static class RemainingControlChecks
{
 public static void Run(Action<bool,string> check)
 {
  void Reject(Action action,string label){try{action();}catch(Exception){check(true,label);return;}throw new Exception(label);}
  var mask=new MaskPattern("(000) 000-0000");
  var phone=mask.Apply("2125550100");check(phone.Text=="(212) 555-0100"&&phone.Complete,"mask inserts literals and validates required positions");
  check(mask.Apply(phone.Text).RawValue=="2125550100","mask accepts already formatted input");
  check(!mask.Apply("212").Complete&&mask.Apply("abc").Error is not null,"mask rejects incomplete and invalid input");
  check(new MaskPattern(">LL-000").Apply("ab123").Text=="AB-123","mask supports case conversion");
  check(new MaskPattern("00099").Apply("123").Complete,"optional mask slots do not block completion");
  Reject(()=>new MaskPattern("000\\"),"incomplete mask escape rejected");
  foreach(var hex in new[]{"#FF0000","#000000","#FFFFFF","#123ABC","#FF000080"})check(ColorValue.TryParse(hex,out var color)&&color.ToHex()==hex,"HSV round trip "+hex);
  check(!ColorValue.TryParse("garbage",out _),"invalid colors rejected");
  check(UploadValidation.Validate("file.TXT",20,100,[".txt"]) is null,"upload extension matching ignores case");
  check(UploadValidation.Validate("file.exe",20,100,["txt"]) is not null,"upload rejects disallowed extension");
  check(UploadValidation.Validate("file.txt",101,100,[]) is not null,"upload enforces size bound");
  var signature=new SignatureValue([new([new(1,2),new(10,20)],"#222222",2)]);
  var svg=XDocument.Parse(signature.ToSvg());check(svg.Descendants().Any(e=>e.Name.LocalName=="polyline"&&e.Attribute("points")?.Value=="1,2 10,20"),"signature saves vector coordinates");
  Reject(()=>new SignatureValue([new([new(double.NaN,1)])]).ToSvg(),"signature rejects nonfinite geometry");
  foreach(var point in new[]{new GeoPoint(40.71,-74),new GeoPoint(-33.8,151.2),new GeoPoint(0,0)}){var restored=MapProjection.Unproject(MapProjection.Project(point,5),5);check(Math.Abs(restored.Latitude-point.Latitude)<.000001&&Math.Abs(restored.Longitude-point.Longitude)<.000001,"Mercator projection round trip");}
  check(double.IsFinite(MapProjection.Project(new(90,0),2).Y),"Mercator clamps polar latitude");
  var shapes=MapProjection.ReadGeoJson("""{"type":"Feature","properties":{"name":"Area"},"geometry":{"type":"Polygon","coordinates":[[[0,0],[10,0],[10,10],[0,0]]]}}""");check(shapes.Count==1&&shapes[0].Title=="Area"&&shapes[0].Paths[0][1]==new GeoPoint(0,10),"GeoJSON coordinate order and properties");
  using(var document=new SpreadsheetDocument()){
   document.SetRange(new(),"Name\tQty\tPrice\tTotal\nWidget\t2\t12.5\t=B2*C2\nService\t3\t20\t=B3*C3\nTotal\t\t\t=SUM(D2:D3)");
   check(document.Sheet.Cell("D4").GetDouble()==85,"spreadsheet computes range formulas");document.SetCell(2,2,"5");check(document.Sheet.Cell("D4").GetDouble()==122.5,"dependent formulas recalculate after edit");document.Undo();check(document.Sheet.Cell("B2").GetDouble()==2,"spreadsheet undo restores value");document.Redo();check(document.Sheet.Cell("B2").GetDouble()==5,"spreadsheet redo reapplies value");
   document.FillDown(new(2,4,3,4));check(document.Sheet.Cell("D3").FormulaA1=="B3*C3","fill-down adjusts relative formula references");
   document.Format(new(1,1,1,4),"bold","true");document.AddSheet("Second");document.SetCell(1,1,"Second sheet data");using var copy=new SpreadsheetDocument(document.Save());check(copy.Workbook.Worksheets.Count==2&&copy.Workbook.Worksheet("Sheet1").Cell("A1").Style.Font.Bold,"XLSX round trip preserves sheets and styling");
   document.Activate("Sheet1");document.Sheet.Protect("test");Reject(()=>document.SetCell(2,2,"99"),"protected spreadsheet cells reject writes");check(document.Sheet.Cell("B2").GetDouble()==5,"failed spreadsheet operation rolls back atomically");
  }
  var range=new SchedulerRange(new(2026,10,31),new(2026,11,4),"America/New_York");
  var daily=new SchedulerEvent{Id="daily",Title="Standup",Start=new(2026,10,31,9,0,0),End=new(2026,10,31,10,0,0),TimeZoneId="America/New_York",RecurrenceRule="FREQ=DAILY;COUNT=4"};
  var occurrences=SchedulerEngine.Expand([daily],range);check(occurrences.Count==4&&occurrences.All(o=>o.Start.Hour==9),"scheduler recurrence preserves local time across DST");
  daily.ExcludedStarts.Add(new(2026,11,1,9,0,0));occurrences=SchedulerEngine.Expand([daily],range);check(occurrences.Count==3&&occurrences.All(o=>o.Start.Day!=1),"scheduler recurrence exception removes one occurrence");
  var month=new SchedulerEvent{Title="Month end",Start=new(2026,9,30,9,0,0),End=new(2026,9,30,10,0,0),RecurrenceRule="FREQ=MONTHLY;BYMONTHDAY=-1;COUNT=3"};var monthly=SchedulerEngine.Expand([month],new(new(2026,9,1),new(2026,12,1),"UTC"));check(monthly.Select(o=>o.Start.Day).SequenceEqual(new[]{30,31,30}),"scheduler supports last-day monthly recurrence");
  var allDay=new SchedulerEvent{Title="All day",Start=new(2026,9,7),End=new(2026,9,8),AllDay=true,TimeZoneId="America/New_York"};var allDayResult=SchedulerEngine.Expand([allDay],new(new(2026,9,7),new(2026,9,8),"America/New_York"));check(allDayResult.Count==1&&allDayResult[0].Start==new DateTime(2026,9,7)&&allDayResult[0].End==new DateTime(2026,9,8),"all-day scheduler dates do not shift across time zones");
  var bad=daily.Clone();bad.End=bad.Start;Reject(()=>SchedulerEngine.Expand([bad],range),"scheduler rejects reversed or empty duration");
  var gap=daily.Clone();gap.Start=new(2026,3,8,2,30,0);gap.End=gap.Start.AddHours(1);Reject(()=>SchedulerEngine.Expand([gap],new(new(2026,3,8),new(2026,3,9),"America/New_York")),"scheduler rejects DST gap times");
  Reject(()=>SchedulerEngine.Expand([daily,daily.Clone()],range),"scheduler rejects duplicate appointment IDs");
 }
}
