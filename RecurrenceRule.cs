namespace Cdiary;

public sealed record RecurrenceRule(long Id, DateTime StartDate, DateTime? EndDate,
    bool IsWeekly, int WeekdaysMask, string Title, TimeSpan? ReminderTime)
{
    public bool Includes(DateTime date)
    {
        date = date.Date;
        return date >= StartDate.Date && (EndDate is null || date <= EndDate.Value.Date)
            && (!IsWeekly || (WeekdaysMask & (1 << (int)date.DayOfWeek)) != 0);
    }
}
