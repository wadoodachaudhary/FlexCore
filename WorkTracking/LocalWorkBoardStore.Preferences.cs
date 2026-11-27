namespace Fx.ControlKit.WorkTracking;

public sealed partial class LocalWorkBoardStore
{
    /// <summary>Preference name for the project key a host should reopen.</summary>
    public const string LastOpenedProjectPreference = "last-opened-project";

    public async Task<string?> GetPreferenceAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
            throw new ArgumentException("A preference name is required.");
        await _gate.WaitAsync(ct);
        try
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT value FROM work_preferences WHERE name=$name";
            cmd.Parameters.AddWithValue("$name", name.Trim());
            return cmd.ExecuteScalar() as string;
        }
        finally { _gate.Release(); }
    }

    public async Task SetPreferenceAsync(string name, string? value, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
            throw new ArgumentException("A preference name is required.");
        await _gate.WaitAsync(ct);
        try
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            if (string.IsNullOrWhiteSpace(value))
            {
                cmd.CommandText = "DELETE FROM work_preferences WHERE name=$name";
                cmd.Parameters.AddWithValue("$name", name.Trim());
            }
            else
            {
                cmd.CommandText = "INSERT INTO work_preferences(name,value) VALUES($name,$value) ON CONFLICT(name) DO UPDATE SET value=$value";
                cmd.Parameters.AddWithValue("$name", name.Trim());
                cmd.Parameters.AddWithValue("$value", value.Trim());
            }
            cmd.ExecuteNonQuery();
        }
        finally { _gate.Release(); }
    }
}
