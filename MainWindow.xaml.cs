using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Data;
using Forms = System.Windows.Forms;

namespace Cdiary;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DiaryRepository _repository;
    private readonly ReminderScheduler _scheduler;
    private readonly CultureInfo _chinese = CultureInfo.GetCultureInfo("zh-CN");
    private readonly DispatcherTimer _saveTimer;
    private readonly HashSet<DiaryTask> _dirtyTasks = new();
    private readonly Dictionary<long, TaskNoteWindow> _noteWindows = new();
    private readonly Dictionary<DateTime, DailyTodoWindow> _dayWindows = new();
    private DateTime _selectedDate = DateTime.Today;
    private DateTime _weekStart = DateTime.Today.AddDays(-3);
    private DayOption? _selectedDay;
    private DiaryTask? _selectedTask;
    private ObservableCollection<DiaryTask> _tasks = new();
    private string _dayMemo = "";
    private string _searchStatus = "输入关键词，按回车搜索";
    private bool _memoDirty;
    private int _searchVersion;
    private int _completedFilterDays;
    private List<DiaryTask> _completedTasks = [];

    public MainWindow(DiaryRepository repository, ReminderScheduler scheduler)
    {
        _repository = repository;
        _scheduler = scheduler;
        InitializeComponent();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => FlushEdits();
        SearchResults.CollectionChanged += (_, _) => Changed(nameof(HasSearchResults));
        ActiveTasks = CollectionViewSource.GetDefaultView(_tasks);
        ActiveTasks.Filter = item => item is DiaryTask { IsDone: false };
        DataContext = this;
        AutostartCheck.IsChecked = StartupSettings.IsEnabled;
        RefreshDays();
        OpenDate(DateTime.Today);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool AllowExit { get; set; }
    public ObservableCollection<DayOption> Days { get; } = new();
    public ObservableCollection<SearchResult> SearchResults { get; } = new();
    public ObservableCollection<CompletedDayGroup> CompletedGroups { get; } = new();
    public ICollectionView ActiveTasks { get; private set; }
    public bool HasActiveTasks => Tasks.Any(task => !task.IsDone);
    public string EmptyTaskMessage => Tasks.Count == 0 ? "这一天还没有安排任务" : "待办已清空，完成的任务在左侧“完成”里";
    public string CompletedCountLabel => $"已完成 {_completedTasks.Count} 项任务";
    public bool HasCompletedTasks => CompletedGroups.Count > 0;
    public bool HasSearchResults => SearchResults.Count > 0;
    public string SearchStatus
    {
        get => _searchStatus;
        private set { _searchStatus = value; Changed(); }
    }

    public DayOption? SelectedDay
    {
        get => _selectedDay;
        set { _selectedDay = value; Changed(); }
    }

    public ObservableCollection<DiaryTask> Tasks
    {
        get => _tasks;
        private set
        {
            foreach (var task in _tasks) task.PropertyChanged -= TaskChanged;
            _tasks = value;
            foreach (var task in _tasks) task.PropertyChanged += TaskChanged;
            ActiveTasks = CollectionViewSource.GetDefaultView(_tasks);
            ActiveTasks.Filter = item => item is DiaryTask { IsDone: false };
            Changed();
            Changed(nameof(ActiveTasks));
            Changed(nameof(HasActiveTasks));
            Changed(nameof(EmptyTaskMessage));
            Changed(nameof(ProgressLabel));
        }
    }

    public DiaryTask? SelectedTask
    {
        get => _selectedTask;
        set
        {
            _selectedTask = value;
            ReminderBox.Text = value?.ReminderTime?.ToString(@"hh\:mm") ?? "";
            Changed();
            Changed(nameof(HasSelectedTask));
            Changed(nameof(RepeatButtonLabel));
        }
    }

    public bool HasSelectedTask => SelectedTask is not null;
    public string RepeatButtonLabel => SelectedTask?.RecurrenceRuleId is long id
        ? _repository.LoadRule(id) is { EndDate: null } rule
            ? rule.IsWeekly ? "每周重复" : "每天重复"
            : "设置重复"
        : "设置重复";
    public string MonthLabel => _selectedDate.ToString("yyyy年M月", _chinese);
    public string SelectedDateLabel => _selectedDate.ToString("M月d日 dddd", _chinese);
    public string TaskHeader => _selectedDate == DateTime.Today ? "今天要做" : $"{_selectedDate:M月d日}要做";
    public string ProgressLabel => $"已完成 {Tasks.Count(task => task.IsDone)} / {Tasks.Count}";

    public string DayMemo
    {
        get => _dayMemo;
        set
        {
            if (_dayMemo == value) return;
            _dayMemo = value;
            _memoDirty = true;
            QueueSave();
            Changed();
        }
    }

    private void RefreshDays()
    {
        Days.Clear();
        for (var i = 0; i < 7; i++)
        {
            var date = _weekStart.AddDays(i);
            Days.Add(new DayOption(date, date.ToString("ddd", _chinese),
                date == DateTime.Today ? "今天" : date < DateTime.Today ? "过去" : "待安排"));
        }
        SelectedDay = Days.FirstOrDefault(day => day.Date == _selectedDate);
    }

    private void OpenDate(DateTime date)
    {
        FlushEdits();
        _selectedDate = date.Date;
        if (!Days.Any(day => day.Date == _selectedDate))
        {
            _weekStart = _selectedDate.AddDays(-3);
            RefreshDays();
        }
        else SelectedDay = Days.First(day => day.Date == _selectedDate);

        _repository.EnsureOccurrences(_selectedDate);
        if (_dayWindows.TryGetValue(_selectedDate, out var dayWindow))
        {
            var known = dayWindow.Tasks.Select(task => task.Id).ToHashSet();
            foreach (var newTask in _repository.LoadTasks(_selectedDate))
                if (known.Add(newTask.Id)) dayWindow.Tasks.Add(newTask);
            Tasks = dayWindow.Tasks;
        }
        else
        {
            var loadedTasks = _repository.LoadTasks(_selectedDate);
            for (var i = 0; i < loadedTasks.Count; i++)
                if (_noteWindows.TryGetValue(loadedTasks[i].Id, out var noteWindow))
                    loadedTasks[i] = noteWindow.Task;
            Tasks = new ObservableCollection<DiaryTask>(loadedTasks);
        }
        _dayMemo = _repository.LoadDayMemo(_selectedDate);
        _memoDirty = false;
        SelectedTask = Tasks.FirstOrDefault(task => !task.IsDone);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (SelectedTask is not null) TaskList.ScrollIntoView(SelectedTask);
        }), DispatcherPriority.Loaded);
        Changed(nameof(MonthLabel));
        Changed(nameof(SelectedDateLabel));
        Changed(nameof(TaskHeader));
        Changed(nameof(DayMemo));
        Changed(nameof(ProgressLabel));
    }

    public void ShowTask(DateTime date, long id)
    {
        ShowHomePage();
        OpenDate(date);
        SelectedTask = Tasks.FirstOrDefault(task => task.Id == id);
        if (SelectedTask is not null) TaskList.ScrollIntoView(SelectedTask);
    }

    private void ShowMemo(DateTime date)
    {
        ShowHomePage();
        OpenDate(date);
        DayMemoBox.Focus();
    }

    private void ShowHomePage()
    {
        HomeTab.IsChecked = true;
        HomePage.Visibility = Visibility.Visible;
        SearchPage.Visibility = Visibility.Collapsed;
        CompletedPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
    }

    private void ShowSearchPage()
    {
        SearchTab.IsChecked = true;
        HomePage.Visibility = Visibility.Collapsed;
        SearchPage.Visibility = Visibility.Visible;
        CompletedPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(new Action(() => SearchBox.Focus()), DispatcherPriority.Input);
    }

    private void ShowSettingsPage()
    {
        SettingsTab.IsChecked = true;
        HomePage.Visibility = Visibility.Collapsed;
        SearchPage.Visibility = Visibility.Collapsed;
        CompletedPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        AutostartCheck.IsChecked = StartupSettings.IsEnabled;
    }

    private void ShowCompletedPage()
    {
        FlushEdits();
        CompletedTab.IsChecked = true;
        HomePage.Visibility = Visibility.Collapsed;
        SearchPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        CompletedPage.Visibility = Visibility.Visible;
        ReloadCompleted();
    }

    private void ReloadCompleted()
    {
        _completedTasks = _repository.LoadCompletedTasks();
        RefreshCompletedGroups();
    }

    private void RefreshCompletedGroups()
    {
        CompletedGroups.Clear();
        var cutoff = _completedFilterDays switch
        {
            1 => DateTime.Today,
            7 => DateTime.Today.AddDays(-6),
            _ => DateTime.MinValue
        };
        foreach (var group in _completedTasks.Where(task => (task.CompletedAt ?? task.Date).Date >= cutoff)
                     .GroupBy(task => (task.CompletedAt ?? task.Date).Date))
            CompletedGroups.Add(new CompletedDayGroup(group.Key, group.ToList()));
        Changed(nameof(CompletedCountLabel));
        Changed(nameof(HasCompletedTasks));
    }

    private void TaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DiaryTask task) return;
        if (e.PropertyName == nameof(DiaryTask.ReminderLabel)) return;
        _dirtyTasks.Add(task);
        if (e.PropertyName is nameof(DiaryTask.IsDone) or nameof(DiaryTask.ReminderTime))
        {
            FlushEdits();
            _scheduler.Refresh();
            Changed(nameof(ProgressLabel));
            if (e.PropertyName == nameof(DiaryTask.IsDone))
            {
                ActiveTasks.Refresh();
                Changed(nameof(HasActiveTasks));
                Changed(nameof(EmptyTaskMessage));
                if (task.IsDone && ReferenceEquals(SelectedTask, task)) SelectedTask = null;
                if (CompletedPage.Visibility == Visibility.Visible) ReloadCompleted();
            }
        }
        else QueueSave();
    }

    private void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void FlushEdits()
    {
        _saveTimer.Stop();
        foreach (var task in _dirtyTasks) _repository.SaveTask(task);
        _dirtyTasks.Clear();
        if (_memoDirty)
        {
            _repository.SaveDayMemo(_selectedDate, _dayMemo);
            _memoDirty = false;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        FlushEdits();
        if (!AllowExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        foreach (var dayWindow in _dayWindows.Values.ToArray()) dayWindow.Close();
        foreach (var noteWindow in _noteWindows.Values.ToArray()) noteWindow.Close();
        base.OnClosing(e);
    }

    private void Days_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedDay is { } day && day.Date != _selectedDate) OpenDate(day.Date);
    }

    private void PreviousWeek_Click(object sender, RoutedEventArgs e)
    {
        _weekStart = _weekStart.AddDays(-7);
        OpenDate(_weekStart.AddDays(3));
    }

    private void NextWeek_Click(object sender, RoutedEventArgs e)
    {
        _weekStart = _weekStart.AddDays(7);
        OpenDate(_weekStart.AddDays(3));
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        ShowHomePage();
        OpenDate(DateTime.Today);
    }
    private void HomeTab_Click(object sender, RoutedEventArgs e) => ShowHomePage();
    private void SearchTab_Click(object sender, RoutedEventArgs e) => ShowSearchPage();
    private void CompletedTab_Click(object sender, RoutedEventArgs e) => ShowCompletedPage();
    private void SettingsTab_Click(object sender, RoutedEventArgs e) => ShowSettingsPage();

    private void CompletedFilter_Click(object sender, RoutedEventArgs e)
    {
        _completedFilterDays = sender == TodayCompletedFilter ? 1 : sender == WeekCompletedFilter ? 7 : 0;
        RefreshCompletedGroups();
    }

    private void RestoreCompleted_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: DiaryTask row }) return;
        var live = Tasks.FirstOrDefault(task => task.Id == row.Id)
            ?? _dayWindows.Values.SelectMany(window => window.Tasks).FirstOrDefault(task => task.Id == row.Id)
            ?? (_noteWindows.TryGetValue(row.Id, out var noteWindow) ? noteWindow.Task : null)
            ?? row;
        live.IsDone = false;
        _repository.SaveTask(live);
        _scheduler.Refresh();
        ReloadCompleted();
    }

    private void AutostartCheck_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupSettings.SetEnabled(AutostartCheck.IsChecked == true);
            SettingsStatus.Text = AutostartCheck.IsChecked == true ? "已开启，下次登录 Windows 后生效。" : "已关闭开机自启。";
        }
        catch (Exception error)
        {
            AutostartCheck.IsChecked = StartupSettings.IsEnabled;
            SettingsStatus.Text = $"设置失败：{error.Message}";
        }
    }

    private void RepeatRule_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTask is not { } task) return;
        FlushEdits();
        if (new RepeatRuleWindow(_repository, task) { Owner = this }.ShowDialog() == true)
        {
            Changed(nameof(RepeatButtonLabel));
            _scheduler.Refresh();
        }
    }

    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = RunSearchAsync();
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await RunSearchAsync();

    private async Task RunSearchAsync()
    {
        var query = SearchBox.Text.Trim();
        var version = ++_searchVersion;
        SearchResults.Clear();
        if (query.Length == 0)
        {
            SearchStatus = "输入关键词，按回车搜索";
            return;
        }

        FlushEdits();
        foreach (var noteWindow in _noteWindows.Values) noteWindow.FlushEdits();
        SearchStatus = "正在搜索…";
        try
        {
            var results = await Task.Run(() => _repository.Search(query));
            if (version != _searchVersion) return;
            foreach (var result in results) SearchResults.Add(result);
            SearchStatus = results.Count == 0 ? "没有找到相关内容" : $"找到 {results.Count} 条结果";
        }
        catch (Exception error)
        {
            if (version == _searchVersion) SearchStatus = $"搜索失败：{error.Message}";
        }
    }

    private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResultsList.SelectedItem is not SearchResult result) return;
        SearchResultsList.SelectedItem = null;
        if (result.TaskId is long id) ShowTask(result.Date, id);
        else ShowMemo(result.Date);
    }
    private void AddTask_Click(object sender, RoutedEventArgs e) => AddTask();

    private void NewTaskBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddTask(); e.Handled = true; }
    }

    private void AddTask()
    {
        var title = NewTaskBox.Text.Trim();
        if (title.Length == 0) return;
        var task = _repository.AddTask(_selectedDate, title);
        task.PropertyChanged += TaskChanged;
        Tasks.Add(task);
        SelectedTask = task;
        TaskList.ScrollIntoView(task);
        NewTaskBox.Clear();
        Changed(nameof(ProgressLabel));
        Changed(nameof(HasActiveTasks));
        Changed(nameof(EmptyTaskMessage));
    }

    private void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTask is null) return;
        var task = SelectedTask;
        if (_noteWindows.TryGetValue(task.Id, out var noteWindow)) noteWindow.Close();
        _dirtyTasks.Remove(task);
        task.PropertyChanged -= TaskChanged;
        _repository.DeleteTask(task.Id);
        Tasks.Remove(task);
        SelectedTask = Tasks.FirstOrDefault(task => !task.IsDone);
        _scheduler.Refresh();
        Changed(nameof(ProgressLabel));
        Changed(nameof(HasActiveTasks));
        Changed(nameof(EmptyTaskMessage));
    }

    private void TaskCheck_Click(object sender, RoutedEventArgs e) => Changed(nameof(ProgressLabel));

    private void OpenDay_Click(object sender, RoutedEventArgs e)
    {
        if (_dayWindows.TryGetValue(_selectedDate, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var dayWindow = new DailyTodoWindow(_selectedDate, Tasks, OpenTaskNote, SaveCompletionFromDaily);
        PlaceNearMain(dayWindow, Left + 205, Top + 80);
        _dayWindows.Add(_selectedDate, dayWindow);
        dayWindow.Closed += (_, _) => _dayWindows.Remove(dayWindow.Date);
        dayWindow.Show();
    }

    private void SaveCompletionFromDaily(DiaryTask task)
    {
        if (Tasks.Contains(task)) return;
        _repository.SaveTask(task);
        _scheduler.Refresh();
        if (CompletedPage.Visibility == Visibility.Visible) ReloadCompleted();
    }

    private void OpenNote_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTask is null) return;
        OpenTaskNote(SelectedTask);
    }

    private void OpenTaskNote(DiaryTask task)
    {
        var fromPinnedDay = _dayWindows.TryGetValue(task.Date, out var dayWindow) && dayWindow.Topmost;
        if (_noteWindows.TryGetValue(task.Id, out var existing))
        {
            if (fromPinnedDay) existing.SetPinned(true);
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        if (Tasks.Contains(task)) SelectedTask = task;
        var noteWindow = new TaskNoteWindow(task, _repository);
        PlaceNearMain(noteWindow, Left + ActualWidth - 45, Top + 85);
        if (fromPinnedDay) noteWindow.SetPinned(true);
        _noteWindows.Add(task.Id, noteWindow);
        noteWindow.Closed += (_, _) => _noteWindows.Remove(noteWindow.Task.Id);
        noteWindow.Show();
    }

    private void PlaceNearMain(Window window, double preferredLeft, double preferredTop)
    {
        var screen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? System.Windows.Media.Matrix.Identity;
        var workArea = new Rect(
            fromDevice.Transform(new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top)),
            fromDevice.Transform(new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom)));
        window.Left = Math.Clamp(preferredLeft, workArea.Left,
            Math.Max(workArea.Left, workArea.Right - window.Width));
        window.Top = Math.Clamp(preferredTop, workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - window.Height));
    }

    private void SetReminder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTask is null) return;
        var text = ReminderBox.Text.Trim();
        if (text.Length == 0) { SelectedTask.ReminderTime = null; return; }
        if (TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out var time)
            && time < TimeSpan.FromDays(1)) SelectedTask.ReminderTime = time;
        else System.Windows.MessageBox.Show(this, "请输入 00:00 至 23:59 之间的时间，例如 14:30。", "提醒时间格式",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Changed([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
