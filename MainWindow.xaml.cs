using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    private readonly DispatcherTimer _dayTimer;
    private readonly HashSet<DiaryTask> _dirtyTasks = new();
    private readonly Dictionary<long, TaskNoteWindow> _noteWindows = new();
    private readonly Dictionary<DateTime, DailyTodoWindow> _dayWindows = new();
    private DateTime _selectedDate = DateTime.Today;
    private DateTime _weekStart = DateTime.Today.AddDays(-3);
    private DayOption? _selectedDay;
    private DiaryTask? _selectedTask;
    private ObservableCollection<DiaryTask> _tasks = new();
    private readonly HashSet<DiaryTask> _observedTasks = new();
    private DiaryTask? _standaloneSelection;
    private string _dayMemo = "";
    private string _searchStatus = "输入关键词，按回车搜索";
    private bool _memoDirty;
    private int _searchVersion;
    private int _completedFilterDays;
    private List<DiaryTask> _completedTasks = [];
    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _calendarSelectedDate = DateTime.Today;
    private DateTime _knownToday = DateTime.Today;
    private bool _overdueExpanded = true;
    private bool _overdueManuallyToggled;
    private WindowLayoutMode _layoutMode = WindowLayoutMode.Standard;
    private bool _hasResponsiveLayout;
    private bool _compactDetails;
    private int _calendarFilter;
    private int _calendarVisibleLimit = 3;
    private DateTime? _calendarExpandedDate;
    public WindowLayoutMode CurrentLayoutMode => _layoutMode;
    public bool IsCompactDetailOpen => _layoutMode == WindowLayoutMode.Compact && _compactDetails;

    public MainWindow(DiaryRepository repository, ReminderScheduler scheduler, ScreenshotService? screenshots = null, FocusStore? focusStore = null, FocusService? focus = null)
    {
        _repository = repository;
        _scheduler = scheduler;
        InitializeComponent();
        InitializeFocus(focusStore, focus);
        TaskNoteEditor.DocumentRepository = repository;
        ScreenshotGallery.SettingsRequested += ShowSettingsPage;
        if (screenshots is not null)
        {
            ScreenshotGallery.Attach(screenshots);
            ScreenshotSettings.Attach(screenshots);
        }
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => FlushEdits();
        _dayTimer = new DispatcherTimer();
        _dayTimer.Tick += (_, _) => RefreshForNewDay();
        SearchResults.CollectionChanged += (_, _) => Changed(nameof(HasSearchResults));
        ActiveTasks = new ListCollectionView(_tasks);
        ActiveTasks.Filter = item => item is DiaryTask { IsDone: false } task && !task.IsShelvedOn(_selectedDate);
        DataContext = this;
        AutostartCheck.IsChecked = StartupSettings.IsEnabled;
        RefreshDays();
        OpenDate(DateTime.Today);
        ScheduleDayChange();
        _repository.PlanningChanged += PlanningChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool AllowExit { get; set; }
    public void ConfigureDevelopmentPreview()
    {
        Title = "小岛日记 · 开发预览";
        AppHeading.Text = "小岛开发预览";
        AutostartCheck.IsChecked = false; AutostartCheck.IsEnabled = false;
        AutostartCheck.ToolTip = "开发预览不修改开机自启设置";
        EnvironmentStatus.Text = "示例数据 · 样式热加载";
    }
    public void ShowStyleReloadStatus(bool success, string message)
    {
        EnvironmentStatus.Text = success ? message : "样式有误，保留上一版";
        EnvironmentStatus.ToolTip = message;
        EnvironmentStatus.SetResourceReference(TextBlock.ForegroundProperty, success ? "Muted" : "DangerInk");
    }
    public ObservableCollection<DayOption> Days { get; } = new();
    public ObservableCollection<SearchResult> SearchResults { get; } = new();
    public ObservableCollection<CompletedDayGroup> CompletedGroups { get; } = new();
    public ObservableCollection<CalendarDay> CalendarDays { get; } = new();
    public ObservableCollection<DiaryTask> OverdueTasks { get; } = new();
    public string OverdueCountLabel => $"之前没做完 · {OverdueTasks.Count}";
    public string CalendarMonthLabel => _calendarMonth.ToString("yyyy年M月", _chinese);
    public ICollectionView ActiveTasks { get; private set; }
    public bool HasActiveTasks => Tasks.Any(task => !task.IsDone && !task.IsShelvedOn(_selectedDate));
    public string EmptyTaskMessage => Tasks.Count == 0 ? "这一天还没有安排任务" : "待办已收起或完成，可查看下方及“完成”页";
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
            foreach (var task in _observedTasks) task.PropertyChanged -= TaskChanged;
            _observedTasks.Clear();
            _tasks = value;
            foreach (var task in _tasks) { task.PropertyChanged += TaskChanged; _observedTasks.Add(task); }
            ActiveTasks = new ListCollectionView(_tasks);
            ActiveTasks.Filter = item => item is DiaryTask { IsDone: false } task && !task.IsShelvedOn(_selectedDate);
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
            if (_standaloneSelection is not null) _standaloneSelection.PropertyChanged -= TaskChanged;
            _standaloneSelection = value is not null && !Tasks.Contains(value) && !OverdueTasks.Contains(value) ? value : null;
            if (_standaloneSelection is not null) _standaloneSelection.PropertyChanged += TaskChanged;
            _selectedTask = value;
            TaskFocus.Task=value;TaskFocus.Reload();
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
        _compactDetails = false;
        ApplyHomeVisibility();
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
            MergeDay(dayWindow.Tasks, _selectedDate);
            Tasks = dayWindow.Tasks;
        }
        else
        {
            Tasks = new ObservableCollection<DiaryTask>(_repository.LoadTasks(_selectedDate).Select(ResolveTask));
        }
        RefreshShelvedTasks();
        _dayMemo = _repository.LoadDayMemo(_selectedDate);
        _memoDirty = false;
        SelectedTask = Tasks.FirstOrDefault(task => !task.IsDone && !task.IsShelvedOn(_selectedDate));
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (SelectedTask is not null) TaskList.ScrollIntoView(SelectedTask);
        }), DispatcherPriority.Loaded);
        Changed(nameof(MonthLabel));
        Changed(nameof(SelectedDateLabel));
        Changed(nameof(TaskHeader));
        Changed(nameof(DayMemo));
        Changed(nameof(ProgressLabel));
        RefreshOverdueTasks();
    }

    private void RefreshOverdueTasks()
    {
        foreach (var task in OverdueTasks) task.PropertyChanged -= TaskChanged;
        OverdueTasks.Clear();
        if (_selectedDate == DateTime.Today)
        {
            foreach (var row in _repository.LoadOverdueTasks(DateTime.Today))
            {
                var task = _noteWindows.TryGetValue(row.Id, out var noteWindow) ? noteWindow.Task
                    : _dayWindows.TryGetValue(row.Date, out var dayWindow)
                        ? dayWindow.Tasks.FirstOrDefault(item => item.Id == row.Id) ?? row
                        : row;
                task.PropertyChanged += TaskChanged;
                OverdueTasks.Add(task);
            }
        }
        UpdateOverdueSection();
    }

    private void UpdateOverdueSection()
    {
        OverdueSection.Visibility = _selectedDate == DateTime.Today && OverdueTasks.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        OverdueList.Visibility = _overdueExpanded ? Visibility.Visible : Visibility.Collapsed;
        OverdueToggle.Content = _overdueExpanded ? "收起" : "展开";
        Changed(nameof(OverdueCountLabel));
    }

    private void ScheduleDayChange()
    {
        _dayTimer.Stop();
        var untilTomorrow = DateTime.Today.AddDays(1).AddMilliseconds(100) - DateTime.Now;
        _dayTimer.Interval = untilTomorrow > TimeSpan.Zero ? untilTomorrow : TimeSpan.FromMilliseconds(100);
        _dayTimer.Start();
    }

    private void RefreshForNewDay(bool refreshOverdue = false)
    {
        var today = DateTime.Today;
        if (today != _knownToday)
        {
            var previousToday = _knownToday;
            _knownToday = today;
            if (_selectedDate == previousToday && HomePage.Visibility == Visibility.Visible)
                OpenDate(today);
            else
            {
                RefreshDays();
                RefreshOverdueTasks();
            }
            PlanningChanged();
        }
        else if (refreshOverdue && _selectedDate == today) RefreshOverdueTasks();
        ScheduleDayChange();
    }

    public void RefreshTodayOverview() => RefreshForNewDay(refreshOverdue: true);

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        RefreshForNewDay();
    }

    public void ShowTask(DateTime date, long id)
    {
        ShowHomePage();
        OpenDate(date);
        SelectedTask = Tasks.FirstOrDefault(task => task.Id == id) ?? (_repository.FindTask(id) is { } row ? ResolveTask(row) : null);
        if (SelectedTask is not null) TaskList.ScrollIntoView(SelectedTask);
        if (_layoutMode == WindowLayoutMode.Compact && SelectedTask is not null) ShowCompactDetails();
    }

    private void ShowMemo(DateTime date)
    {
        ShowHomePage();
        OpenDate(date);
        DayMemoBox.Focus();
    }

    private void ShowHomePage()
    {
        _compactDetails = false;
        ApplyHomeVisibility();
        FocusView.Visibility = Visibility.Collapsed; ScreenshotGallery.Visibility = Visibility.Collapsed;
        HomeTab.IsChecked = true;
        HomePage.Visibility = Visibility.Visible;
        CalendarPage.Visibility = Visibility.Collapsed;
        SearchPage.Visibility = Visibility.Collapsed;
        CompletedPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        if (_selectedDate == DateTime.Today) RefreshOverdueTasks();
    }

    private void ShowSearchPage()
    {
        FocusView.Visibility = Visibility.Collapsed; ScreenshotGallery.Visibility = Visibility.Collapsed;
        SearchTab.IsChecked = true;
        HomePage.Visibility = Visibility.Collapsed;
        CalendarPage.Visibility = Visibility.Collapsed;
        SearchPage.Visibility = Visibility.Visible;
        CompletedPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(new Action(() => SearchBox.Focus()), DispatcherPriority.Input);
    }

    private void ShowSettingsPage()
    {
        FocusView.Visibility = Visibility.Collapsed; ScreenshotGallery.Visibility = Visibility.Collapsed;
        SettingsTab.IsChecked = true;
        HomePage.Visibility = Visibility.Collapsed;
        CalendarPage.Visibility = Visibility.Collapsed;
        SearchPage.Visibility = Visibility.Collapsed;
        CompletedPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        if (AutostartCheck.IsEnabled) AutostartCheck.IsChecked = StartupSettings.IsEnabled;
        _ = RefreshAttachmentUsageAsync();
    }

    private void ShowCompletedPage()
    {
        FocusView.Visibility = Visibility.Collapsed; ScreenshotGallery.Visibility = Visibility.Collapsed;
        FlushEdits();
        CompletedTab.IsChecked = true;
        HomePage.Visibility = Visibility.Collapsed;
        CalendarPage.Visibility = Visibility.Collapsed;
        SearchPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        CompletedPage.Visibility = Visibility.Visible;
        ReloadCompleted();
    }

    private void ShowCalendarPage()
    {
        FocusView.Visibility = Visibility.Collapsed; ScreenshotGallery.Visibility = Visibility.Collapsed;
        FlushEdits();
        _calendarSelectedDate = _selectedDate;
        _calendarMonth = new DateTime(_selectedDate.Year, _selectedDate.Month, 1);
        CalendarTab.IsChecked = true;
        HomePage.Visibility = Visibility.Collapsed;
        SearchPage.Visibility = Visibility.Collapsed;
        CompletedPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        CalendarPage.Visibility = Visibility.Visible;
        CalendarDayOverlay.Visibility = Visibility.Collapsed;
        RefreshCalendar();
    }

    private void RefreshCalendar()
    {
        var first = _calendarMonth;
        var visibleStart = first.AddDays(-(int)first.DayOfWeek);
        var daysNeeded = (int)first.DayOfWeek + DateTime.DaysInMonth(first.Year, first.Month);
        var cellCount = Math.Max(35, ((daysNeeded + 6) / 7) * 7);
        _calendarVisibleLimit = Math.Max(1, (int)((Math.Max(470, CalendarGrid.ActualHeight) / (cellCount / 7) - 43) / 22));
        var entries = _repository.LoadCalendarEntries(visibleStart, visibleStart.AddDays(cellCount))
            .GroupBy(entry => entry.Date).ToDictionary(group => group.Key, group => (IReadOnlyList<CalendarEntry>)group.ToList());
        CalendarDays.Clear();
        for (var day = 0; day < cellCount; day++)
        {
            var date = visibleStart.AddDays(day);
            var dayEntries = entries.GetValueOrDefault(date) ?? [];
            CalendarDays.Add(new CalendarDay(date, first, _calendarSelectedDate,
                dayEntries, _calendarVisibleLimit, _calendarFilter));
        }
        Changed(nameof(CalendarMonthLabel));
        if (CalendarDayOverlay.Visibility == Visibility.Visible && _calendarExpandedDate is { } expanded)
        {
            var day = CalendarDays.FirstOrDefault(item => item.Date == expanded);
            if (day is null) CalendarDayOverlay.Visibility = Visibility.Collapsed;
            else SetCalendarDayContents(day);
        }
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
        if (e.PropertyName is not (nameof(DiaryTask.Title) or nameof(DiaryTask.Note) or nameof(DiaryTask.IsDone) or nameof(DiaryTask.ReminderTime))) return;
        _dirtyTasks.Add(task);
        if (e.PropertyName is nameof(DiaryTask.IsDone) or nameof(DiaryTask.ReminderTime))
        {
            FlushEdits();
            _scheduler.Refresh();
            Changed(nameof(ProgressLabel));
            if (e.PropertyName == nameof(DiaryTask.IsDone))
            {
                PlanningChanged();
                if (task.IsDone && OverdueTasks.Remove(task))
                {
                    task.PropertyChanged -= TaskChanged;
                    UpdateOverdueSection();
                }
                ActiveTasks.Refresh();
                Changed(nameof(HasActiveTasks));
                Changed(nameof(EmptyTaskMessage));
                if (task.IsDone && ReferenceEquals(SelectedTask, task)) SelectedTask = null;
                if (CompletedPage.Visibility == Visibility.Visible) ReloadCompleted();
                if (CalendarPage.Visibility == Visibility.Visible) RefreshCalendar();
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

    public async System.Threading.Tasks.Task PrepareDocumentExitAsync()
    {
        await TaskNoteEditor.FlushDocumentAsync();
        foreach (var noteWindow in _noteWindows.Values.ToArray()) await noteWindow.PrepareDocumentExitAsync();
        FlushEdits();
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
        _planningClosed = true;
        _repository.PlanningChanged -= PlanningChanged;
        foreach (var dayWindow in _dayWindows.Values.ToArray()) dayWindow.Close();
        foreach (var noteWindow in _noteWindows.Values.ToArray()) noteWindow.Close();
        _dayTimer.Stop();
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
    private void OverdueToggle_Click(object sender, RoutedEventArgs e)
    {
        _overdueManuallyToggled = true;
        _overdueExpanded = !_overdueExpanded;
        UpdateOverdueSection();
    }

    private void HomePage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height <= 0) return;
        MemoRow.Height = new GridLength(Math.Clamp((e.NewSize.Height - DateToolbar.ActualHeight) * 0.24, 110, 210));
        if (_overdueManuallyToggled) return;
        var expanded = e.NewSize.Height >= 520;
        if (_overdueExpanded == expanded) return;
        _overdueExpanded = expanded;
        UpdateOverdueSection();
    }

    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || TaskNotePanel is null) return;
        var mode = ResponsiveLayout.ForWidth(e.NewSize.Width + LayoutRoot.Margin.Left + LayoutRoot.Margin.Right);
        var shortWindow = e.NewSize.Height + LayoutRoot.Margin.Top + LayoutRoot.Margin.Bottom < 650;
        if (!_hasResponsiveLayout || mode != _layoutMode)
        {
            if (mode == WindowLayoutMode.Compact && TaskNotePanel.IsKeyboardFocusWithin) _compactDetails = true;
            _layoutMode = mode; _hasResponsiveLayout = true;
            var wide = mode == WindowLayoutMode.Wide;
            DateColumn.Width = new GridLength(wide ? 180 : 0);
            DateGap.Width = new GridLength(wide ? 12 : 0);
            TaskColumn.Width = new GridLength(mode == WindowLayoutMode.Compact ? 1 : 1.2, GridUnitType.Star);
            NoteGap.Width = new GridLength(mode == WindowLayoutMode.Compact ? 0 : 12);
            NoteColumn.Width = mode == WindowLayoutMode.Compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            DateToolbar.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
            DateRail.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            TaskDateSubtitle.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            EnvironmentStatus.Visibility = mode == WindowLayoutMode.Compact ? Visibility.Collapsed : Visibility.Visible;
            ApplyHomeVisibility();
        }
        HeaderRow.Height = new GridLength(shortWindow ? 64 : 74);
        System.Windows.Shell.WindowChrome.GetWindowChrome(this).CaptionHeight = (shortWindow ? 64 : 74) + LayoutRoot.Margin.Top;
        BrandSubtitle.Visibility = shortWindow ? Visibility.Collapsed : Visibility.Visible;
        NoteSubtitle.Visibility = shortWindow ? Visibility.Collapsed : Visibility.Visible;
        TasksCard.Padding = TaskNotePanel.Padding = new Thickness(shortWindow ? 16 : _layoutMode == WindowLayoutMode.Wide ? 24 : 20);
        TaskDivider.Margin = NoteDivider.Margin = new Thickness(0, shortWindow ? 8 : 15, 0, shortWindow ? 8 : 15);
    }

    private void ApplyHomeVisibility()
    {
        var compact = _layoutMode == WindowLayoutMode.Compact;
        TaskContent.Visibility = compact && _compactDetails ? Visibility.Collapsed : Visibility.Visible;
        TaskNotePanel.Visibility = !compact || _compactDetails ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(TaskNotePanel, compact ? 2 : 4);
        Grid.SetColumnSpan(TaskNotePanel, compact ? 3 : 1);
        BackToTasksButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowCompactDetails()
    {
        if (_layoutMode != WindowLayoutMode.Compact || SelectedTask is null) return;
        _compactDetails = true; ApplyHomeVisibility();
    }
    private void BackToTasks_Click(object sender, RoutedEventArgs e)
    { _compactDetails = false; ApplyHomeVisibility(); }
    private void HomePage_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsCompactDetailOpen)
        { _compactDetails = false; ApplyHomeVisibility(); e.Handled = true; }
    }
    private void TaskList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_layoutMode != WindowLayoutMode.Compact) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != TaskList; node = VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase) return;
            if (node is ListBoxItem { DataContext: DiaryTask task })
            { SelectedTask = task; ShowCompactDetails(); return; }
        }
    }
    private void TaskList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    { if (e.Key == Key.Enter && _layoutMode == WindowLayoutMode.Compact) { ShowCompactDetails(); e.Handled = true; } }
    private void PreviousDay_Click(object sender, RoutedEventArgs e) => OpenDate(_selectedDate.AddDays(-1));
    private void NextDay_Click(object sender, RoutedEventArgs e) => OpenDate(_selectedDate.AddDays(1));

    private void OpenOverdue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: DiaryTask task }) ShowTask(task.Date, task.Id);
    }
    private void HomeTab_Click(object sender, RoutedEventArgs e) => ShowHomePage();
    private void CalendarTab_Click(object sender, RoutedEventArgs e) => ShowCalendarPage();
    private void SearchTab_Click(object sender, RoutedEventArgs e) => ShowSearchPage();
    private void CompletedTab_Click(object sender, RoutedEventArgs e) => ShowCompletedPage();
    private void SettingsTab_Click(object sender, RoutedEventArgs e) => ShowSettingsPage();

    private void ScreenshotTab_Click(object sender, RoutedEventArgs e)
        => ShowScreenshots();

    public void ShowScreenshots()
    {
        FlushEdits();
        ScreenshotTab.IsChecked = true;
        HomePage.Visibility = CalendarPage.Visibility = SearchPage.Visibility = CompletedPage.Visibility = SettingsPage.Visibility = Visibility.Collapsed;
        FocusView.Visibility = Visibility.Collapsed; ScreenshotGallery.Visibility = Visibility.Visible;
    }

    private void CalendarPrevious_Click(object sender, RoutedEventArgs e) => MoveCalendarMonth(-1);
    private void CalendarNext_Click(object sender, RoutedEventArgs e) => MoveCalendarMonth(1);

    private void MoveCalendarMonth(int offset)
    {
        CalendarDayOverlay.Visibility = Visibility.Collapsed;
        _calendarMonth = _calendarMonth.AddMonths(offset);
        _calendarSelectedDate = new DateTime(_calendarMonth.Year, _calendarMonth.Month,
            Math.Min(_calendarSelectedDate.Day, DateTime.DaysInMonth(_calendarMonth.Year, _calendarMonth.Month)));
        RefreshCalendar();
    }

    private void CalendarToday_Click(object sender, RoutedEventArgs e)
    {
        CalendarDayOverlay.Visibility = Visibility.Collapsed;
        _calendarSelectedDate = DateTime.Today;
        _calendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        RefreshCalendar();
    }

    private void CalendarAdd_Click(object sender, RoutedEventArgs e)
    {
        ShowHomePage();
        OpenDate(_calendarSelectedDate);
        Dispatcher.BeginInvoke(new Action(() => NewTaskBox.Focus()), DispatcherPriority.Input);
    }

    private void CalendarDay_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border { DataContext: CalendarDay day }) return;
        for (DependencyObject? current = e.OriginalSource as DependencyObject; current is not null && current != sender;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is System.Windows.Controls.Button) return;
        _calendarSelectedDate = day.Date;
        ShowHomePage();
        OpenDate(day.Date);
    }

    private void CalendarTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: CalendarEntry entry }) return;
        _calendarSelectedDate = entry.Date;
        ShowHomePage();
        OpenDate(entry.Date);
        SelectedTask = entry.TaskId is long id
            ? Tasks.FirstOrDefault(task => task.Id == id) ?? (_repository.FindTask(id) is { } row ? ResolveTask(row) : null)
            : Tasks.FirstOrDefault(task => task.RecurrenceRuleId == entry.RuleId);
        if (SelectedTask is not null) TaskList.ScrollIntoView(SelectedTask);
        CalendarDayOverlay.Visibility = Visibility.Collapsed;
        if (SelectedTask is not null && _layoutMode == WindowLayoutMode.Compact) ShowCompactDetails();
        e.Handled = true;
    }

    private void CalendarFilter_Click(object sender, RoutedEventArgs e)
    {
        _calendarFilter = sender == CalendarDoneFilter ? 2 : sender == CalendarPendingFilter ? 1 : 0;
        CalendarDayOverlay.Visibility = Visibility.Collapsed;
        RefreshCalendar();
    }
    private void CalendarMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: CalendarDay day }) return;
        _calendarExpandedDate = day.Date;
        SetCalendarDayContents(day);
        CalendarDayOverlay.Visibility = Visibility.Visible;
        e.Handled = true;
    }
    private void CalendarCloseDay_Click(object sender, RoutedEventArgs e) => CalendarDayOverlay.Visibility = Visibility.Collapsed;
    private void SetCalendarDayContents(CalendarDay day)
    {
        CalendarDayHeading.Text = $"{day.Date:M月d日} · {day.SummaryHint}";
        CalendarDayEntries.ItemsSource = day.Entries;
    }
    private void CalendarPage_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape || CalendarDayOverlay.Visibility != Visibility.Visible) return;
        CalendarDayOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }
    private void CalendarGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (CalendarDays.Count == 0) return;
        var rows = CalendarDays.Count / 7;
        var limit = Math.Max(1, (int)((e.NewSize.Height / rows - 43) / 22));
        if (limit == _calendarVisibleLimit) return;
        _calendarVisibleLimit = limit;
        RefreshCalendar();
    }

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
        PlanningChanged();
        _repository.SaveTask(live);
        if (live.IsShelvedOn(DateTime.Today) && live.Date <= DateTime.Today) _repository.SetDayShelved(live, DateTime.Today, false);
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
        if (task.CarryForward) { System.Windows.MessageBox.Show(this, "请先在日期与推进中关闭持续推进，再设置重复。", "小岛日记"); return; }
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
        _observedTasks.Add(task);
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
        PlanningChanged();
        Tasks.Remove(task);
        SelectedTask = Tasks.FirstOrDefault(task => !task.IsDone && !task.IsShelvedOn(_selectedDate));
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

        var dayWindow = new DailyTodoWindow(_selectedDate, Tasks, OpenTaskNote, SaveCompletionFromDaily, _repository);
        PlaceNearMain(dayWindow, Left + 205, Top + 80);
        _dayWindows.Add(_selectedDate, dayWindow);
        dayWindow.Closed += (_, _) => _dayWindows.Remove(dayWindow.Date);
        dayWindow.Show();
    }

    private void SaveCompletionFromDaily(DiaryTask task)
    {
        PlanningChanged();
        if (Tasks.Contains(task)) return;
        _repository.SaveTask(task);
        if (_selectedDate == DateTime.Today) RefreshOverdueTasks();
        _scheduler.Refresh();
        if (CompletedPage.Visibility == Visibility.Visible) ReloadCompleted();
        if (CalendarPage.Visibility == Visibility.Visible) RefreshCalendar();
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
        if(_focusStore is not null && _focus is not null) noteWindow.AttachFocus(_focusStore,_focus,OpenFocusTask);
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
