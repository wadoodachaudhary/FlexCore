using System.Globalization;

namespace Fx.ControlKit.Reports;

public sealed partial class ReportLayoutSession
{
    // Crystal report ordering treats punctuation as a sort character before
    // numbers/letters (e.g. Test_A before Test1, Mon_Source before MOND).
    // Use explicit invariant linguistic string sorting, independent of the web
    // user's culture. Formula comparisons, group identity and filters keep their
    // existing semantics. An ordinal tie-break keeps linguistically equivalent
    // but distinct group keys contiguous instead of merging/interleaving them.
    // Crystal pushes the record sort to the database, whose word-sort collation
    // ignores hyphens and apostrophes unless the strings are otherwise equal
    // ("PO-1234" sorts as "PO1234", before "PO-13"), so the same keys are
    // compared without them first.
    private static int CompareSortValues(object? left, object? right)
    {
        if (left is not string a || right is not string b) return CrystalFormula.Compare(left, right);
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions options = CompareOptions.StringSort | CompareOptions.IgnoreCase;
        var result = compare.Compare(WordSortKey(a), WordSortKey(b), options);
        if (result == 0) result = compare.Compare(a, b, options);
        return result != 0 ? result : StringComparer.OrdinalIgnoreCase.Compare(a, b);
    }

    private static string WordSortKey(string value)
        => value.IndexOfAny(WordSortIgnored) < 0 ? value : string.Concat(value.Where(c => Array.IndexOf(WordSortIgnored, c) < 0));

    private static readonly char[] WordSortIgnored = ['-', '\''];
}
