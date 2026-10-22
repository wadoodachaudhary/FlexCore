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
    private static int CompareSortValues(object? left, object? right)
    {
        if (left is not string a || right is not string b) return CrystalFormula.Compare(left, right);
        var result = CultureInfo.InvariantCulture.CompareInfo.Compare(a, b, CompareOptions.StringSort | CompareOptions.IgnoreCase);
        return result != 0 ? result : StringComparer.OrdinalIgnoreCase.Compare(a, b);
    }
}
