using System.Globalization;
namespace Fx.ControlKit;

public enum CalendarSelectionMode { Single, Multiple, Range }
public enum CalendarView { Month, Year, Decade }
public sealed record CalendarRange(DateTime? Start, DateTime? End);
public sealed record CalendarCellContext(DateTime Date, bool Selected, bool Disabled, bool InCurrentMonth);

internal static class CalendarDateMath
{
    internal static IReadOnlyList<DateTime?> DaysForMonth(DateTime month, DayOfWeek firstDay)
    {
        month = new DateTime(month.Year, month.Month, 1);
        var offset = ((int)month.DayOfWeek - (int)firstDay + 7) % 7;
        var count = Math.Clamp(((offset + DateTime.DaysInMonth(month.Year, month.Month) + 6) / 7) * 7, 28, 42);
        return Enumerable.Range(0, count).Select(i =>
        {
            var ticks = month.Ticks + (long)(i - offset) * TimeSpan.TicksPerDay;
            return ticks < 0 || ticks > DateTime.MaxValue.Ticks ? (DateTime?)null : new DateTime(ticks);
        }).ToArray();
    }
    internal static DateTime AddDays(DateTime date, int days) => new(Math.Clamp(date.Date.Ticks + (long)days * TimeSpan.TicksPerDay, 0, DateTime.MaxValue.Date.Ticks));
    internal static DateTime AddMonths(DateTime date, int months)
    {
        var index = Math.Clamp((date.Year - 1) * 12 + date.Month - 1 + months, 0, 119987);
        return new DateTime(index / 12 + 1, index % 12 + 1, Math.Min(date.Day, DateTime.DaysInMonth(index / 12 + 1, index % 12 + 1)));
    }
}
