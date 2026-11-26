using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Fx.ControlKit.WorkTracking;

/// <summary>SQLite is authoritative. JSON folders are recoverable, atomic projections of each committed board.</summary>
public sealed class LocalWorkBoardStore
{
    public static JsonSerializerOptions Json { get; } = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;
    public string RootDirectory { get; }

    public LocalWorkBoardStore(LocalWorkBoardOptions options, TimeProvider? time = null)
    {
        RootDirectory = Path.GetFullPath(options.RootDirectory);
        _time = time ?? TimeProvider.System;
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(RootDirectory);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(RootDirectory, "board.sqlite"), DefaultTimeout = 30
        }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS work_projects(id TEXT PRIMARY KEY, project_key TEXT UNIQUE NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS work_events(id INTEGER PRIMARY KEY AUTOINCREMENT, project_id TEXT NOT NULL,
                item_key TEXT NOT NULL, actor TEXT NOT NULL, action TEXT NOT NULL, detail TEXT NOT NULL, at_utc TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        return db;
    }

    public async Task<IReadOnlyList<WorkProject>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var db = Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM work_projects ORDER BY project_key";
            using var reader = cmd.ExecuteReader();
            var result = new List<WorkProject>();
            while (reader.Read()) result.Add(Deserialize(reader.GetString(0)).Project);
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<WorkBoardSnapshot?> GetAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { using var db = Open(); return Read(db, null, id); }
        finally { _gate.Release(); }
    }

    public Task<WorkBoardSnapshot> CreateAsync(WorkProject project, IReadOnlyList<WorkItem> items, CancellationToken ct = default)
    {
        if (!Regex.IsMatch(project.Key, "^[A-Z][A-Z0-9]{1,11}$") || !Regex.IsMatch(project.Id, "^[a-f0-9]{32}$"))
            throw new ArgumentException("Use a project key of 2–12 uppercase letters/digits and a generated project ID.");
        if (string.IsNullOrWhiteSpace(project.Name) || string.IsNullOrWhiteSpace(project.SourceFingerprint))
            throw new ArgumentException("The project needs a name and source fingerprint.");
        ValidatePlan(project, items);
        return WriteAsync(project.Key, "planner", "plan-created", "", "Source analysis created a draft backlog.", existing =>
        {
            if (existing is not null)
            {
                if (existing.Project.SourceFingerprint == project.SourceFingerprint && existing.Project.PlanVersion == project.PlanVersion
                    && existing.Project.SourcePath == project.SourcePath) return existing;
                throw new InvalidOperationException("This project key already has a different source snapshot. Use a new key; existing work is never overwritten.");
            }
            return new(project with { Approved = false }, items.Select(i => i with
            {
                State = WorkItemState.Draft, LeaseToken = null, LeaseExpiresUtc = null, Attempts = 0,
                ResultRevision = "", VerificationEvidence = ""
            }).ToArray());
        }, ct);
    }

    public Task<WorkBoardSnapshot> ApproveAsync(string project, string actor, CancellationToken ct = default) =>
        WriteAsync(project, actor, "plan-approved", "", "Approved for task claiming, not for completion.", current =>
        {
            var board = Required(current);
            return board with { Project = board.Project with { Approved = true }, Items = board.Items.Select(i =>
                i.State == WorkItemState.Draft ? i with { State = WorkItemState.Ready } : i).ToArray() };
        }, ct);

    public Task<WorkBoardSnapshot> AssignAsync(string project, string key, string agent, string actor, CancellationToken ct = default) =>
        WriteAsync(project, actor, "assigned", key, agent, current => Change(Required(current), key, item =>
        {
            if (item.Kind != WorkItemKind.Task || item.State is WorkItemState.InProgress or WorkItemState.Review or WorkItemState.Done)
                throw new InvalidOperationException("Only unstarted tasks can be assigned.");
            return item with { Assignee = agent.Trim() };
        }), ct);

    public async Task<WorkClaim?> ClaimNextAsync(string project, string agent, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        WorkClaim? claim = null;
        await WriteAsync(project, agent, "claim", "", "Claimed a ready dependency-satisfied task, or found no eligible work.", current =>
        {
            var board = Required(current);
            if (!board.Project.Approved) throw new InvalidOperationException("Review and approve the plan before assigning work to agents.");
            var now = _time.GetUtcNow();
            var items = board.Items.Select(i => i.State == WorkItemState.InProgress && i.LeaseExpiresUtc <= now
                ? i with { State = WorkItemState.Ready, LeaseToken = null, LeaseExpiresUtc = null } : i).ToArray();
            var item = items.FirstOrDefault(i => i.Kind == WorkItemKind.Task && i.State == WorkItemState.Ready
                && (i.Assignee.Length == 0 || i.Assignee == agent)
                && i.DependsOn.All(key => items.Any(d => d.Key == key && d.State == WorkItemState.Done)));
            if (item is not null)
            {
                var token = Guid.NewGuid().ToString("N");
                var expires = now.AddMinutes(30);
                var claimed = item with { State = WorkItemState.InProgress, Assignee = agent, LeaseToken = token,
                    LeaseExpiresUtc = expires, Attempts = item.Attempts + 1 };
                claim = new(board.Project.Id, claimed, token, expires);
                items = items.Select(i => i.Key == item.Key ? claimed : i).ToArray();
            }
            return board with { Items = items };
        }, ct);
        return claim;
    }

