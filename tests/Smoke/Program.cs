using Cdiary;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Media;
using Microsoft.Data.Sqlite;

var path = Path.Combine(Path.GetTempPath(), "CdiarySmoke", Guid.NewGuid().ToString("N"), "test.db");
var repository = new DiaryRepository(path);
var date = new DateTime(2026, 9, 26);

void Check(bool condition, string label)
{
    if (!condition) throw new Exception($"失败：{label}");
    Console.WriteLine($"通过：{label}");
}

System.Windows.Controls.Button? FindRestoreButton(DependencyObject root)
{
    if (root is System.Windows.Controls.Button { Content: "恢复待办" } button) return button;
    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        if (FindRestoreButton(VisualTreeHelper.GetChild(root, index)) is { } found) return found;
    return null;
}

System.Windows.Controls.CheckBox? FindTaskCheckBox(DependencyObject root, long id)
{
    if (root is System.Windows.Controls.CheckBox { DataContext: DiaryTask task } check && task.Id == id)
        return check;
    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        if (FindTaskCheckBox(VisualTreeHelper.GetChild(root, index), id) is { } found) return found;
    return null;
}

System.Windows.Controls.Button? FindCalendarTaskButton(DependencyObject root, long id)
{
    if (root is System.Windows.Controls.Button { DataContext: CalendarEntry { TaskId: var taskId } } button && taskId == id)
        return button;
    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        if (FindCalendarTaskButton(VisualTreeHelper.GetChild(root, index), id) is { } found) return found;
    return null;
}

Check(repository.LoadTasks(date).Count == 0, "初始任务为空");
Check(repository.NextReminder(date) is null, "初始无提醒");

var task = repository.AddTask(date, "写计划");
task.Note = "常用链接与备注";
task.ReminderTime = new TimeSpan(9, 0, 0);
repository.SaveTask(task);
repository.SaveDayMemo(date, "今天的备忘");

var reopened = new DiaryRepository(path);
var loaded = reopened.LoadTasks(date).Single();
Check(loaded.Id == task.Id && loaded.Title == "写计划" && loaded.Note == "常用链接与备注", "任务重启后恢复");
Check(reopened.LoadDayMemo(date) == "今天的备忘", "当天备忘重启后恢复");
Check(reopened.NextReminder(date.AddHours(8)) == date.AddHours(9), "找到下一次提醒");
Check(reopened.DueReminders(date.AddHours(10)).Count == 1, "逾时任务待提醒");

reopened.MarkNotified([task.Id], date.AddHours(10));
Check(reopened.DueReminders(date.AddHours(10)).Count == 0, "已提醒任务不重复提醒");
loaded.Title = "修改标题";
reopened.SaveTask(loaded);
Check(reopened.DueReminders(date.AddHours(10)).Count == 0, "编辑标题不重置提醒状态");
loaded.ReminderTime = new TimeSpan(8, 0, 0);
reopened.SaveTask(loaded);
Check(reopened.DueReminders(date.AddHours(10)).Count == 1, "修改提醒时间后可重新提醒");
loaded.IsDone = true;
reopened.SaveTask(loaded);
Check(reopened.DueReminders(date.AddHours(10)).Count == 0, "完成任务不会提醒");
loaded.Note = "常用链接 50%_进度";
reopened.SaveTask(loaded);
Check(reopened.Search("链接").Any(result => result.TaskId == task.Id), "搜索中文任务笔记");
Check(reopened.Search("备忘").Any(result => result.TaskId is null), "搜索每日备忘");
Check(reopened.Search("%_").Count == 1 && reopened.Search("%_")[0].TaskId == task.Id,
    "搜索百分号与下划线按原字符匹配");
Check(reopened.Search("不存在").Count == 0, "无匹配搜索返回空结果");
reopened.DeleteTask(task.Id);
Check(reopened.LoadTasks(date).Count == 0, "删除任务");

