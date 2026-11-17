using System.Text.RegularExpressions;

namespace Fx.ControlKit.Migration;

/// <summary>String-level rewrites that turn EF6 / System.Web fragments into code a .NET 10 project can compile.</summary>
internal static class EfCoreRewriter
{
    private static readonly string[] DropUsings =
    [
        "System.Web", "System.Web.UI", "System.Web.UI.WebControls", "System.Web.Hosting", "System.Web.Routing",
        "System.Web.Optimization", "System.Data.Entity", "System.Data.Entity.ModelConfiguration",
        "System.Data.Entity.Infrastructure", "System.Data.SqlClient", "System.Configuration",
        "log4net", "Autofac", "Autofac.Integration.Web"
    ];

    public static string StripUsings(string source)
    {
        foreach (var name in DropUsings)
            source = Regex.Replace(source, @"^[ \t]*using\s+" + Regex.Escape(name) + @"\s*;\s*\r?\n", "", RegexOptions.Multiline);
        return source;
    }

    public static bool StillLegacy(string source) =>
        source.Contains("System.Web", StringComparison.Ordinal)
        || source.Contains("System.Data.Entity", StringComparison.Ordinal)
        || source.Contains("Autofac", StringComparison.Ordinal)
        || source.Contains("log4net", StringComparison.Ordinal)
        || source.Contains("HostingEnvironment", StringComparison.Ordinal)
        || source.Contains("ConfigurationManager", StringComparison.Ordinal)
        || source.Contains("HttpContext", StringComparison.Ordinal)
        || source.Contains("CreateDatabaseIfNotExists", StringComparison.Ordinal)
        || source.Contains("ContainerBuilder", StringComparison.Ordinal)
        || source.Contains("ExecuteSqlCommand", StringComparison.Ordinal)
        || source.Contains("LogManager", StringComparison.Ordinal)
        || source.Contains("ILog", StringComparison.Ordinal)
        || source.Contains(": Module", StringComparison.Ordinal);

    public static string RewriteDbContext(string source, List<MigrationTodo> todos, string relative)
    {
        source = StripUsings(source);
        if (!source.Contains("using Microsoft.EntityFrameworkCore;", StringComparison.Ordinal))
            source = "using Microsoft.EntityFrameworkCore;\n" + source;
        if (!source.Contains("using Microsoft.EntityFrameworkCore.Metadata.Builders;", StringComparison.Ordinal))
            source = "using Microsoft.EntityFrameworkCore.Metadata.Builders;\n" + source;
        source = Regex.Replace(source, @"public\s+(\w+)\s*\(\s*\)\s*:\s*base\(\s*""name=[^""]+""\s*\)",
            "public $1(DbContextOptions<$1> options) : base(options)");
        source = source.Replace("DbModelBuilder", "ModelBuilder", StringComparison.Ordinal)
            .Replace("EntityTypeConfiguration<", "EntityTypeBuilder<", StringComparison.Ordinal);
        source = Regex.Replace(source, @"\.HasDatabaseGeneratedOption\(\s*DatabaseGeneratedOption\.None\s*\)", ".ValueGeneratedNever()");
        source = Regex.Replace(source,
            @"\.HasRequired<[^>]+>\(([^)]+)\)\s*\.WithMany\(\)\s*\.HasForeignKey\(([^)]+)\)",
            ".HasOne($1).WithMany().HasForeignKey($2).IsRequired()",
            RegexOptions.Singleline);
        if (source.Contains("Database.SqlQuery", StringComparison.Ordinal) || source.Contains("ExecuteSqlCommand", StringComparison.Ordinal))
        {
            todos.Add(new MigrationTodo(relative, "SQL Server sequence or script calls in the DbContext were not rewritten. SQLite seeding replaces them."));
        }
        todos.Add(new MigrationTodo(relative, "EF6 DbContext was rewritten to EF Core. The SQL Server / LocalDB connection is not used."));
        return source;
    }