    public Task<WorkBoardSnapshot> SubmitAsync(string project, string key, string agent, string token,
        string revision, string verification, CancellationToken ct = default) =>
        WriteAsync(project, agent, "submitted-for-review", key, verification, current => Change(Required(current), key, item =>
        {
            CheckLease(item, agent, token);
            if (string.IsNullOrWhiteSpace(revision) || string.IsNullOrWhiteSpace(verification))
                throw new ArgumentException("A result revision/artifact hash and verification evidence are required. Submission is not approval.");
            return item with { State = WorkItemState.Review, ResultRevision = revision.Trim(), VerificationEvidence = verification.Trim(),
                LeaseToken = null, LeaseExpiresUtc = null };
        }), ct);

    public Task<WorkBoardSnapshot> RenewAsync(string project, string key, string agent, string token, CancellationToken ct = default) =>
        WriteAsync(project, agent, "lease-renewed", key, "", current => Change(Required(current), key, item =>
        { CheckLease(item, agent, token); return item with { LeaseExpiresUtc = _time.GetUtcNow().AddMinutes(30) }; }), ct);

    public Task<WorkBoardSnapshot> ReviewAsync(string project, string key, string reviewer, bool accepted, string note, CancellationToken ct = default) =>
        WriteAsync(project, reviewer, accepted ? "accepted" : "changes-requested", key, note, current =>
        {
            if (string.IsNullOrWhiteSpace(note)) throw new ArgumentException("A review note is required.");
            var board = Change(Required(current), key, item =>
            {
                if (item.State != WorkItemState.Review || item.Assignee == reviewer)
                    throw new InvalidOperationException("A different reviewer must evaluate submitted work.");
                return item with { State = accepted ? WorkItemState.Done : WorkItemState.Ready };
            });
            var items = board.Items.ToArray();
            foreach (var kind in new[] { WorkItemKind.Story, WorkItemKind.Epic, WorkItemKind.Feature })
                for (var n = 0; n < items.Length; n++)
                {
                    if (items[n].Kind != kind) continue;
                    var children = items.Where(i => i.ParentKey == items[n].Key).ToArray();
                    if (children.Length > 0 && children.All(i => i.State == WorkItemState.Done)) items[n] = items[n] with { State = WorkItemState.Done };
                }
            return board with { Items = items };
        }, ct);

