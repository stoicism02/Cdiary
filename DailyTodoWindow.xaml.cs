using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using WpfCheckBox = System.Windows.Controls.CheckBox;

namespace Cdiary;

public partial class DailyTodoWindow : Window, INotifyPropertyChanged
{
    private readonly Action<DiaryTask> _openTaskNote;
    private readonly Action<DiaryTask> _saveCompletion;

    public DailyTodoWindow(DateTime date, ObservableCollection<DiaryTask> tasks,
        Action<DiaryTask> openTaskNote, Action<DiaryTask> saveCompletion)
    {
        Date = date.Date;
        Tasks = tasks;
        _openTaskNote = openTaskNote;
        _saveCompletion = saveCompletion;
        InitializeComponent();
        DataContext = this;
        Tasks.CollectionChanged += TasksChanged;
        foreach (var task in Tasks) task.PropertyChanged += TaskChanged;
        Title = $"{DayLabel}待办 · 小岛日记";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public DateTime Date { get; }
    public ObservableCollection<DiaryTask> Tasks { get; }
    public string DayLabel => Date.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
    public string ProgressLabel => $"已完成 {Tasks.Count(task => task.IsDone)} / {Tasks.Count}";
    public bool HasTasks => Tasks.Count > 0;

    private void TasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (DiaryTask task in e.OldItems) task.PropertyChanged -= TaskChanged;
        if (e.NewItems is not null)
            foreach (DiaryTask task in e.NewItems) task.PropertyChanged += TaskChanged;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTasks)));
    }

    private void TaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiaryTask.IsDone))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressLabel)));
    }

    private void TaskCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfCheckBox { DataContext: DiaryTask task }) _saveCompletion(task);
    }

    private void TodoList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        for (DependencyObject? current = source; current is not null && current != TodoList;
             current = current is Visual or Visual3D
                 ? VisualTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
        {
            if (current is WpfCheckBox) return;
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
        foreach (var task in Tasks) task.PropertyChanged -= TaskChanged;
        base.OnClosing(e);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
