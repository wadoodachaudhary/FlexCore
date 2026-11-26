using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Fx.ControlKit.WorkTracking;

public sealed record WorkStepRevision(string ProjectKey, string Step, int Revision, string Content, string Operation,
    string Actor, string Provenance, DateTimeOffset SavedUtc, string BasedOn = "");

public sealed partial class LocalWorkBoardStore
{
    public async Task<IReadOnlyList<WorkStepRevision>> StepHistoryAsync(string? project = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT payload FROM work_step_revisions WHERE ($key IS NULL OR project_key=$key) ORDER BY project_key,step,revision";
            cmd.Parameters.AddWithValue("$key", (object?)project ?? DBNull.Value);
            using var rows = cmd.ExecuteReader(); var results = new List<WorkStepRevision>();
            while (rows.Read()) results.Add(JsonSerializer.Deserialize<WorkStepRevision>(rows.GetString(0), Json)!);
            return results;
        }
        finally { _gate.Release(); }
    }

    public async Task<WorkStepRevision> SaveStepAsync(WorkStepRevision revision, int expectedRevision, CancellationToken ct = default)
    {
        if (!Regex.IsMatch(revision.ProjectKey, "^[A-Z][A-Z0-9]{1,11}$") || !Regex.IsMatch(revision.Step, "^[A-Za-z0-9-]{1,80}$")
            || string.IsNullOrWhiteSpace(revision.Content) || revision.Content.Length > 4_000_000 || string.IsNullOrWhiteSpace(revision.Actor))
            throw new ArgumentException("A step needs a valid project/step key, author and bounded nonempty content.");
        await _gate.WaitAsync(ct);
        try
        {
            using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
            var saved = SaveStep(db, tx, revision, expectedRevision); tx.Commit();
            ExportStep(saved); return saved;
        }
        finally { _gate.Release(); }
    }

    private WorkStepRevision SaveStep(SqliteConnection db, SqliteTransaction tx, WorkStepRevision revision, int? expectedRevision = null)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT payload FROM work_step_revisions WHERE project_key=$key AND step=$step ORDER BY revision DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$key", revision.ProjectKey); cmd.Parameters.AddWithValue("$step", revision.Step);
        var latest = cmd.ExecuteScalar() as string;
        var previous = latest is null ? null : JsonSerializer.Deserialize<WorkStepRevision>(latest, Json);
        if (expectedRevision is not null && (previous?.Revision ?? 0) != expectedRevision)
            throw new InvalidOperationException("This step changed in another session. Reload its saved revision before saving or reviewing it.");
        if (previous?.Content == revision.Content && previous.Operation == revision.Operation && previous.Provenance == revision.Provenance) return previous;
        var saved = revision with { Revision = (previous?.Revision ?? 0) + 1, SavedUtc = _time.GetUtcNow() };
        cmd.CommandText = "INSERT INTO work_step_revisions(project_key,step,revision,payload) VALUES($key,$step,$revision,$json)";
        cmd.Parameters.AddWithValue("$revision", saved.Revision); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(saved, Json)); cmd.ExecuteNonQuery();
        return saved;
    }
    private void ExportStep(WorkStepRevision revision)
    {
        var folder = Path.Combine(RootDirectory, "history", revision.ProjectKey, revision.Step); Directory.CreateDirectory(folder);
        Atomic(Path.Combine(folder, revision.Revision.ToString("D6") + ".json"), JsonSerializer.Serialize(revision, Json));
    }
    public async Task ExportStepsAsync(string project, CancellationToken ct = default)
    { foreach (var revision in await StepHistoryAsync(project, ct)) ExportStep(revision); }
}
