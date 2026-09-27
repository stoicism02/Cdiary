using System.Globalization;

namespace Cdiary;

public sealed class CompletedDayGroup(DateTime date, IReadOnlyList<DiaryTask> tasks)
{
    public DateTime Date { get; } = date.Date;
    public IReadOnlyList<DiaryTask> Tasks { get; } = tasks;
    public string DateLabel => Date == DateTime.Today ? "今天"
        : Date == DateTime.Today.AddDays(-1) ? "昨天"
        : Date.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
    public string CountLabel => $"{Tasks.Count} 项";
}