    public async Task<IReadOnlyList<WorkBoardEvent>> EventsAsync(string project, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var db = Open(); var board = Required(Read(db, null, project));
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id,item_key,actor,action,detail,at_utc FROM work_events WHERE project_id=$id ORDER BY id";
            cmd.Parameters.AddWithValue("$id", board.Project.Id);
            using var r = cmd.ExecuteReader(); var result = new List<WorkBoardEvent>();
            while (r.Read()) result.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), DateTimeOffset.Parse(r.GetString(5))));
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task ExportAsync(string project, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { using var db = Open(); using var tx = db.BeginTransaction(deferred: false); Export(Required(Read(db, tx, project))); tx.Commit(); }
        finally { _gate.Release(); }
    }

    private async Task<WorkBoardSnapshot> WriteAsync(string id, string actor, string action, string key, string detail,
        Func<WorkBoardSnapshot?, WorkBoardSnapshot> update, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
            var previous = Read(db, tx, id); var board = update(previous);
            if (previous is null && Read(db, tx, board.Project.Id) is not null)
                throw new InvalidOperationException("This project ID already belongs to another board.");
            if (!ReferenceEquals(previous, board))
            {
                using var cmd = db.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO work_projects(id,project_key,payload) VALUES($id,$key,$json) ON CONFLICT(id) DO UPDATE SET payload=$json";
                cmd.Parameters.AddWithValue("$id", board.Project.Id); cmd.Parameters.AddWithValue("$key", board.Project.Key);
                cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(board, Json)); cmd.ExecuteNonQuery();
                cmd.Parameters.Clear();
                cmd.CommandText = "INSERT INTO work_events(project_id,item_key,actor,action,detail,at_utc) VALUES($id,$key,$actor,$action,$detail,$time)";
                cmd.Parameters.AddWithValue("$id", board.Project.Id); cmd.Parameters.AddWithValue("$key", key);
                cmd.Parameters.AddWithValue("$actor", actor); cmd.Parameters.AddWithValue("$action", action);
                cmd.Parameters.AddWithValue("$detail", detail); cmd.Parameters.AddWithValue("$time", _time.GetUtcNow().ToString("O")); cmd.ExecuteNonQuery();
            }
            tx.Commit();
            // Take another write lock so another process cannot publish an older projection after a newer commit.
            using var projection = db.BeginTransaction(deferred: false);
            var latest = Required(Read(db, projection, board.Project.Id));
            try { Export(latest); }
            catch (IOException ex) { throw new IOException("Board committed to SQLite, but its JSON projection failed. Retry ExportAsync to repair the folder.", ex); }
            projection.Commit();
            return latest;
        }
        finally { _gate.Release(); }
    }

    private static WorkBoardSnapshot? Read(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT payload FROM work_projects WHERE id=$id OR project_key=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is string json ? Deserialize(json) : null;
    }
    private static WorkBoardSnapshot Deserialize(string json) => JsonSerializer.Deserialize<WorkBoardSnapshot>(json, Json)!;
    private static WorkBoardSnapshot Required(WorkBoardSnapshot? board) => board ?? throw new KeyNotFoundException("Project not found.");
    private static WorkBoardSnapshot Change(WorkBoardSnapshot board, string key, Func<WorkItem, WorkItem> change)
    {
        if (!board.Items.Any(i => i.Key == key)) throw new KeyNotFoundException("Ticket not found.");
        return board with { Items = board.Items.Select(i => i.Key == key ? change(i) : i).ToArray() };
    }
    private void CheckLease(WorkItem item, string agent, string token)
    {
        if (item.State != WorkItemState.InProgress || item.Assignee != agent || string.IsNullOrEmpty(token)
            || item.LeaseToken != token || item.LeaseExpiresUtc is null || item.LeaseExpiresUtc <= _time.GetUtcNow())
            throw new InvalidOperationException("A current lease owned by this agent is required.");
    }
    private void Export(WorkBoardSnapshot board)
    {
        var root = Path.Combine(RootDirectory, "projects", board.Project.Id);
        Directory.CreateDirectory(Path.Combine(root, "tickets"));
        foreach (var item in board.Items) Atomic(Path.Combine(root, "tickets", item.Key + ".json"), JsonSerializer.Serialize(item, Json));
        Atomic(Path.Combine(root, "board.json"), JsonSerializer.Serialize(board, Json));
    }
    private static void Atomic(string path, string json)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, json); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void ValidatePlan(WorkProject project, IReadOnlyList<WorkItem> items)
    {
        if (items.Count == 0 || items.Select(i => i.Key).Distinct().Count() != items.Count
            || items.Select(i => i.StableId).Distinct().Count() != items.Count) throw new ArgumentException("Plan needs unique tickets and stable IDs.");
        var map = items.ToDictionary(i => i.Key);
        foreach (var item in items)
        {
            if (!Regex.IsMatch(item.Key, "^" + project.Key + "-[1-9][0-9]*$") || string.IsNullOrWhiteSpace(item.StableId)
                || string.IsNullOrWhiteSpace(item.Title) || !Enum.IsDefined(item.Kind) || item.AcceptanceCriteria.Length == 0)
                throw new ArgumentException("Tickets need valid keys, titles, kinds, stable IDs and acceptance criteria.");
            if (item.Kind == WorkItemKind.Feature ? item.ParentKey is not null
                : item.ParentKey is null || !map.TryGetValue(item.ParentKey, out var parent) || (int)parent.Kind != (int)item.Kind - 1)
                throw new ArgumentException("Use Feature → Epic → Story → Task parentage.");
            if (item.DependsOn.Any(key => !map.TryGetValue(key, out var dependency) || key == item.Key || dependency.Kind != WorkItemKind.Task)
                || (item.DependsOn.Length > 0 && item.Kind != WorkItemKind.Task)) throw new ArgumentException("Dependencies must link tasks in this project.");
        }
        var visiting = new HashSet<string>(); var visited = new HashSet<string>();
        void Visit(string key)
        {
            if (visited.Contains(key)) return;
            if (!visiting.Add(key)) throw new ArgumentException("Task dependencies contain a cycle.");
            foreach (var dependency in map[key].DependsOn) Visit(dependency);
            visiting.Remove(key); visited.Add(key);
        }
        foreach (var key in map.Keys) Visit(key);
    }
}
