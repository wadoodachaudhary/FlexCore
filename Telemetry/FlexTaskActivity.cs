using System.Text;

namespace Fx.ControlKit.Telemetry;

/// <summary>
/// One entry of the host's task list: the name the user sees, the routes it opens,
/// and the names under which a launch of it is recorded (its title always counts).
/// </summary>
public sealed record TelemetryTaskItem(
    string Title,
    IReadOnlyList<string> Routes,
    IReadOnlyList<string>? LaunchNames = null);

/// <summary>A section of the host's task list, in the order the host shows it.</summary>
public sealed record TelemetryTaskGroup(string Title, IReadOnlyList<TelemetryTaskItem> Items);

/// <summary>Usage of one function (a button, field, grid action, menu entry) inside a task.</summary>
public sealed class FlexTaskFunctionRow
{
    public string Area { get; set; } = string.Empty;
    public string Function { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Count { get; set; }
    public int Users { get; set; }
    public double SharePercent { get; set; }
    public string? LastValue { get; set; }
    public DateTime LastUsedLocal { get; set; }
}

/// <summary>Usage of one part of a task's screen: its main window, a dialog, a toolbar.</summary>
public sealed class FlexTaskAreaRow
{
    public string Area { get; set; } = string.Empty;
    public int Count { get; set; }
    public int Functions { get; set; }
}

/// <summary>One user's activity inside a task.</summary>
public sealed class FlexTaskUserRow
{
    public string UserId { get; set; } = string.Empty;
    public int Launches { get; set; }
    public int Opens { get; set; }
    public int Actions { get; set; }
    public DateTime LastUsedLocal { get; set; }
}

/// <summary>Everything recorded for one task of the task list.</summary>
public sealed class FlexTaskActivity
{
    public string Key { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int Launches { get; set; }
    public int Opens { get; set; }
    public int Actions { get; set; }
    public int Errors { get; set; }
    public int Users { get; set; }
    public double ActiveMinutes { get; set; }
    public DateTime? LastUsedLocal { get; set; }
    public IReadOnlyList<FlexTaskFunctionRow> Functions { get; set; } = Array.Empty<FlexTaskFunctionRow>();
    public IReadOnlyList<FlexTaskAreaRow> Areas { get; set; } = Array.Empty<FlexTaskAreaRow>();
    public IReadOnlyList<FlexTaskUserRow> UserRows { get; set; } = Array.Empty<FlexTaskUserRow>();

    /// <summary>True when nothing at all was recorded for the task in the window.</summary>
    public bool Unused => Launches == 0 && Opens == 0 && Actions == 0;
}

/// <summary>A section of the task list with the activity of each of its tasks.</summary>
public sealed class FlexTaskActivityGroup
{
    public string Title { get; set; } = string.Empty;
    public IReadOnlyList<FlexTaskActivity> Items { get; set; } = Array.Empty<FlexTaskActivity>();
    public int Actions => Items.Sum(i => i.Actions);
}

/// <summary>The task list with its recorded activity.</summary>
public sealed class FlexTaskActivityReport
{
    public IReadOnlyList<FlexTaskActivityGroup> Groups { get; set; } = Array.Empty<FlexTaskActivityGroup>();
    public int EventCount { get; set; }

    public IEnumerable<FlexTaskActivity> AllItems => Groups.SelectMany(g => g.Items);
}

/// <summary>
/// Sorts recorded events under the host's task list. An event belongs to the task whose
/// route is the longest match of the event's own route; a launch belongs to the task it names.
/// Events on a screen no task opens are listed under a last group, one entry per screen.
/// </summary>
public static class FlexTaskActivityEngine
{
    public const string OtherGroupTitle = "Other screens";
    public const string MainArea = "Main window";

