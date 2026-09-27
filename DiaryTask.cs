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
    public DateTime Date { get; }
    public DateTime? NotifiedAt { get; set; }
    public DateTime? CompletedAt { get; private set; }
    public long? RecurrenceRuleId { get; set; }
    public string Title { get => _title; set { _title = value; Changed(); } }
    public string Note { get => _note; set { _note = value; Changed(); } }
    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (_isDone == value) return;
            _isDone = value;
            CompletedAt = value ? DateTime.Now : null;
            Changed();
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
