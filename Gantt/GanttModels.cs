using System.ComponentModel.DataAnnotations;
namespace Fx.ControlKit.Gantt;
public enum GanttView { Day, Week, Month, Year }
public enum GanttChangeAction { Create, Update, Delete }
public sealed class GanttTask
{
    [Display(AutoGenerateField=false)] public string Id {get;set;}=Guid.NewGuid().ToString("N");
    [Required,MinLength(1),Display(Order=0)] public string Title {get;set;}="New task";
    [Display(Order=1)] public DateTime Start {get;set;}=DateTime.Today;
    [Display(Order=2)] public DateTime End {get;set;}=DateTime.Today.AddDays(1);
    [Range(0,100),Display(Name="Progress (%)",Order=3)] public int Progress {get;set;}
    [Display(Name="Parent task ID",Order=4)] public string? ParentId {get;set;}
    [Display(Name="Predecessor task IDs (comma-separated)",Order=5)] public string Predecessors {get;set;}="";
    [Display(AutoGenerateField=false)] public string Color {get;set;}="#2563eb";
    public GanttTask Clone()=>(GanttTask)MemberwiseClone();
    internal string[] PredecessorIds=>Predecessors.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
}
public sealed class GanttTaskChangingArgs
{
    public required GanttChangeAction Action {get;init;}
    public GanttTask? Previous {get;init;}
    public required GanttTask Task {get;init;}
    public bool Cancel {get;set;}
}
internal static class GanttSchedule
{
    internal static void Validate(IReadOnlyList<GanttTask> tasks,bool enforceDependencies)
    {
        var map=new Dictionary<string,GanttTask>(StringComparer.Ordinal);
        foreach(var task in tasks)
        {
            if(string.IsNullOrWhiteSpace(task.Id)||!map.TryAdd(task.Id,task))throw new ArgumentException("Task IDs must be unique and nonempty.");
            if(task.End<task.Start)throw new ArgumentException($"'{task.Title}' ends before it starts.");
            Validator.ValidateObject(task,new ValidationContext(task),true);
        }
        foreach(var task in tasks)
        {
            if(!string.IsNullOrEmpty(task.ParentId)&&!map.ContainsKey(task.ParentId))throw new ArgumentException($"Unknown parent '{task.ParentId}'.");
            foreach(var predecessor in task.PredecessorIds)
            {
                if(!map.TryGetValue(predecessor,out var prior))throw new ArgumentException($"Unknown predecessor '{predecessor}'.");
                if(enforceDependencies&&prior.End>task.Start)throw new ArgumentException($"'{task.Title}' must start after '{prior.Title}' finishes.");
            }
        }
        void CheckCycles(Func<GanttTask,IEnumerable<string>> edges)
        {
            var active=new HashSet<string>();var visited=new HashSet<string>();
            void Visit(string id,int depth){if(active.Contains(id)||depth>1000)throw new ArgumentException("Task hierarchy or dependencies contain a cycle or excessive depth.");if(!visited.Add(id))return;active.Add(id);foreach(var next in edges(map[id]))Visit(next,depth+1);active.Remove(id);}
            foreach(var id in map.Keys)Visit(id,0);
        }
        CheckCycles(t=>string.IsNullOrEmpty(t.ParentId)?Array.Empty<string>():[t.ParentId]);
        CheckCycles(t=>t.PredecessorIds);
    }
}
