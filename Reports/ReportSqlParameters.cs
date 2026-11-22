using System.Text.RegularExpressions;

namespace Fx.ControlKit.Reports;

/// <summary>
/// The parameters one report query sends: only those its SQL reads. A subreport inherits every value of its report, so without
/// this each per-record query would declare and carry the whole of a long prompt list that only the main query filters on.
/// <see cref="Request"/> keeps the whole request for an executor that authorizes a query from values the SQL need not read.
/// </summary>
public sealed class ReportSqlParameters : Dictionary<string, object>
{
    // Named markers of the common providers: @name, :name, $name.
    private static readonly Regex Marker = new(@"[@:$]([\p{L}\p{N}_#$]+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private ReportSqlParameters(IDictionary<string, object> request) : base(StringComparer.OrdinalIgnoreCase) => Request = request;

    /// <summary>Every value of the request, including those the SQL does not read.</summary>
    public IDictionary<string, object> Request { get; }

    /// <summary>
    /// What to send for <paramref name="sql"/>. A name that appears anywhere in the text is kept, even in a comment or a string,
    /// since an extra value is harmless and a missing one is an error. Text with no white space may be a procedure name, whose
    /// parameters it cannot show, so it keeps them all; so does a request whose names the SQL all reads.
    /// </summary>
    public static IDictionary<string, object>? For(string? sql, IDictionary<string, object>? parameters)
    {
        if (parameters is null || parameters.Count == 0 || string.IsNullOrEmpty(sql) || !sql.Any(char.IsWhiteSpace)) return parameters;
        var read = Names(sql);
        var sent = new ReportSqlParameters(parameters);
        foreach (var (key, value) in parameters)
            if (read.Contains(key.TrimStart('@'))) sent.Add(key, value);
        return sent.Count == parameters.Count ? parameters : sent;
    }

    /// <summary>The parameter names <paramref name="sql"/> shows.</summary>
    internal static HashSet<string> Names(string sql)
    {
        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Marker.Matches(sql)) read.Add(match.Groups[1].Value);
        return read;
    }

    /// <summary>The whole request behind <paramref name="parameters"/>, for a check that authorizes a query.</summary>
    public static IEnumerable<KeyValuePair<string, object>>? RequestOf(IEnumerable<KeyValuePair<string, object>>? parameters) =>
        parameters is ReportSqlParameters sent ? sent.Request : parameters;
}