    public static FlexTaskActivityReport Build(
        IReadOnlyList<FlexTelemetryEvent> events,
        IReadOnlyList<TelemetryTaskGroup>? groups,
        ITelemetryCatalogProvider? catalog = null,
        string? userId = null)
    {
        groups ??= Array.Empty<TelemetryTaskGroup>();
        var groupAccs = new List<(string Title, List<TaskAcc> Items)>();
        var byRoute = new List<(string Prefix, TaskAcc Acc)>();
        var byName = new Dictionary<string, TaskAcc>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in groups)
        {
            var items = new List<TaskAcc>();
            foreach (var item in g.Items)
            {
                var acc = new TaskAcc(g.Title, item.Title);
                items.Add(acc);
                foreach (var route in item.Routes)
                {
                    var prefix = RoutePath(route);
                    if (prefix.Length > 0)
                        byRoute.Add((prefix, acc));
                }
                byName.TryAdd(item.Title, acc);
                if (item.LaunchNames is not null)
                {
                    foreach (var name in item.LaunchNames)
                        byName.TryAdd(name, acc);
                }
            }
            groupAccs.Add((g.Title, items));
        }
        byRoute.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length));

        var others = new Dictionary<string, TaskAcc>(StringComparer.OrdinalIgnoreCase);
        bool filterUser = !string.IsNullOrWhiteSpace(userId) && !string.Equals(userId, "All", StringComparison.OrdinalIgnoreCase);
        int counted = 0;

        foreach (var evt in events)
        {
            if (filterUser && !string.Equals(evt.UserId, userId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (evt.Kind is TelemetryEventKind.Session or TelemetryEventKind.SqlQuery)
                continue;

            var local = evt.TimestampUtc.ToLocalTime();

            if (evt.Kind == TelemetryEventKind.TaskLaunch)
            {
                var name = !string.IsNullOrWhiteSpace(evt.Control) ? evt.Control : evt.Detail;
                if (!string.IsNullOrWhiteSpace(name) && byName.TryGetValue(name.Trim(), out var launched))
                {
                    launched.Launches++;
                    launched.User(evt.UserId).Launches++;
                    launched.Touch(evt.UserId, local);
                    counted++;
                }
                continue;
            }

            var path = RoutePath(evt.Route);
            TaskAcc? task = null;
            foreach (var (prefix, acc) in byRoute)
            {
                if (path.Length == prefix.Length
                        ? string.Equals(path, prefix, StringComparison.Ordinal)
                        : path.Length > prefix.Length && path[prefix.Length] == '/' && path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    task = acc;
                    break;
                }
            }

            if (task is null)
            {
                var title = catalog is not null
                    ? catalog.ResolveScreenInfo(evt.Route, evt.Screen).ScreenTitle
                    : evt.Screen;
                if (string.IsNullOrWhiteSpace(title))
                    continue;
                if (!others.TryGetValue(title, out task))
                {
                    task = new TaskAcc(OtherGroupTitle, title);
                    others[title] = task;
                }
            }

            counted++;
            switch (evt.Kind)
            {
                case TelemetryEventKind.ScreenOpen:
                    task.Opens++;
                    task.User(evt.UserId).Opens++;
                    task.Touch(evt.UserId, local);
                    break;

                case TelemetryEventKind.ScreenFocus:
                    if (evt.DurationMs is > 0)
                        task.ActiveMs += evt.DurationMs.Value;
                    task.Touch(evt.UserId, local);
                    break;

                case TelemetryEventKind.ScreenClose:
                    break;

                case TelemetryEventKind.UnhandledError:
                    task.Errors++;
                    task.Touch(evt.UserId, local);
                    break;

                default:
                    if (IsShellControl(evt))
                        break;
                    task.Actions++;
                    task.User(evt.UserId).Actions++;
                    task.Touch(evt.UserId, local);
                    task.Function(evt, local);
                    break;
            }
        }

        var result = new List<FlexTaskActivityGroup>();
        foreach (var (title, items) in groupAccs)
            result.Add(new FlexTaskActivityGroup { Title = title, Items = items.Select(i => i.ToActivity()).ToList() });

        if (others.Count > 0)
        {
            result.Add(new FlexTaskActivityGroup
            {
                Title = OtherGroupTitle,
                Items = others.Values
                    .Select(i => i.ToActivity())
                    .Where(i => !i.Unused)
                    .OrderByDescending(i => i.Actions)
                    .ThenByDescending(i => i.Opens)
                    .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            });
        }

        return new FlexTaskActivityReport { Groups = result, EventCount = counted };
    }

    /// <summary>The path of a route in lower case: no query, no trailing slash, escapes decoded.</summary>
    public static string RoutePath(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
            return string.Empty;
        var path = route.Trim();
        int cut = path.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0)
            path = path[..cut];
        if (path.Contains('%'))
        {
            try { path = Uri.UnescapeDataString(path); }
            catch (UriFormatException) { }
        }
        path = path.TrimEnd('/');
        if (path.Length > 0 && path[0] != '/')
            path = "/" + path;
        return path.ToLowerInvariant();
    }