var repeatPath = Path.Combine(Path.GetTempPath(), "CdiarySmoke", Guid.NewGuid().ToString("N"), "repeat.db");
var repeatRepository = new DiaryRepository(repeatPath);
var start = DateTime.Today;
var templateTask = repeatRepository.AddTask(start, "每天喝水");
templateTask.Note = "先喝一杯温水";
repeatRepository.SaveTask(templateTask);
var dailyId = repeatRepository.SaveRule(templateTask, false, 0, "每天喝水", "先喝一杯温水", new TimeSpan(9, 15, 0));
Check(repeatRepository.EnsureOccurrences(start.AddDays(1)) == 1
    && repeatRepository.EnsureOccurrences(start.AddDays(1)) == 0, "每日规则只生成一次");
var tomorrow = repeatRepository.LoadTasks(start.AddDays(1)).Single();
Check(tomorrow.RecurrenceRuleId == dailyId && tomorrow.Note == "先喝一杯温水"
    && tomorrow.ReminderAt == start.AddDays(1).AddHours(9).AddMinutes(15), "重复任务复制模板和提醒");
tomorrow.IsDone = true;
tomorrow.Note = "这天已经完成";
repeatRepository.SaveTask(tomorrow);
repeatRepository.SaveRule(templateTask, false, 0, "每天散步", "出门十分钟", null);
repeatRepository.EnsureOccurrences(start.AddDays(2));
Check(repeatRepository.LoadTasks(start.AddDays(1)).Single().Note == "这天已经完成"
    && repeatRepository.LoadTasks(start.AddDays(2)).Single().Title == "每天散步", "更新规则不覆盖已有记录");
var previewDay = start.AddDays(4);
var projected = repeatRepository.LoadCalendarEntries(previewDay, previewDay.AddDays(1)).Single();
Check(projected.IsProjected && projected.RuleId == dailyId && repeatRepository.LoadTasks(previewDay).Count == 0,
    "月历预览重复任务而不写入数据库");
repeatRepository.EnsureOccurrences(previewDay);
var materialized = repeatRepository.LoadCalendarEntries(previewDay, previewDay.AddDays(1)).Single();
Check(materialized.TaskId is not null && !materialized.IsProjected, "打开日期后月历使用已生成任务");
var completedPreviewTask = repeatRepository.LoadTasks(previewDay).Single();
completedPreviewTask.IsDone = true;
repeatRepository.SaveTask(completedPreviewTask);
Check(repeatRepository.LoadCalendarEntries(previewDay, previewDay.AddDays(1)).Count == 0,
    "已完成任务不显示在月历待办中");
repeatRepository.DeleteTask(completedPreviewTask.Id);
Check(repeatRepository.LoadCalendarEntries(previewDay, previewDay.AddDays(1)).Count == 0,
    "删除的重复任务不在月历重新预览");
repeatRepository.DeleteTask(tomorrow.Id);
Check(repeatRepository.EnsureOccurrences(start.AddDays(1)) == 0, "删掉单日任务不会重新生成");
repeatRepository.StopRule(dailyId, start.AddDays(2));
Check(repeatRepository.EnsureOccurrences(start.AddDays(3)) == 0, "停止规则后不再生成");
var weeklyTask = repeatRepository.AddTask(start, "每周检查");
var nextDay = start.AddDays(1);
repeatRepository.SaveRule(weeklyTask, true, 1 << (int)nextDay.DayOfWeek, "每周检查", "", null);
Check(repeatRepository.EnsureOccurrences(nextDay) == 1
    && repeatRepository.EnsureOccurrences(nextDay.AddDays(1)) == 0, "每周规则只在选定星期生成");
Check(StartupSettings.BuildCommand(@"C:\Program Files\Cdiary\Cdiary.exe")
    == "\"C:\\Program Files\\Cdiary\\Cdiary.exe\" --background", "自启命令正确引用路径和后台参数");

