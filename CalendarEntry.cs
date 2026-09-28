namespace Cdiary;

public sealed record CalendarEntry(DateTime Date, string Title, long? TaskId, long? RuleId)
{
    public bool IsProjected => TaskId is null;
    public string DisplayTitle => IsProjected ? $"↻ {Title}" : Title;
    public string Hint => IsProjected ? "重复任务预览 · 点击打开这一天" : "点击查看任务";
}

public sealed class CalendarDay(DateTime date, DateTime month, DateTime selected, IReadOnlyList<CalendarEntry> entries, int visibleLimit)
{
    public DateTime Date { get; } = date.Date;
    public string DayNumber => Date.Day.ToString();
    public bool IsCurrentMonth => Date.Month == month.Month && Date.Year == month.Year;
    public bool IsToday => Date == DateTime.Today;
    public bool IsSelected => Date == selected.Date;
    public IReadOnlyList<CalendarEntry> VisibleEntries { get; } = entries.Take(visibleLimit).ToList();
    public string OverflowLabel => entries.Count > visibleLimit ? $"还有 {entries.Count - visibleLimit} 项" : "";
    public bool HasOverflow => entries.Count > visibleLimit;
}
