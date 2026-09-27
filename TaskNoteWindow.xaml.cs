using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace Cdiary;

public partial class TaskNoteWindow : Window
{
    private readonly DiaryRepository _repository;
    private readonly DispatcherTimer _saveTimer;
    private bool _dirty;

    public TaskNoteWindow(DiaryTask task, DiaryRepository repository)
    {
        Task = task;
        _repository = repository;
        InitializeComponent();
        DataContext = task;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => FlushEdits();
        Task.PropertyChanged += TaskChanged;
        Title = string.IsNullOrWhiteSpace(Task.Title) ? "任务便签" : $"{Task.Title} · 小岛日记";
    }

    public DiaryTask Task { get; }

    private void TaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiaryTask.Title))
            Title = string.IsNullOrWhiteSpace(Task.Title) ? "任务便签" : $"{Task.Title} · 小岛日记";
        if (e.PropertyName is nameof(DiaryTask.Title) or nameof(DiaryTask.Note))
        {
            _dirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        }
    }

    public void FlushEdits()
    {
        _saveTimer.Stop();
        if (!_dirty) return;
        _repository.SaveTask(Task);
        _dirty = false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        FlushEdits();
        Task.PropertyChanged -= TaskChanged;
        base.OnClosing(e);
    }

    public void SetPinned(bool pinned) => Topmost = pinned;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