var legacyPath = Path.Combine(Path.GetTempPath(), "CdiarySmoke", Guid.NewGuid().ToString("N"), "legacy.db");
Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
using (var oldConnection = new SqliteConnection($"Data Source={legacyPath}"))
{
    oldConnection.Open();
    using var command = oldConnection.CreateCommand();
    command.CommandText = "CREATE TABLE tasks (id INTEGER PRIMARY KEY AUTOINCREMENT, task_date TEXT NOT NULL, title TEXT NOT NULL, note TEXT NOT NULL DEFAULT '', completed INTEGER NOT NULL DEFAULT 0, reminder_at TEXT, notified_at TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); INSERT INTO tasks(task_date,title,note,completed,created_at,updated_at) VALUES('2026-09-26','旧任务','保留笔记',0,'2026-09-26T00:00:00.0000000','2026-09-26T00:00:00.0000000'); INSERT INTO tasks(task_date,title,note,completed,created_at,updated_at) VALUES('2026-09-26','旧完成任务','旧记录',1,'2026-09-26T00:00:00.0000000','2026-09-26T09:00:00.0000000');";
    command.ExecuteNonQuery();
}
var migrated = new DiaryRepository(legacyPath);
Check(migrated.LoadTasks(date).Single(task => task.Title == "旧任务").Note == "保留笔记", "旧数据库迁移后保留任务和笔记");
Check(migrated.LoadCompletedTasks().Single().CompletedAt == date.AddHours(9), "旧完成任务迁移后保留完成日期");

