namespace Cdiary;

public enum FocusMode { Pomodoro, Stopwatch, ShortBreak, LongBreak }
public enum FocusState { Running, Paused, Finished }
public enum FocusEndReason { None, Elapsed, Manual, TaskCompleted, TaskDeleted, Switched }
public sealed record FocusSegment(DateTimeOffset StartUtc, long Milliseconds);
public sealed record FocusSession(string Id, long TaskId, string TaskTitle, FocusMode Mode,
    int PlannedMinutes, FocusState State, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc,
    FocusEndReason EndReason, string PauseReason, int Revision, FocusSegment[] Segments)
{
    public bool IsBreak => Mode is FocusMode.ShortBreak or FocusMode.LongBreak;
    public long ElapsedMilliseconds => Segments.Sum(x => x.Milliseconds);
    public bool CompletedPomodoro => Mode == FocusMode.Pomodoro && EndReason == FocusEndReason.Elapsed;
}

public sealed record FocusSettings(int WorkMinutes = 25, int ShortBreakMinutes = 5,
    int LongBreakMinutes = 15, int RoundsBeforeLongBreak = 4, bool SoundEnabled = true, bool UseStopwatch = false)
{
    public void Validate()
    {
        foreach (var minutes in new[] { WorkMinutes, ShortBreakMinutes, LongBreakMinutes })
            if (minutes < 5 || minutes > 180 || minutes % 5 != 0)
                throw new ArgumentOutOfRangeException(nameof(minutes), "时长须为 5～180 分钟，间隔 5 分钟。");
        if (RoundsBeforeLongBreak is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(RoundsBeforeLongBreak));
    }
}

public static class FocusStatistics
{
    // Split actual recorded intervals at local midnight; never count breaks or offline gaps.
    public static IReadOnlyDictionary<DateOnly, TimeSpan> ByDay(IEnumerable<FocusSession> sessions, TimeZoneInfo zone)
    {
        var totals = new Dictionary<DateOnly, TimeSpan>();
        foreach (var session in sessions.Where(x => !x.IsBreak))
        foreach (var segment in session.Segments)
        {
            var cursor = segment.StartUtc;
            var end = cursor.AddMilliseconds(segment.Milliseconds);
            while (cursor < end)
            {
                var local = TimeZoneInfo.ConvertTime(cursor, zone);
                var day = DateOnly.FromDateTime(local.DateTime);
                var midnight = DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified);
                while (zone.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(1);
                var boundary = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone));
                var until = boundary < end ? boundary : end;
                if (until <= cursor) throw new InvalidOperationException("无法确定本地日期边界。");
                totals[day] = totals.GetValueOrDefault(day) + (until - cursor);
                cursor = until;
            }
        }
        return totals;
    }
}
