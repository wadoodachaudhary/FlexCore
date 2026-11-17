using System.Globalization;

namespace Fx.ControlKit.Grid;

/// <summary>A named group with a separate identity and explicit order; raw measure values are not changed.</summary>
public sealed record PivotGroupValue(object Identity, string Label, int Order, object? SortValue = null) : IComparable
{
    public int CompareTo(object? obj)
    {
        if (obj is not PivotGroupValue other) throw new ArgumentException("Expected a pivot group value.", nameof(obj));
        var order = Order.CompareTo(other.Order);
        if (order != 0) return order;
        if (SortValue is IComparable comparable && SortValue.GetType() == other.SortValue?.GetType())
            return comparable.CompareTo(other.SortValue);
        return StringComparer.CurrentCulture.Compare(Convert.ToString(SortValue, CultureInfo.CurrentCulture), Convert.ToString(other.SortValue, CultureInfo.CurrentCulture));
    }
    public override string ToString() => Label;
}
