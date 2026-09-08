namespace Fx.ControlKit.Pdf;
public sealed class PdfDocumentInfo
{
    public int Pages {get;set;}
    public List<PdfFormField> Fields {get;set;}=[];
    public List<PdfBookmark> Bookmarks {get;set;}=[];
}
public sealed class PdfFormField
{
    public string Name {get;set;}="";
    public string Type {get;set;}="";
    public string TextValue {get;set;}="";
    public bool BoolValue {get;set;}
    public List<string> Options {get;set;}=[];
    public bool ReadOnly {get;set;}
    public bool Dirty {get;set;}
}
public sealed class PdfAnnotation
{
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    public int Page {get;set;}
    public string Kind {get;set;}="Text";
    public string Text {get;set;}="";
    public double X {get;set;}
    public double Y {get;set;}
    public double Width {get;set;}
    public double Height {get;set;}
}
/// <summary>A literal text occurrence. Index and Length refer to the page's extracted text.</summary>
public sealed record PdfSearchMatch(int Page,string Text,int Index=0,int Length=0);
public sealed record PdfSearchState(string Query,bool MatchCase,IReadOnlyList<PdfSearchMatch> Matches,int CurrentIndex,bool IsTruncated);
public sealed class PdfSearchResults
{
    public List<PdfSearchMatch> Matches {get;set;}=[];
    public bool IsTruncated {get;set;}
}
/// <summary>A document outline entry. Page is null for grouping or unsupported external destinations.</summary>
public sealed class PdfBookmark
{
    public string Title {get;set;}="";
    public int? Page {get;set;}
    public List<PdfBookmark> Children {get;set;}=[];
}
