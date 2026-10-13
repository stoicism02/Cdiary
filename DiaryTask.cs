using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Cdiary;

public sealed class DiaryTask : INotifyPropertyChanged
{
    private string _title;
    private string _note;
    private bool _isDone;
    private TimeSpan? _reminderTime;

    public DiaryTask(long id, DateTime date, string title, string note, bool isDone,
        TimeSpan? reminderTime, DateTime? notifiedAt, long? recurrenceRuleId = null,
        DateTime? completedAt = null)
    {
        Id = id;
        Date = date.Date;
        _title = title;
        _note = note;
        _isDone = isDone;
        CompletedAt = completedAt;
        _reminderTime = reminderTime;
        NotifiedAt = notifiedAt;
        RecurrenceRuleId = recurrenceRuleId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public long Id { get; }
    public DateTime Date { get; private set; }
    public DateTime? Deadline { get; private set; }
    public bool CarryForward { get; private set; }
    public bool IsOverdue => !IsDone && Deadline?.Date < DateTime.Today;
    private HashSet<DateTime> _shelvedDays = new();
    public bool IsShelvedOn(DateTime date) => _shelvedDays.Contains(date.Date);
    public void SyncPlanning(DiaryTask source) => ApplyPlanning(source.Date, source.Deadline, source.CarryForward, source._shelvedDays);
    public string PlanLabel => string.Join(" · ", new[] {
        CarryForward ? "持续推进" : "",
        Deadline is { } due ? !IsDone && due.Date < DateTime.Today ? $"逾期 {(DateTime.Today-due.Date).Days} 天" : $"{due:M月d日} 截止" : ""
    }.Where(s => s.Length > 0));
    public void ApplyPlanning(DateTime date, DateTime? deadline, bool carryForward, IEnumerable<DateTime>? shelvedDays = null)
    {
        Date = date.Date; Deadline = deadline?.Date; CarryForward = carryForward;
        if (shelvedDays is not null) _shelvedDays = shelvedDays.Select(d => d.Date).ToHashSet();
        Changed(nameof(Date)); Changed(nameof(Deadline)); Changed(nameof(CarryForward)); Changed(nameof(PlanLabel)); Changed(nameof(IsOverdue)); Changed(nameof(IsShelvedOn));
    }
    public DateTime? NotifiedAt { get; set; }
    public DateTime? CompletedAt { get; private set; }
    public string CompletionLabel => IsDone ? CompletedAt is { } at ? $"✓ 已完成 · {at:yyyy年M月d日 HH:mm}" : "✓ 已完成 · 完成时间未记录" : "";
    public long? RecurrenceRuleId { get; set; }
    public string Title { get => _title; set { _title = value; Changed(); } }
    public long NoteRevision { get; private set; }
    public string Note { get => _note; set { if (_note == value) return; _note = value; NoteRevision++; Changed(); } }
    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (_isDone == value) return;
            _isDone = value;
            CompletedAt = value ? DateTime.Now : null;
            Changed();
            Changed(nameof(CompletionLabel));
            Changed(nameof(PlanLabel));
            Changed(nameof(IsOverdue));
        }
    }
    public TimeSpan? ReminderTime
    {
        get => _reminderTime;
        set { _reminderTime = value; NotifiedAt = null; Changed(); Changed(nameof(ReminderLabel)); }
    }
    public DateTime? ReminderAt => ReminderTime is { } time ? Date.Add(time) : null;
    public string ReminderLabel => ReminderTime?.ToString(@"hh\:mm") ?? "";

    private void Changed([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DayOption(DateTime date, string weekday, string hint)
{
    public DateTime Date { get; } = date;
    public string DayNumber => Date.Day.ToString("00");
    public string Weekday { get; } = weekday;
    public string Hint { get; } = hint;
}
