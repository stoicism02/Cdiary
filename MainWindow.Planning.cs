using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
namespace Cdiary;

public partial class MainWindow
{
    private bool _planningRefreshQueued, _planningClosed;
    public ObservableCollection<DiaryTask> ShelvedTasks { get; } = new();
    public string ShelvedLabel => $"今日已推进 · {ShelvedTasks.Count}";
    public bool HasShelvedTasks => ShelvedTasks.Count > 0;

    private DiaryTask ResolveTask(DiaryTask row)
    {
        var live = _noteWindows.GetValueOrDefault(row.Id)?.Task
            ?? Tasks.FirstOrDefault(t => t.Id == row.Id)
            ?? _dayWindows.Values.SelectMany(w => w.Tasks).FirstOrDefault(t => t.Id == row.Id)
            ?? OverdueTasks.FirstOrDefault(t => t.Id == row.Id) ?? row;
        if (!ReferenceEquals(live, row)) live.SyncPlanning(row);
        return live;
    }
    private void MergeDay(ObservableCollection<DiaryTask> target, DateTime date)
    {
        var rows = _repository.LoadTasks(date).Select(ResolveTask).ToList();
        var ids = rows.Select(t => t.Id).ToHashSet();
        // Individual removes keep window subscriptions balanced and preserve selection.
        foreach (var old in target.Where(t => !ids.Contains(t.Id)).ToArray()) target.Remove(old);
        foreach (var row in rows)
            if (!target.Any(t => t.Id == row.Id)) target.Add(row);
    }
    private void RefreshShelvedTasks()
    {
        ShelvedTasks.Clear();
        if (_selectedDate == DateTime.Today)
            foreach (var task in Tasks.Where(t => !t.IsDone && t.IsShelvedOn(_selectedDate))) ShelvedTasks.Add(task);
        Changed(nameof(ShelvedLabel)); Changed(nameof(HasShelvedTasks));
    }
    private void PlanningChanged()
    {
        if (_planningRefreshQueued || _planningClosed) return;
        _planningRefreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _planningRefreshQueued = false;
            if (_planningClosed) return;
            FlushEdits();
            foreach (var window in _noteWindows.Values)
                if (_repository.FindTask(window.Task.Id) is { } fresh) window.Task.SyncPlanning(fresh);
            foreach (var window in _dayWindows.Values) { MergeDay(window.Tasks, window.Date); window.RefreshPlanning(); }
            var selectedId = SelectedTask?.Id;
            Tasks = _dayWindows.TryGetValue(_selectedDate, out var currentDay) ? currentDay.Tasks
                : new ObservableCollection<DiaryTask>(_repository.LoadTasks(_selectedDate).Select(ResolveTask));
            SelectedTask = Tasks.FirstOrDefault(t => t.Id == selectedId && !t.IsDone && !t.IsShelvedOn(_selectedDate));
            RefreshShelvedTasks(); RefreshOverdueTasks(); _scheduler.Refresh();
            if (CalendarPage.Visibility == Visibility.Visible) RefreshCalendar();
            if (CompletedPage.Visibility == Visibility.Visible) ReloadCompleted();
        }), DispatcherPriority.Background);
    }
    private void PlanTask_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTask is not { } task) return;
        FlushEdits();
        new TaskPlanWindow(_repository, task) { Owner = this }.ShowDialog();
    }
    private void TaskPlanning_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: DiaryTask task } button) return;
        var menu = TaskPlanningMenu.Create(this, task, _repository, () => { FlushEdits(); return Task.CompletedTask; }, _selectedDate);
        menu.PlacementTarget = button; menu.IsOpen = true; e.Handled = true;
    }
    private void ContinueTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: DiaryTask task }) return;
        try { FlushEdits(); _repository.SetDayShelved(task, DateTime.Today, false); }
        catch (Exception error) { System.Windows.MessageBox.Show(this, error.Message, "小岛日记"); }
    }
}
