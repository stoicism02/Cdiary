using System.Windows.Threading;
using Microsoft.Win32;

namespace Cdiary;

public sealed class ReminderScheduler : IDisposable
{
    private static readonly TimeSpan MaximumWait = TimeSpan.FromMinutes(30);
    private readonly DiaryRepository _repository;
    private readonly Action<IReadOnlyList<DiaryTask>> _notify;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private bool _started;

    public ReminderScheduler(DiaryRepository repository, Dispatcher dispatcher,
        Action<IReadOnlyList<DiaryTask>> notify)
    {
        _repository = repository;
        _dispatcher = dispatcher;
        _notify = notify;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += TimerTick;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        SystemEvents.PowerModeChanged += PowerModeChanged;
        SystemEvents.TimeChanged += TimeChanged;
        Refresh();
    }

    public void Refresh()
    {
        if (!_started) return;
        _timer.Stop();
        var now = DateTime.Now;
        _repository.EnsureOccurrences(now.Date);
        var due = _repository.DueReminders(now);
        if (due.Count > 0)
        {
            _notify(due);
            _repository.MarkNotified(due.Select(task => task.Id), now);
        }
        var next = _repository.NextReminder(DateTime.Now);
        var wait = next is null ? MaximumWait : next.Value - DateTime.Now;
        var untilTomorrow = DateTime.Today.AddDays(1) - DateTime.Now;
        if (untilTomorrow < wait) wait = untilTomorrow;
        if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
        if (wait > MaximumWait) wait = MaximumWait;
        _timer.Interval = wait;
        _timer.Start();
    }

    private void TimerTick(object? sender, EventArgs e) => Refresh();

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _dispatcher.BeginInvoke(Refresh);
    }

    private void TimeChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(Refresh);

    public void Dispose()
    {
        _started = false;
        _timer.Stop();
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        SystemEvents.TimeChanged -= TimeChanged;
    }
}
