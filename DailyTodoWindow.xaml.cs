using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Data;
using WpfCheckBox = System.Windows.Controls.CheckBox;

namespace Cdiary;

public partial class DailyTodoWindow : Window, INotifyPropertyChanged
{
    private readonly Action<DiaryTask> _openTaskNote;
    private readonly Action<DiaryTask> _saveCompletion;
    private readonly DiaryRepository? _repository;
    private readonly HashSet<DiaryTask> _observed = new();

    public DailyTodoWindow(DateTime date, ObservableCollection<DiaryTask> tasks,
        Action<DiaryTask> openTaskNote, Action<DiaryTask> saveCompletion, DiaryRepository? repository = null)
    {
        _repository = repository;
        Date = date.Date;
        Tasks = tasks;
        _openTaskNote = openTaskNote;
        _saveCompletion = saveCompletion;
        InitializeComponent();
        ActiveTasks = new ListCollectionView(Tasks);
        ActiveTasks.Filter = item => item is DiaryTask { IsDone: false } task && !task.IsShelvedOn(Date);
        DataContext = this;
        Tasks.CollectionChanged += TasksChanged;
        ObserveTasks();
        RefreshPlanning();
        Title = $"{DayLabel}待办 · 小岛日记";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public DateTime Date { get; }
    public ObservableCollection<DiaryTask> Tasks { get; }
    public ICollectionView ActiveTasks { get; }
    public string DayLabel => Date.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
    public string ProgressLabel => $"已完成 {Tasks.Count(task => task.IsDone)} / {Tasks.Count}";
    public bool HasTasks => Tasks.Any(task => !task.IsDone && !task.IsShelvedOn(Date));

    public ObservableCollection<DiaryTask> ShelvedTasks { get; } = new();
    public bool HasShelvedTasks => ShelvedTasks.Count > 0;
    public string ShelvedLabel => $"{(Date == DateTime.Today ? "今日" : "当日")}已推进 · {ShelvedTasks.Count}";
    public bool IsToday => Date == DateTime.Today;
    private void ObserveTasks()
    {
        foreach (var old in _observed.Where(t => !Tasks.Contains(t)).ToArray()) { old.PropertyChanged -= TaskChanged; _observed.Remove(old); }
        foreach (var task in Tasks) if (_observed.Add(task)) task.PropertyChanged += TaskChanged;
    }
    private void TasksChanged(object? sender, NotifyCollectionChangedEventArgs e) { ObserveTasks(); RefreshPlanning(); }
    public void RefreshPlanning()
    {
        ActiveTasks.Refresh(); ShelvedTasks.Clear();
        foreach (var task in Tasks.Where(t => !t.IsDone && t.IsShelvedOn(Date))) ShelvedTasks.Add(task);
        foreach (var name in new[] { nameof(ProgressLabel), nameof(HasTasks), nameof(HasShelvedTasks), nameof(ShelvedLabel), nameof(IsToday) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    private void TaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiaryTask.IsDone) or nameof(DiaryTask.IsShelvedOn)) RefreshPlanning();
    }
    private void Planning_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null || sender is not System.Windows.Controls.Button { DataContext: DiaryTask task } button) return;
        var menu = TaskPlanningMenu.Create(this, task, _repository, () => System.Threading.Tasks.Task.CompletedTask, Date);
        menu.PlacementTarget = button; menu.IsOpen = true; e.Handled = true;
    }
    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null || sender is not System.Windows.Controls.Button { DataContext: DiaryTask task }) return;
        try { _repository.SetDayShelved(task, Date, false); }
        catch (Exception error) { System.Windows.MessageBox.Show(this, error.Message, "小岛日记"); }
    }

    private void TaskCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfCheckBox { DataContext: DiaryTask task }) _saveCompletion(task);
    }

    private void TodoList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Recycled rows can retain wider extent estimates during a resize. Keep
        // focus/BringIntoView requests from moving the completion boxes offscreen.
        if (e.OriginalSource is ScrollViewer { HorizontalOffset: > 0 } scroll)
            scroll.ScrollToHorizontalOffset(0);
    }

    private void TodoList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        for (DependencyObject? current = source; current is not null && current != TodoList;
             current = current is Visual or Visual3D
                 ? VisualTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase) return;
            if (current is ListBoxItem { DataContext: DiaryTask task })
            {
                _openTaskNote(task);
                return;
            }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        Tasks.CollectionChanged -= TasksChanged;
        foreach (var task in _observed) task.PropertyChanged -= TaskChanged;
        _observed.Clear();
        base.OnClosing(e);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