    public static string RewriteHiLo(string source, string contextType, string dbSet, List<MigrationTodo> todos, string relative)
    {
        source = StripUsings(source);
        var signature = Regex.Match(source, @"public\s+int\s+GetNextSequenceValue\s*\([^)]*\)\s*\{");
        if (!signature.Success) return source;
        var open = signature.Index + signature.Length - 1;
        var close = MatchBrace(source, open);
        if (close < 0) return source;
        var set = string.IsNullOrWhiteSpace(dbSet) ? "Set<object>()" : dbSet;
        var body = "{\n        // TODO: SQL Server NEXT VALUE FOR sequences are not available on SQLite.\n        lock (sequenceLock)\n        {\n            var max = db." + set + ".Select(item => (int?)item.Id).Max() ?? 0;\n            return max + 1;\n        }\n    }";
        todos.Add(new MigrationTodo(relative, "HiLo SQL Server sequences were replaced with MAX(Id)+1 for SQLite."));
        return source[..signature.Index] + "public int GetNextSequenceValue(" + contextType + " db)\n    " + body + source[(close + 1)..];
    }

    public static string RewriteService(string source, string contextType, List<MigrationTodo> todos, string relative)
    {
        source = StripUsings(source);
        if (!source.Contains("using Microsoft.EntityFrameworkCore;", StringComparison.Ordinal))
            source = "using Microsoft.EntityFrameworkCore;\n" + source;
        var field = Regex.Match(source, @"private\s+(?:readonly\s+)?" + Regex.Escape(contextType) + @"\s+(?<name>\w+)\s*;");
        if (!field.Success)
        {
            todos.Add(new MigrationTodo(relative, "The data service uses " + contextType + " but its field was not recognized. It was copied with EF Core usings only."));
            return source;
        }
        var name = field.Groups["name"].Value;
        source = source.Remove(field.Index, field.Length)
            .Insert(field.Index, "private readonly IDbContextFactory<" + contextType + "> dbFactory;");
        source = Regex.Replace(source, Regex.Escape(contextType) + @"\s+" + Regex.Escape(name) + @"\b",
            "IDbContextFactory<" + contextType + "> dbFactory");
        source = source.Replace("this." + name + " = " + name + ";", "this.dbFactory = dbFactory;", StringComparison.Ordinal);
        source = source.Replace("this." + name + " = " + name, "this.dbFactory = dbFactory", StringComparison.Ordinal);
        source = Regex.Replace(source, @"\b" + Regex.Escape(name) + @"\.Entry\((?<arg>\w+)\)\.State\s*=\s*EntityState\.Modified\s*;",
            "db.Update(${arg});");
        source = InsertContext(source, name);
        todos.Add(new MigrationTodo(relative, "The EF service now opens a DbContext per call. EntityState.Modified was changed to DbContext.Update."));
        return source;
    }

    private static string InsertContext(string source, string oldField)
    {
        var regex = new Regex(@"\b(?<name>\w+)\s*\((?<args>[^)]*)\)\s*\{", RegexOptions.Compiled);
        var matches = regex.Matches(source).Cast<Match>().ToList();
        for (var index = matches.Count - 1; index >= 0; index--)
        {
            var match = matches[index];
            var name = match.Groups["name"].Value;
            if (name is "if" or "for" or "foreach" or "while" or "lock" or "using" or "switch" or "catch" or "get" or "set") continue;
            var open = match.Index + match.Length - 1;
            if (open < 0 || open >= source.Length || source[open] != '{') continue;
            var close = MatchBrace(source, open);
            if (close < 0) continue;
            var body = source[(open + 1)..close];
            if (name.Equals("Dispose", StringComparison.Ordinal))
            {
                source = source[..(open + 1)] + "\n    " + source[close..];
                continue;
            }
            if (!Regex.IsMatch(body, @"\b" + Regex.Escape(oldField) + @"\.")) continue;
            var injection = "\n        using var " + oldField + " = dbFactory.CreateDbContext();";
            source = source.Insert(open + 1, injection);
        }
        return source;
    }

    private static int MatchBrace(string text, int open)
    {
        var depth = 0;
        for (var index = open; index < text.Length; index++)
        {
            if (text[index] == '{') depth++;
            else if (text[index] == '}')
            {
                depth--;
                if (depth == 0) return index;
            }
        }
        return -1;
    }
}