var notePath = Path.Combine(Path.GetTempPath(), "CdiarySmoke", Guid.NewGuid().ToString("N"), "note.db");
var noteRepository = new DiaryRepository(notePath);
var noteTask = noteRepository.AddTask(date, "便签任务");
Exception? noteError = null;
var dayReady = false;
var pinReady = false;
var searchNavigationReady = false;
var completionFlowReady = false;
var stickyFilterReady = false;
var calendarNavigationReady = false;
var calendarAddReady = false;
var noteThread = new Thread(() =>
{
    try
    {
        var noteWindow = new TaskNoteWindow(noteTask, noteRepository);
        noteTask.Note = "便签中的内容";
        noteWindow.FlushEdits();
        var sharedTasks = new ObservableCollection<DiaryTask> { noteTask };
        var dayWindow = new DailyTodoWindow(date, sharedTasks, _ => { }, _ => { });
        noteTask.IsDone = true;
        dayReady = ReferenceEquals(dayWindow.Tasks, sharedTasks) && dayWindow.ProgressLabel == "已完成 1 / 1";
        stickyFilterReady = !dayWindow.ActiveTasks.Cast<DiaryTask>().Any();
        noteWindow.SetPinned(true);
        var notePin = (ToggleButton)noteWindow.FindName("PinToggle")!;
        notePin.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
        var dayPin = (ToggleButton)dayWindow.FindName("PinToggle")!;
        dayPin.IsChecked = true;
        dayPin.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
        pinReady = notePin.IsChecked == true && dayWindow.Topmost;
        using var noteScheduler = new ReminderScheduler(noteRepository, Dispatcher.CurrentDispatcher, _ => { });
        var activeTask = noteRepository.AddTask(DateTime.Today, "完成后收纳");
        var mainWindow = new MainWindow(noteRepository, noteScheduler);
        var visibleBefore = mainWindow.ActiveTasks.Cast<DiaryTask>().Any(task => task.Id == activeTask.Id);
        var mainRoot = (FrameworkElement)mainWindow.Content;
        mainRoot.Measure(new Size(1100, 720));
        mainRoot.Arrange(new Rect(0, 0, 1100, 720));
        mainRoot.UpdateLayout();
        var taskCheck = FindTaskCheckBox(mainRoot, activeTask.Id);
        if (taskCheck is not null)
        {
            taskCheck.IsChecked = true;
            taskCheck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        var hiddenAfter = !mainWindow.ActiveTasks.Cast<DiaryTask>().Any(task => task.Id == activeTask.Id)
            && noteRepository.LoadCompletedTasks().Any(task => task.Id == activeTask.Id);
        ((RadioButton)mainWindow.FindName("CompletedTab")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var grouped = mainWindow.CompletedGroups.Any(group => group.Tasks.Any(task => task.Id == activeTask.Id));
        var completedRoot = (FrameworkElement)mainWindow.Content;
        completedRoot.Measure(new Size(1100, 720));
        completedRoot.Arrange(new Rect(0, 0, 1100, 720));
        completedRoot.UpdateLayout();
        var restore = FindRestoreButton(completedRoot);
        restore?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        completionFlowReady = visibleBefore && taskCheck is not null && hiddenAfter && grouped && restore is not null
            && mainWindow.ActiveTasks.Cast<DiaryTask>().Any(task => task.Id == activeTask.Id)
            && !noteRepository.LoadCompletedTasks().Any(task => task.Id == activeTask.Id);
        ((RadioButton)mainWindow.FindName("CalendarTab")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var monthReady = ((UIElement)mainWindow.FindName("CalendarPage")!).Visibility == Visibility.Visible
            && mainWindow.CalendarDays.Count is 35 or 42
            && mainWindow.CalendarDays.Any(day => day.Date == DateTime.Today
                && day.VisibleEntries.Any(entry => entry.TaskId == activeTask.Id));
        ((System.Windows.Controls.Button)mainWindow.FindName("CalendarNextButton")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var nextMonth = mainWindow.CalendarMonthLabel == DateTime.Today.AddMonths(1).ToString("yyyy年M月", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
        ((System.Windows.Controls.Button)mainWindow.FindName("CalendarTodayButton")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        mainRoot.UpdateLayout();
        var calendarButton = FindCalendarTaskButton(mainRoot, activeTask.Id);
        calendarButton?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        calendarNavigationReady = monthReady && nextMonth && calendarButton is not null
            && ((UIElement)mainWindow.FindName("HomePage")!).Visibility == Visibility.Visible
            && mainWindow.SelectedTask?.Id == activeTask.Id;
        ((RadioButton)mainWindow.FindName("CalendarTab")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        ((System.Windows.Controls.Button)mainWindow.FindName("CalendarNextButton")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        ((System.Windows.Controls.Button)mainWindow.FindName("CalendarAddButton")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var nextDate = DateTime.Today.AddMonths(1);
        calendarAddReady = ((UIElement)mainWindow.FindName("HomePage")!).Visibility == Visibility.Visible
            && mainWindow.SelectedDateLabel.StartsWith($"{nextDate.Month}月{nextDate.Day}日");
        var searchResult = noteRepository.Search("便签任务").Single();
        mainWindow.SearchResults.Add(searchResult);
        var root = (FrameworkElement)mainWindow.Content;
        root.Measure(new Size(1100, 720));
        root.Arrange(new Rect(0, 0, 1100, 720));
        root.UpdateLayout();
        ((ListBox)mainWindow.FindName("SearchResultsList")!).SelectedItem = searchResult;
        searchNavigationReady = mainWindow.SelectedTask?.Id == noteTask.Id
            && ((UIElement)mainWindow.FindName("HomePage")!).Visibility == Visibility.Visible;
    }
    catch (Exception error) { noteError = error; }
});
noteThread.SetApartmentState(ApartmentState.STA);
noteThread.Start();
Check(noteThread.Join(TimeSpan.FromSeconds(5)) && noteError is null
    && noteRepository.LoadTasks(date).Single().Note == "便签中的内容", "便签窗口加载并保存共享笔记");
Check(dayReady, "当日便签加载共享任务并更新进度");
Check(stickyFilterReady, "完成任务从当日便签清单移走");
Check(completionFlowReady, "完成任务移入已完成并可恢复到待办");
Check(calendarNavigationReady, "月历切换月份并定位到任务");
Check(calendarAddReady, "月历添加入口定位到指定月份的日期");
Check(pinReady, "图钉状态与窗口置顶同步");
Check(searchNavigationReady, "搜索结果定位到任务");

var clockPath = Path.Combine(Path.GetTempPath(), "CdiarySmoke", Guid.NewGuid().ToString("N"), "clock.db");
var clockRepository = new DiaryRepository(clockPath);
var trigger = DateTime.Now.AddSeconds(2);
if (trigger.Date == DateTime.Today)
{
    var clockTask = clockRepository.AddTask(DateTime.Today, "计时提醒");
    clockTask.ReminderTime = trigger.TimeOfDay;
    clockRepository.SaveTask(clockTask);
    var delivered = 0;
    var thread = new Thread(() =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var scheduler = new ReminderScheduler(clockRepository, dispatcher, due =>
        {
            delivered += due.Count;
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
        });
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timeout.Tick += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
        timeout.Start();
        scheduler.Start();
        Dispatcher.Run();
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Check(thread.Join(TimeSpan.FromSeconds(7)) && delivered == 1, "到点触发一次提醒");
    Check(clockRepository.DueReminders(DateTime.Now).Count == 0, "到点提醒后记录已送达");
}
