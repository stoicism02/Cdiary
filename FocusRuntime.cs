using Microsoft.Win32;
using System.Windows.Threading;

namespace Cdiary;

// The application will own this adapter when the approved UI is connected.
// No rendering timer is needed here: wake at the deadline or every 30 seconds to checkpoint.
public sealed class FocusRuntime : IDisposable
{
    private readonly FocusService _service;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private bool _disposed;
    public event Action<Exception>? SaveFailed;
    public FocusRuntime(FocusService service, Dispatcher dispatcher)
    {
        _service = service; _dispatcher = dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += Tick;
        service.Changed += Schedule;
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.SessionSwitch += SessionChanged;
        Schedule();
    }
    private void Schedule()
    {
        _timer.Stop();
        if (_disposed || _service.Snapshot() is not { State: FocusState.Running } session) return;
        var remaining = session.Mode == FocusMode.Stopwatch ? 30000 : session.PlannedMinutes * 60000L - session.ElapsedMilliseconds;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(remaining, 50, 30000));
        _timer.Start();
    }
    private void Tick(object? sender, EventArgs e)
    {
        try { _service.Checkpoint(); Schedule(); }
        catch (Exception error)
        {
            _timer.Stop(); _timer.Interval = TimeSpan.FromSeconds(30); _timer.Start();
            SaveFailed?.Invoke(error);
        }
    }
    private void Pause(string reason)
    {
        if (_disposed) return;
        try { _service.Pause(reason); }
        catch (Exception error)
        {
            _timer.Stop(); _timer.Interval = TimeSpan.FromSeconds(30); _timer.Start();
            SaveFailed?.Invoke(error);
        }
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _dispatcher.Invoke(() => Pause("电脑进入睡眠"));
    }
    private void SessionChanged(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            _dispatcher.Invoke(() => Pause("屏幕锁定或会话断开"));
    }
    public void Dispose()
    {
        if (_disposed) return;
        // Let the caller keep the application open if the final durable save fails.
        _service.Pause("程序退出");
        _disposed = true; _timer.Stop(); _service.Changed -= Schedule;
        SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.SessionSwitch -= SessionChanged;
    }
}
