using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.WorkTracking;

public sealed partial class LocalWorkBoardStore
{
    public async Task<IReadOnlyList<WorkPlanningDraft>> ListDraftsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT payload FROM work_planning_drafts ORDER BY project_key";
            using var r = cmd.ExecuteReader(); var list = new List<WorkPlanningDraft>();
            while (r.Read()) list.Add(JsonSerializer.Deserialize<WorkPlanningDraft>(r.GetString(0), Json)!);
            return list;
        }
        finally { _gate.Release(); }
    }

    public async Task<WorkPlanningDraft> SaveDraftAsync(WorkPlanningDraft draft, CancellationToken ct = default)
    {
        if (!Regex.IsMatch(draft.Key, "^[A-Z][A-Z0-9]{1,11}$") || draft.Baseline is null || string.IsNullOrWhiteSpace(draft.PlanMarkdown))
            throw new ArgumentException("A planning checkpoint requires a valid key, assessed baseline and plan.");
        await _gate.WaitAsync(ct);
        try
        {
            using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
            if (Read(db, tx, draft.Key) is not null) throw new InvalidOperationException("This key already has a published board. Use a new key to replan without resetting work.");
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT revision FROM work_planning_drafts WHERE project_key=$key";
            cmd.Parameters.AddWithValue("$key", draft.Key);
            var current = cmd.ExecuteScalar();
            if ((current is null ? 0 : Convert.ToInt32(current)) != draft.Revision)
                throw new InvalidOperationException("This plan changed in another session. Reload it before saving.");
            var saved = draft with { Revision = draft.Revision + 1 };
            cmd.CommandText = "INSERT INTO work_planning_drafts(project_key,revision,payload) VALUES($key,$revision,$json) ON CONFLICT(project_key) DO UPDATE SET revision=$revision,payload=$json";
            cmd.Parameters.AddWithValue("$revision", saved.Revision);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(saved, Json)); cmd.ExecuteNonQuery();
            tx.Commit();
            using var projection = db.BeginTransaction(deferred: false);
            using var latest = db.CreateCommand(); latest.Transaction = projection;
            latest.CommandText = "SELECT payload FROM work_planning_drafts WHERE project_key=$key";
            latest.Parameters.AddWithValue("$key", draft.Key);
            var json = (string)latest.ExecuteScalar()!;
            try
            {
                var root = Path.Combine(RootDirectory, "planning"); Directory.CreateDirectory(root);
                Atomic(Path.Combine(root, draft.Key + ".json"), json);
            }
            catch (IOException ex) { throw new IOException("Planning checkpoint committed to SQLite, but its JSON projection failed. Reload the saved plan before retrying.", ex); }
            projection.Commit(); return JsonSerializer.Deserialize<WorkPlanningDraft>(json, Json)!;
        }
        finally { _gate.Release(); }
    }

    public Task<WorkBoardSnapshot> AssignAgentAsync(string project, string key, WorkAgentProfile agent, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agent.Id) || string.IsNullOrWhiteSpace(agent.Provider) || string.IsNullOrWhiteSpace(agent.Model)
            || agent.MaxOutputTokens is < 256 or > 16384) throw new ArgumentException("Choose an agent identity, provider, model and a bounded output budget.");
        return WriteAsync(project, actor, "agent-assigned", key, agent.Id + " / " + agent.Provider + "/" + agent.Model,
            current =>
            {
                var board = Required(current);
                var root = board.Items.SingleOrDefault(i => i.Key == key) ?? throw new KeyNotFoundException("Ticket not found.");
                var scope = new HashSet<string> { root.Key };
                for (var level = 0; level < 3; level++)
                    foreach (var child in board.Items.Where(i => i.ParentKey is not null && scope.Contains(i.ParentKey)).ToArray()) scope.Add(child.Key);
                var eligible = board.Items.Where(i => scope.Contains(i.Key) && i.Kind == WorkItemKind.Task
                    && i.State is WorkItemState.Draft or WorkItemState.Ready or WorkItemState.Blocked).Select(i => i.Key).ToHashSet();
                if (eligible.Count == 0) throw new InvalidOperationException("There are no unstarted tasks in this ticket's scope.");
                return board with { Items = board.Items.Select(i => eligible.Contains(i.Key) ? i with { Agent = agent, Assignee = agent.Id, LastRunError = "" } : i).ToArray() };
            }, ct);
    }

    public Task<WorkBoardSnapshot> ReleaseClaimAsync(string project, string key, string agent, string token, string reason, CancellationToken ct = default) =>
        WriteAsync(project, agent, "agent-run-stopped", key, reason, current => Change(Required(current), key, item =>
        {
            CheckLease(item, agent, token);
            return item with { State = WorkItemState.Ready, LeaseToken = null, LeaseExpiresUtc = null, LastRunError = reason };
        }), ct);

    public Task<WorkBoardSnapshot> SubmitProposalAsync(string project, string key, string agent, string token, string artifact, string provenance, CancellationToken ct = default) =>
        WriteAsync(project, agent, "proposal-submitted", key, provenance, current => Change(Required(current), key, item =>
        {
            CheckLease(item, agent, token);
            if (string.IsNullOrWhiteSpace(artifact) || string.IsNullOrWhiteSpace(provenance)) throw new ArgumentException("A proposal artifact and provenance are required.");
            return item with { State = WorkItemState.Review, ResultRevision = artifact, VerificationEvidence = provenance,
                ResultKind = "proposal", LeaseToken = null, LeaseExpiresUtc = null, LastRunError = "" };
        }), ct);
}
