namespace Cdiary;

// One instance owned by App; calls are serialized on the UI dispatcher.
// A clock is injected so all duration and recovery behavior can be tested without waiting.
public sealed class FocusService
{
    private readonly FocusStore _store;
    private readonly TimeProvider _clock;
    private long _runTimestamp;
    private DateTimeOffset _runUtc;
    private FocusSegment[] _beforeRun = [];
    private FocusSession? _pendingPause;
    public bool HasUnsavedChanges => _pendingPause is not null;
    public FocusSession? Current { get; private set; }
    public event Action? Changed;
    public event Action<FocusSession>? Finished;
    public FocusService(FocusStore store, TimeProvider? clock = null)
    {
        _store = store; _clock = clock ?? TimeProvider.System;
        Current = store.Read(activeOnly: true).SingleOrDefault();
        if (Current is { State: FocusState.Running } session)
        {
            var recovered = session with { State = FocusState.Paused, PauseReason = "上次运行中断", Revision = session.Revision + 1 };
            _store.Save(recovered, session.Revision); Current = recovered;
        }
    }
    public FocusSession? Snapshot()
    {
        if (_pendingPause is not null) return _pendingPause;
        if (Current is not { State: FocusState.Running } session) return Current;
        var milliseconds = Math.Max(0, (long)_clock.GetElapsedTime(_runTimestamp).TotalMilliseconds);
        if (session.Mode != FocusMode.Stopwatch)
            milliseconds = Math.Min(milliseconds, Math.Max(0, session.PlannedMinutes * 60000L - _beforeRun.Sum(x => x.Milliseconds)));
        return session with { Segments = [.. _beforeRun, new FocusSegment(_runUtc, milliseconds)] };
    }
    public void Start(DiaryTask task, FocusMode mode, int minutes = 25)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (Current is { State: not FocusState.Finished }) throw new InvalidOperationException("请先结束当前专注，再切换任务。");
        if (!_store.CanStartTask(task.Id,mode is FocusMode.ShortBreak or FocusMode.LongBreak)) throw new InvalidOperationException("任务已完成或已删除。");
        if (mode != FocusMode.Stopwatch && (minutes < 5 || minutes > 180 || minutes % 5 != 0)) throw new ArgumentOutOfRangeException(nameof(minutes));
        var session = new FocusSession(Guid.NewGuid().ToString("N"), task.Id, task.Title, mode,
            mode == FocusMode.Stopwatch ? 0 : minutes, FocusState.Running, _clock.GetUtcNow(), null,
            FocusEndReason.None, "", 0, []);
        _store.Save(session, null); Current = session; BeginRun(); Changed?.Invoke();
    }
    private void BeginRun() { _beforeRun = Current!.Segments; _runUtc = _clock.GetUtcNow(); _runTimestamp = _clock.GetTimestamp(); }
    private void Commit(FocusSession next, bool notify = true)
    {
        next = next with { Revision = Current!.Revision + 1 };
        _store.Save(next, Current.Revision); // Publish memory state only after the durable write succeeds.
        Current = next; _pendingPause = null; if (notify) Changed?.Invoke();
    }
    public void Checkpoint()
    {
        if (_pendingPause is { } pending) { Commit(pending); return; }
        if (Current is not { State: FocusState.Running }) return;
        var next = Snapshot()!;
        if (next.Mode != FocusMode.Stopwatch && next.ElapsedMilliseconds >= next.PlannedMinutes * 60000L)
            Finish(FocusEndReason.Elapsed);
        else Commit(next);
    }
    public void Pause(string reason = "手动暂停")
    {
        if (Current is not { State: FocusState.Running }) return;
        var next = Snapshot()!;
        if (next.Mode != FocusMode.Stopwatch && next.ElapsedMilliseconds >= next.PlannedMinutes * 60000L) { Finish(FocusEndReason.Elapsed); return; }
        var paused = next with { State = FocusState.Paused, PauseReason = reason };
        try { Commit(paused); }
        catch
        {
            // Even with a disk failure, lock/sleep must stop counting immediately.
            _pendingPause = paused; Changed?.Invoke(); throw;
        }
    }
    public void Resume()
    {
        if (_pendingPause is not null) Checkpoint();
        if (Current is not { State: FocusState.Paused } session) return;
        if (!_store.CanStartTask(session.TaskId,session.IsBreak)) throw new InvalidOperationException("任务已完成或已删除，请结束这条记录。");
        Commit(session with { State = FocusState.Running, PauseReason = "" }, notify: false); BeginRun(); Changed?.Invoke();
    }
    public void Finish(FocusEndReason reason = FocusEndReason.Manual)
    {
        if (Current is null or { State: FocusState.Finished }) return;
        if (!Enum.IsDefined(reason) || reason == FocusEndReason.None) throw new ArgumentOutOfRangeException(nameof(reason));
        var next = Snapshot()!;
        var elapsed = next.Mode != FocusMode.Stopwatch && next.ElapsedMilliseconds >= next.PlannedMinutes * 60000L;
        if (reason == FocusEndReason.Elapsed && !elapsed) throw new InvalidOperationException("计时尚未结束。");
        if (elapsed) reason = FocusEndReason.Elapsed;
        var end = elapsed && next.Segments.Length > 0 ? next.Segments[^1].StartUtc.AddMilliseconds(next.Segments[^1].Milliseconds) : _clock.GetUtcNow();
        Commit(next with { State = FocusState.Finished, EndReason = reason, EndedUtc = end, PauseReason = "" });
        Finished?.Invoke(Current!);
    }
    public int CompletedRounds => _store.Read().Count(x => x.CompletedPomodoro);
    public FocusMode SuggestedBreak(FocusSettings settings) => CompletedRounds > 0 && CompletedRounds % settings.RoundsBeforeLongBreak == 0 ? FocusMode.LongBreak : FocusMode.ShortBreak;
}
