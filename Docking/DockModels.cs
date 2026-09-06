using System.Text.Json;
using Microsoft.AspNetCore.Components;
namespace Fx.ControlKit.Docking;

public enum DockPosition { Tab, Left, Right, Top, Bottom }
public sealed record DockPanel(string Key, string Title, RenderFragment Content, bool Closable = true, bool Floatable = true);
public sealed class DockNode
{
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    public List<string> Panels {get;set;}=[];
    public string? ActivePanel {get;set;}
    public List<DockNode> Children {get;set;}=[];
    public bool Vertical {get;set;}=true;
    public double Ratio {get;set;}=50;
}
public sealed class DockFloatingPane
{
    public string Panel {get;set;}="";
    public string Width {get;set;}="520px";
    public string Height {get;set;}="360px";
    public DialogPosition Position {get;set;}=new(0,0);
}
public sealed class DockLayoutState
{
    public DockNode Root {get;set;}=new();
    public List<DockFloatingPane> Floating {get;set;}=[];
    public List<string> AutoHidden {get;set;}=[];
    public List<string> Closed {get;set;}=[];
    public DockLayoutState Clone()=>JsonSerializer.Deserialize<DockLayoutState>(JsonSerializer.Serialize(this))!;
    public string ToJson()=>JsonSerializer.Serialize(this);
    public static DockLayoutState FromJson(string json)=>JsonSerializer.Deserialize<DockLayoutState>(json)??throw new ArgumentException("Dock layout cannot be null.");
    public IEnumerable<DockNode> Nodes()=>Enumerate(Root);
    private static IEnumerable<DockNode> Enumerate(DockNode node){yield return node;foreach(var child in node.Children)foreach(var descendant in Enumerate(child))yield return descendant;}
    public void Validate(IEnumerable<string> keys)
    {
        var allowed=keys.ToHashSet(StringComparer.Ordinal);var nodes=new HashSet<string>();var visited=new HashSet<DockNode>();var panels=new HashSet<string>();
        void Add(string key){if(!allowed.Contains(key)||!panels.Add(key))throw new ArgumentException($"Unknown or duplicate dock panel '{key}'.");}
        void Walk(DockNode node,int depth)
        {
            if(depth>30||!visited.Add(node)||!nodes.Add(node.Id)||string.IsNullOrWhiteSpace(node.Id))throw new ArgumentException("Dock layout has duplicate, cyclic or excessively nested nodes.");
            if(node.Children.Count is not (0 or 2)||node.Children.Count>0&&node.Panels.Count>0)throw new ArgumentException("A dock node is a tab group or a two-pane split.");
            if(!double.IsFinite(node.Ratio)||node.Ratio<5||node.Ratio>95)throw new ArgumentException("Dock split ratio must be between 5 and 95.");
            foreach(var key in node.Panels)Add(key);
            if(node.ActivePanel!=null&&!node.Panels.Contains(node.ActivePanel))throw new ArgumentException("The active panel must belong to its tab group.");
            foreach(var child in node.Children)Walk(child,depth+1);
        }
        Walk(Root,0);
        foreach(var pane in Floating){Add(pane.Panel);if(!double.IsFinite(pane.Position.X)||!double.IsFinite(pane.Position.Y))throw new ArgumentException("Invalid floating pane position.");}
        foreach(var key in AutoHidden.Concat(Closed))Add(key);
        if(!panels.SetEquals(allowed))throw new ArgumentException("Every dock panel must have a layout location.");
    }
    internal void Remove(string key)
    {
        foreach(var node in Nodes()){node.Panels.Remove(key);if(node.ActivePanel==key)node.ActivePanel=node.Panels.FirstOrDefault();}
        Floating.RemoveAll(p=>p.Panel==key);AutoHidden.Remove(key);Closed.Remove(key);
    }
    internal void Compact()=>Root=CompactNode(Root);
    private static DockNode CompactNode(DockNode node)
    {
        node.Children=node.Children.Select(CompactNode).ToList();
        if(node.Children.Count==2)
        {
            if(node.Children[0].Children.Count==0&&node.Children[0].Panels.Count==0)return node.Children[1];
            if(node.Children[1].Children.Count==0&&node.Children[1].Panels.Count==0)return node.Children[0];
        }
        return node;
    }
}