    /// <summary>The part of the screen an event happened in, read from its location path.</summary>
    public static string AreaOf(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return MainArea;
        var parts = location.Split(" > ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length <= 2)
            return MainArea;
        var sb = new StringBuilder();
        for (int i = 1; i < parts.Length - 1; i++)
        {
            if (sb.Length > 0)
                sb.Append(" > ");
            var part = parts[i];
            int colon = part.IndexOf(':');
            if (colon > 0 && colon < part.Length - 1)
                sb.Append(part[..colon].Replace("Control", string.Empty, StringComparison.Ordinal)).Append(": ").Append(part[(colon + 1)..]);
            else
                sb.Append(part);
        }
        return sb.Length == 0 ? MainArea : sb.ToString();
    }

    /// <summary>"RowDoubleClicked" as "Row double clicked".</summary>
    public static string Words(string? pascal)
    {
        if (string.IsNullOrWhiteSpace(pascal))
            return string.Empty;
        var sb = new StringBuilder(pascal.Length + 8);
        for (int i = 0; i < pascal.Length; i++)
        {
            char c = pascal[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(pascal[i - 1]) && pascal[i - 1] != ' ')
            {
                sb.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static bool IsShellControl(FlexTelemetryEvent evt) =>
        evt.Category.StartsWith("Mdi", StringComparison.OrdinalIgnoreCase);

    private static (string Kind, string Function) Describe(FlexTelemetryEvent evt)
    {
        var name = evt.Control;
        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(evt.Location))
        {
            var last = evt.Location[(evt.Location.LastIndexOf(" > ", StringComparison.Ordinal) + 1)..].Trim(' ', '>');
            int colon = last.IndexOf(':');
            name = colon >= 0 ? last[(colon + 1)..] : last;
        }

        switch (evt.Kind)
        {
            case TelemetryEventKind.GridOperation:
                {
                    var verb = Words(string.IsNullOrWhiteSpace(evt.Action) ? "Action" : evt.Action);
                    return ("Grid", string.IsNullOrWhiteSpace(evt.Tag) ? verb : $"{verb} ({evt.Tag})");
                }
            case TelemetryEventKind.ControlChange:
                return ("Field", Named(name, evt));
            case TelemetryEventKind.MenuAction:
                return ("Menu", Named(name, evt));
            case TelemetryEventKind.ReportRun:
                return ("Report", Named(name, evt));
            case TelemetryEventKind.InquiryRun:
                return ("Inquiry", Named(name, evt));
            case TelemetryEventKind.ShortcutKey:
                return ("Shortcut", Named(name, evt));
            default:
                return (evt.Category.Contains("Tab", StringComparison.OrdinalIgnoreCase) ? "Tab" : "Button", Named(name, evt));
        }
    }

    private static string Named(string? name, FlexTelemetryEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return name.Trim();
        if (!string.IsNullOrWhiteSpace(evt.Tag))
            return evt.Tag!;
        return string.IsNullOrWhiteSpace(evt.Action) ? "(unnamed)" : Words(evt.Action);
    }

    private sealed class TaskAcc(string group, string title)
    {
        private readonly Dictionary<string, FunctionAcc> _functions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, UserAcc> _users = new(StringComparer.OrdinalIgnoreCase);

        public int Launches;
        public int Opens;
        public int Actions;
        public int Errors;
        public long ActiveMs;
        public DateTime? Last;

        public UserAcc User(string? userId)
        {
            var key = string.IsNullOrWhiteSpace(userId) ? "(not signed in)" : userId;
            if (!_users.TryGetValue(key, out var acc))
            {
                acc = new UserAcc(key);
                _users[key] = acc;
            }
            return acc;
        }

        public void Touch(string? userId, DateTime local)
        {
            if (Last is null || local > Last)
                Last = local;
            var user = User(userId);
            if (local > user.Last)
                user.Last = local;
        }

        public void Function(FlexTelemetryEvent evt, DateTime local)
        {
            var area = AreaOf(evt.Location);
            var (kind, function) = Describe(evt);
            var key = area + "|" + kind + "|" + function;
            if (!_functions.TryGetValue(key, out var acc))
            {
                acc = new FunctionAcc(area, kind, function);
                _functions[key] = acc;
            }
            acc.Count++;
            if (!string.IsNullOrWhiteSpace(evt.UserId))
                acc.Users.Add(evt.UserId);
            if (local >= acc.Last)
            {
                acc.Last = local;
                acc.LastValue = evt.Value;
            }
        }

        public FlexTaskActivity ToActivity()
        {
            var functions = _functions.Values
                .OrderByDescending(f => f.Count)
                .ThenBy(f => f.Function, StringComparer.OrdinalIgnoreCase)
                .Select(f => new FlexTaskFunctionRow
                {
                    Area = f.Area,
                    Kind = f.Kind,
                    Function = f.Function,
                    Count = f.Count,
                    Users = f.Users.Count,
                    SharePercent = Actions == 0 ? 0 : Math.Round(100.0 * f.Count / Actions, 1),
                    LastValue = f.LastValue,
                    LastUsedLocal = f.Last
                })
                .ToList();

            return new FlexTaskActivity
            {
                Key = group + "|" + title,
                Group = group,
                Title = title,
                Launches = Launches,
                Opens = Opens,
                Actions = Actions,
                Errors = Errors,
                Users = _users.Values.Count(u => u.Launches + u.Opens + u.Actions > 0 || u.Last > DateTime.MinValue),
                ActiveMinutes = Math.Round(ActiveMs / 60000.0, 1),
                LastUsedLocal = Last,
                Functions = functions,
                Areas = functions
                    .GroupBy(f => f.Area, StringComparer.OrdinalIgnoreCase)
                    .Select(a => new FlexTaskAreaRow { Area = a.Key, Count = a.Sum(f => f.Count), Functions = a.Count() })
                    .OrderByDescending(a => a.Count)
                    .ToList(),
                UserRows = _users.Values
                    .OrderByDescending(u => u.Actions)
                    .ThenByDescending(u => u.Last)
                    .Select(u => new FlexTaskUserRow
                    {
                        UserId = u.UserId,
                        Launches = u.Launches,
                        Opens = u.Opens,
                        Actions = u.Actions,
                        LastUsedLocal = u.Last
                    })
                    .ToList()
            };
        }
    }

    private sealed class FunctionAcc(string area, string kind, string function)
    {
        public string Area { get; } = area;
        public string Kind { get; } = kind;
        public string Function { get; } = function;
        public int Count;
        public HashSet<string> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime Last = DateTime.MinValue;
        public string? LastValue;
    }

    private sealed class UserAcc(string userId)
    {
        public string UserId { get; } = userId;
        public int Launches;
        public int Opens;
        public int Actions;
        public DateTime Last = DateTime.MinValue;
    }
}
