namespace Cdiary;

public sealed record CalendarEntry(DateTime Date, string Title, long? TaskId, long? RuleId, bool IsDone = false, DateTime? CompletedAt = null,
    bool Progressed = false, bool WasOverdue = false, long WorkedMilliseconds = 0)
{
    public bool IsProjected => TaskId is null;
    public string State => IsDone ? "Done" : WasOverdue ? "Overdue" : Progressed ? "Progress" : IsProjected || Date.Date > DateTime.Today ? "Future" : Date.Date < DateTime.Today ? "Planned" : "Today";
    public string DisplayTitle => $"{(IsDone ? "✓" : IsProjected ? "↻" : State == "Overdue" ? "!" : Progressed ? "◷" : "○")} {Title}";
    public string StatusLabel => IsDone ? CompletedAt is { } at ? $"已完成 · {at:yyyy年M月d日 HH:mm}" : "已完成 · 完成时间未记录"
        : IsProjected ? "重复任务预览" : State == "Overdue" ? "当日已逾期" : Progressed ? "当日已推进" : Date.Date < DateTime.Today ? "当日安排" : State == "Today" ? "今日待办" : "未来待办";
    public string Hint => $"{Title}\n{StatusLabel}\n日期：{Date:yyyy年M月d日}" + (WorkedMilliseconds > 0 ? $"\n当日投入：{FocusPage.Duration(WorkedMilliseconds)}" : "");
}

public sealed class CalendarDay(DateTime date, DateTime month, DateTime selected, IReadOnlyList<CalendarEntry> entries, int visibleLimit, int filter = 0)
{
    public DateTime Date { get; } = date.Date;
    public string DayNumber => Date.Day.ToString();
    public bool IsCurrentMonth => Date.Month == month.Month && Date.Year == month.Year;
    public bool IsToday => Date == DateTime.Today;
    public bool IsSelected => Date == selected.Date;
    public string Summary => entries.Count == 0 ? "" : $"{entries.Count(entry => entry.IsDone)}/{entries.Count}";
    public string SummaryHint => $"完成 {entries.Count(entry => entry.IsDone)} / {entries.Count} 项（含重复计划）";
    public IReadOnlyList<CalendarEntry> Entries { get; } = entries.Where(entry => filter == 0 || (filter == 2 ? entry.IsDone : !entry.IsDone)).ToList();
    public IReadOnlyList<CalendarEntry> VisibleEntries => Entries.Take(Entries.Count > visibleLimit ? Math.Max(0, visibleLimit - 1) : visibleLimit).ToList();
    public string OverflowLabel => $"还有 {Entries.Count - VisibleEntries.Count} 项";
    public bool HasOverflow => Entries.Count > VisibleEntries.Count;
}
