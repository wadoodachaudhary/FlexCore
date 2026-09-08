using ClosedXML.Excel;
using System.Globalization;
using System.IO.Compression;
namespace Fx.ControlKit.Spreadsheet;

public sealed record SpreadsheetSelection(int Row=1,int Column=1,int EndRow=1,int EndColumn=1)
{
 public int Top=>Math.Min(Row,EndRow); public int Left=>Math.Min(Column,EndColumn);
 public int Bottom=>Math.Max(Row,EndRow); public int Right=>Math.Max(Column,EndColumn);
 public string Address=>XLHelper.GetColumnLetterFromNumber(Column)+Row;
 public string RangeAddress=>$"{XLHelper.GetColumnLetterFromNumber(Left)}{Top}:{XLHelper.GetColumnLetterFromNumber(Right)}{Bottom}";
}
public sealed record SpreadsheetChange(string Sheet,string Address,string Action);
public enum SpreadsheetTextFilter { Contains, Equals, NotContains }
public sealed class SpreadsheetDocument : IDisposable
{
 public XLWorkbook Workbook {get;private set;}
 public string ActiveSheet {get;private set;}
 private MemoryStream? _saveStream;
 private readonly List<byte[]> _undo=[];private readonly List<byte[]> _redo=[];
 public int HistoryLimit {get;set;}=20;
 public bool CanUndo=>_undo.Count>0;public bool CanRedo=>_redo.Count>0;
 public IXLWorksheet Sheet=>Workbook.Worksheet(ActiveSheet);
 public SpreadsheetDocument(){Workbook=new();ActiveSheet=Workbook.AddWorksheet("Sheet1").Name;}
 public SpreadsheetDocument(byte[] bytes){Workbook=LoadWorkbook(bytes);ActiveSheet=Workbook.Worksheets.First().Name;}
 private static XLWorkbook LoadWorkbook(byte[] bytes)
 {
  if(bytes.Length>50*1024*1024)throw new ArgumentException("Workbook exceeds 50 MiB.");
  using(var zip=new ZipArchive(new MemoryStream(bytes))){if(zip.Entries.Count>20000||zip.Entries.Sum(e=>e.Length)>256L*1024*1024)throw new ArgumentException("Expanded workbook exceeds the supported size.");}
  var workbook=new XLWorkbook(new MemoryStream(bytes));if(!workbook.Worksheets.Any()){workbook.Dispose();throw new ArgumentException("Workbook has no worksheets.");}return workbook;
 }
 public void Activate(string name){if(!Workbook.Worksheets.Any(s=>s.Name==name))throw new ArgumentException("Unknown worksheet.");ActiveSheet=name;}
 public string EditText(int row,int column){var cell=Sheet.Cell(row,column);return cell.HasFormula?"="+cell.FormulaA1:cell.GetString();}
 public string DisplayText(int row,int column){try{return Sheet.Cell(row,column).GetFormattedString(CultureInfo.CurrentCulture);}catch(Exception e) when(e is not OutOfMemoryException and not StackOverflowException){return "#ERROR: "+e.Message;}}
 public void SetCell(int row,int column,string? text)=>Mutate(()=>Set(Sheet.Cell(row,column),text));
 public void SetRange(SpreadsheetSelection range,string tsv)=>Mutate(()=>{var rows=tsv.Replace("\r\n","\n").TrimEnd('\n').Split('\n');if(rows.Length>10000)throw new ArgumentException("Paste is limited to 10,000 rows.");for(var r=0;r<rows.Length;r++){var values=rows[r].Split('\t');for(var c=0;c<values.Length;c++)Set(Sheet.Cell(range.Top+r,range.Left+c),values[c]);}});
 private void Set(IXLCell cell,string? text)
 {
  if(Sheet.IsProtected&&cell.Style.Protection.Locked)throw new InvalidOperationException("This cell is protected.");
  text??="";if(text.StartsWith('=')){cell.FormulaA1=text[1..];return;}cell.FormulaA1="";
  if(text.StartsWith('\''))cell.Value=text[1..];else if(double.TryParse(text,NumberStyles.Float|NumberStyles.AllowThousands,CultureInfo.CurrentCulture,out var number)&&double.IsFinite(number))cell.Value=number;else if(bool.TryParse(text,out var boolean))cell.Value=boolean;else cell.Value=text;
 }
 public string Copy(SpreadsheetSelection range)=>string.Join('\n',Enumerable.Range(range.Top,range.Bottom-range.Top+1).Select(r=>string.Join('\t',Enumerable.Range(range.Left,range.Right-range.Left+1).Select(c=>EditText(r,c)))));
 public void Clear(SpreadsheetSelection range)=>Mutate(()=>{EnsureWritable(range);Sheet.Range(range.RangeAddress).Clear(XLClearOptions.Contents);});
 public void Format(SpreadsheetSelection range,string property,string value)=>Mutate(()=>{
  EnsureWritable(range);var style=Sheet.Range(range.RangeAddress).Style;
  switch(property){case "bold":style.Font.Bold=bool.Parse(value);break;case "italic":style.Font.Italic=bool.Parse(value);break;case "number":style.NumberFormat.Format=value;break;case "fill":style.Fill.BackgroundColor=XLColor.FromHtml(value);break;case "color":style.Font.FontColor=XLColor.FromHtml(value);break;case "wrap":style.Alignment.WrapText=bool.Parse(value);break;case "align":style.Alignment.Horizontal=Enum.Parse<XLAlignmentHorizontalValues>(value);break;default:throw new ArgumentException("Unknown format property.");}
 });
 public void Merge(SpreadsheetSelection range,bool merge)=>Mutate(()=>{EnsureWritable(range);if(merge)Sheet.Range(range.RangeAddress).Merge();else Sheet.Range(range.RangeAddress).Unmerge();});
 public void FillDown(SpreadsheetSelection range)=>Mutate(()=>{EnsureWritable(range);for(int r=range.Top+1;r<=range.Bottom;r++)for(int c=range.Left;c<=range.Right;c++)Sheet.Cell(range.Top,c).CopyTo(Sheet.Cell(r,c));});
 public void Sort(SpreadsheetSelection range,bool ascending)=>Mutate(()=>{EnsureWritable(range);Sheet.Range(range.RangeAddress).Sort(1,ascending?XLSortOrder.Ascending:XLSortOrder.Descending);});
 public void InsertRow(int row)=>Mutate(()=>{EnsureUnprotected();Sheet.Row(row).InsertRowsAbove(1);});
 public void DeleteRow(int row)=>Mutate(()=>{EnsureUnprotected();Sheet.Row(row).Delete();});
 public void InsertColumn(int column)=>Mutate(()=>{EnsureUnprotected();Sheet.Column(column).InsertColumnsBefore(1);});
 public void DeleteColumn(int column)=>Mutate(()=>{EnsureUnprotected();Sheet.Column(column).Delete();});
 public int FrozenRows=>Sheet.SheetView.SplitRow;
 public int FrozenColumns=>Sheet.SheetView.SplitColumn;
 public void Freeze(int rows,int columns)=>Mutate(()=>
 {
  EnsureUnprotected();
  if(rows<0||rows>=1048576||columns<0||columns>=16384)throw new ArgumentOutOfRangeException(nameof(rows),"Freeze counts must leave at least one unfrozen cell.");
  Sheet.SheetView.Freeze(rows,columns);
 });
 /// <summary>Apply a case-insensitive text filter to an absolute column. The range includes its header row.</summary>
 public void ApplyTextFilter(SpreadsheetSelection range,int column,string text,SpreadsheetTextFilter condition=SpreadsheetTextFilter.Contains)=>Mutate(()=>
 {
  EnsureUnprotected();
  if(range.Bottom<=range.Top||column<range.Left||column>range.Right)throw new ArgumentException("Choose a header and at least one data row, with the filter column inside that range.");
  var filter=Sheet.AutoFilter;
  if(filter.IsEnabled&&filter.Range.RangeAddress.ToStringRelative()!=range.RangeAddress)
   throw new InvalidOperationException("Clear existing filters before choosing a different filter range.");
  if(!filter.IsEnabled)filter=Sheet.Range(range.RangeAddress).SetAutoFilter();
  var target=filter.Column(column-range.Left+1);
  target.Clear(false);
  switch(condition)
  {
   case SpreadsheetTextFilter.Contains:target.Contains(EscapeFilterText(text),false);break;
   case SpreadsheetTextFilter.Equals:target.AddFilter(text,false);break;
   case SpreadsheetTextFilter.NotContains:target.NotContains(EscapeFilterText(text),false);break;
   default:throw new ArgumentException("Unknown text filter condition.");
  }
 });
 public void ClearFilter(int column)=>Mutate(()=>
 {
  EnsureUnprotected();if(!Sheet.AutoFilter.IsEnabled)return;
  var range=Sheet.AutoFilter.Range.RangeAddress;
  if(column<range.FirstAddress.ColumnNumber||column>range.LastAddress.ColumnNumber)throw new ArgumentException("Column is outside the filter range.");
  RemoveFilterXml(column-range.FirstAddress.ColumnNumber);
 });
 public void ClearFilters()=>Mutate(()=>{EnsureUnprotected();if(Sheet.AutoFilter.IsEnabled)RemoveFilterXml(null);});
 public void ReapplyFilters()=>Mutate(EnsureUnprotected);
 private void RefreshFilters()
 {
  if(!Sheet.AutoFilter.IsEnabled)return;
  // ClosedXML custom filters inspect cached values. Evaluate formulas in the
  // filter area first, so dependent edits cannot leave row visibility stale.
  foreach(var cell in Sheet.AutoFilter.Range.CellsUsed().Where(c=>c.HasFormula))_ = cell.Value;
  Sheet.AutoFilter.Reapply();
 }
 private static string EscapeFilterText(string text)=>text.Replace("~","~~").Replace("*","~*").Replace("?","~?");
 private void RemoveFilterXml(int? relativeColumn)
 {
  // ClosedXML 0.105 retains cleared columns as FilterType.None, which its XLSX writer
  // cannot serialize. Remove only the requested OOXML criteria, then reload through
  // the existing workbook loader so other columns (including imported filters) survive.
  var range=Sheet.AutoFilter.Range.RangeAddress;
  var firstRow=range.FirstAddress.RowNumber+1;var lastRow=range.LastAddress.RowNumber;
  using var buffer=new MemoryStream();buffer.Write(Save());buffer.Position=0;
  using(var package=DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(buffer,true))
  {
   var part=package.WorkbookPart!;
   var sheet=part.Workbook.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().Single(s=>s.Name?.Value==ActiveSheet);
   var worksheet=(DocumentFormat.OpenXml.Packaging.WorksheetPart)part.GetPartById(sheet.Id!);
   var filter=worksheet.Worksheet.GetFirstChild<DocumentFormat.OpenXml.Spreadsheet.AutoFilter>();
   if(relativeColumn is null)filter?.Remove();
   else foreach(var column in filter?.Elements<DocumentFormat.OpenXml.Spreadsheet.FilterColumn>().Where(c=>c.ColumnId?.Value==(uint)relativeColumn).ToArray()??[])column.Remove();
   worksheet.Worksheet.Save();
  }
  var next=LoadWorkbook(buffer.ToArray());
  Workbook.Dispose();Workbook=next;
  Sheet.Rows(firstRow,lastRow).Unhide();
 }
 public void AddSheet(string name)=>Mutate(()=>{ActiveSheet=Workbook.AddWorksheet(name).Name;});
 public void RenameSheet(string name)=>Mutate(()=>{Sheet.Name=name;ActiveSheet=name;});
 public void DeleteSheet(){if(Workbook.Worksheets.Count<=1)throw new InvalidOperationException("Keep at least one worksheet.");Mutate(()=>{Sheet.Delete();ActiveSheet=Workbook.Worksheets.First().Name;});}
 private void EnsureUnprotected(){if(Sheet.IsProtected)throw new InvalidOperationException("This worksheet is protected.");}
 private void EnsureWritable(SpreadsheetSelection range){if(Sheet.IsProtected&&Sheet.Range(range.RangeAddress).Cells().Any(c=>c.Style.Protection.Locked))throw new InvalidOperationException("The selection contains protected cells.");}
 public void Mutate(Action action){var snapshot=Save();var active=ActiveSheet;try{action();RefreshFilters();Push(_undo,snapshot);_redo.Clear();}catch{Workbook.Dispose();Workbook=LoadWorkbook(snapshot);ActiveSheet=active;throw;}}
 private void Push(List<byte[]> history,byte[] bytes){history.Add(bytes);while(history.Count>Math.Max(1,HistoryLimit))history.RemoveAt(0);}
 public void Undo(){if(!CanUndo)return;Push(_redo,Save());Restore(_undo);}
 public void Redo(){if(!CanRedo)return;Push(_undo,Save());Restore(_redo);}
 private void Restore(List<byte[]> history){var bytes=history[^1];history.RemoveAt(history.Count-1);var active=ActiveSheet;Workbook.Dispose();Workbook=LoadWorkbook(bytes);ActiveSheet=Workbook.Worksheets.Any(s=>s.Name==active)?active:Workbook.Worksheets.First().Name;}
 public byte[] Save(){var stream=new MemoryStream();try{Workbook.SaveAs(stream,new SaveOptions{EvaluateFormulasBeforeSaving=false,ValidatePackage=false});var previous=_saveStream;_saveStream=stream;previous?.Dispose();return stream.ToArray();}catch{stream.Dispose();throw;}}
 public void Dispose(){Workbook.Dispose();_saveStream?.Dispose();}
}
