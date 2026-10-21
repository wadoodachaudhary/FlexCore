using System.Security.Cryptography;
using Fx.ControlKit.Reports;
using Microsoft.Data.Sqlite;

internal static class CrystalSampleChecks
{
    public const string ExpectedSha256 = "2676BBF45861FFB532A4E76BD71A8D4057699831E86805B75D8B72CF66DB7A0F";

    public static void Run(Action<bool, string> check)
    {
        var path = CrystalSampleDatabase.Path;
        check(File.Exists(path), "crystal sample database ships beside the application");
        check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == ExpectedSha256,
            "crystal sample database matches the refreshed pack");

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        using (var connection = new SqliteConnection(builder.ToString()))
        {
            connection.Open();
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM ReportCatalog";
            check(Convert.ToInt64(count.ExecuteScalar()) == 530,
                "crystal sample database opens read-only with the 530-report catalog");

            var rejected = false;
            try
            {
                using var write = connection.CreateCommand();
                write.CommandText = "CREATE TABLE FlexCoreWriteProbe (Id INTEGER)";
                write.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                rejected = true;
            }
            check(rejected, "crystal sample database rejects writes");
        }

        check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == ExpectedSha256,
            "reading the crystal sample database leaves the file unchanged");
    }
}
