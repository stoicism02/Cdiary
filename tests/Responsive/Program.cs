using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cdiary;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static void Main()
    {
        var path = Path.Combine(Path.GetTempPath(), "CdiaryResponsiveTests", Guid.NewGuid().ToString("N"), "diary.db");
        var repository = new DiaryRepository(path);
        var task = repository.AddTask(DateTime.Today, "验证布局切换保留编辑内容");
        using var scheduler = new ReminderScheduler(repository, Dispatcher.CurrentDispatcher, _ => { });
        var window = new MainWindow(repository, scheduler);
        var root = (FrameworkElement)window.Content;
        T Find<T>(string name) where T : class => (T)window.FindName(name);
        void Layout(int width, int height = 720)
        {
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        }
        bool Visible(string name) => Find<UIElement>(name).Visibility == Visibility.Visible;
        Layout(1100);
        Check(((SolidColorBrush)window.FindResource("WindowSurface")).Color == (Color)ColorConverter.ConvertFromString("#F2F7F5"),
              "主窗口使用轻岛底色");
        var unchangedSticky = new TaskNoteWindow(task, repository);
        Check(((SolidColorBrush)unchangedSticky.FindResource("Accent")).Color == (Color)ColorConverter.ConvertFromString("#19C8B9")
              && !ReferenceEquals(window.FindResource("Pill"), unchangedSticky.FindResource("Pill")),
              "任务便签保留原有资源与按钮模板，不继承主窗口主题");
        Check(window.CurrentLayoutMode == WindowLayoutMode.Standard && Visible("DateToolbar") && !Visible("DateRail")
              && Visible("TaskContent") && Visible("TaskNotePanel"), "标准窗口为日期顶栏与双栏内容");
        var selected = window.SelectedTask;
        var editor = Find<MarkdownNoteView>("TaskNoteEditor");
        editor.Text = "尚在编辑的笔记\n第二行"; editor.CaretIndex = 5;
        Find<TextBox>("ReminderBox").Text = "09:35";
        Layout(1500, 900);
        Check(window.CurrentLayoutMode == WindowLayoutMode.Wide && !Visible("DateRail") && Visible("DateToolbar"), "宽屏保持水平日期栏，阅读空间不被日期柱占用");
        Layout(760, 580);
        Check(window.CurrentLayoutMode == WindowLayoutMode.Compact && Visible("TaskContent") && !Visible("TaskNotePanel"), "窄屏先展示待办与备忘");
        Check(Find<ListBox>("TaskList").ActualHeight >= 4 * 52 && !Find<Expander>("MemoExpander").IsExpanded,
              "760×580 窗口给四条任务保留足够高度，备忘默认折叠");
        Check(!Visible("OverdueList"), "之前未完成默认收起，不挤占当前清单");
        Check(ReferenceEquals(selected, window.SelectedTask) && ReferenceEquals(editor, Find<MarkdownNoteView>("TaskNoteEditor"))
              && editor.Text == "尚在编辑的笔记\n第二行" && editor.CaretIndex == 5 && Find<TextBox>("ReminderBox").Text == "09:35",
              "跨布局不重建任务与编辑器，保留正文、光标和未提交提醒时间");
        var list = Find<ListBox>("TaskList");
        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        var check = Descendants<CheckBox>(row).First();
        list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = check });
        Check(!window.IsCompactDetailOpen, "点击完成框的预览事件不会切入笔记");
        list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = row });
        Layout(760, 580);
        Check(window.IsCompactDetailOpen && Visible("TaskNotePanel") && !Visible("TaskContent") && Visible("BackToTasksButton"), "点任务进入窄屏笔记详情");
        Find<Button>("BackToTasksButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(!window.IsCompactDetailOpen && Visible("TaskContent"), "详情返回待办清单");
        window.ShowTask(DateTime.Today, task.Id); Layout(760, 580);
        Check(window.IsCompactDetailOpen && window.SelectedTask?.Id == task.Id, "外部定位任务会直接打开窄屏详情");
        foreach (var width in new[] { 999, 1099, 1100, 1399, 1400, 1920, 760, 1100 })
        {
            Layout(width, 580);
            var expected = width < 1100 ? WindowLayoutMode.Compact : width < 1400 ? WindowLayoutMode.Standard : WindowLayoutMode.Wide;
            Check(window.CurrentLayoutMode == expected, $"连续缩放 {width} DIP 的断点正确");
            foreach (var name in new[] { "TaskContent", "TaskNotePanel", "DateToolbar", "DateRail" })
            {
                var element = Find<FrameworkElement>(name);
                if (element.Visibility != Visibility.Visible) continue;
                var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                Check(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1,
                    $"{width} DIP 下 {name} 未超出窗口");
            }
        }
        Find<RadioButton>("SettingsTab").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout(760, 580);
        var settings = Find<ScreenshotSettingsView>("ScreenshotSettings");
        var days = (TextBox)settings.FindName("DaysBox"); days.Text = "12";
        var field = (FrameworkElement)settings.FindName("DaysField");
        Check(Grid.GetRow(field) == 3 && days.ActualHeight <= 34, "窄屏设置标签与输入上下排列，输入框保持紧凑");
        Layout(1500, 900);
        Check(Grid.GetRow(field) == 2 && days.Text == "12", "宽屏设置恢复横排且保留输入");
        window.FlushEdits();
        Check(repository.LoadTasks(DateTime.Today).Single().Note == "尚在编辑的笔记\n第二行", "缩放后笔记正常保存");
        var done = repository.AddTask(DateTime.Today, "已完成的记录"); done.IsDone = true; done.Note = "完成后也能查看的资料"; repository.SaveTask(done);
        for (var index = 0; index < 8; index++) repository.AddTask(DateTime.Today, $"月历任务 {index}");
        var past = repository.AddTask(DateTime.Today.AddDays(-1), "补完昨天的安排"); past.IsDone = true; repository.SaveTask(past);
        var history = repository.LoadCalendarEntries(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1));
        Check(history.Single(entry => entry.TaskId == past.Id && entry.IsDone) is { IsDone: true, State: "Done" } pastEntry
            && pastEntry.Date == DateTime.Today && pastEntry.CompletedAt?.Date == DateTime.Today,
            "补完过去任务在真实完成日展示绿色记录");
        Check(new CalendarEntry(DateTime.Today.AddDays(-1), "旧任务", 1, null, WasOverdue: true).State == "Overdue"
            && new CalendarEntry(DateTime.Today, "今天", 2, null).State == "Today"
            && new CalendarEntry(DateTime.Today.AddDays(1), "未来", 3, null).State == "Future", "区分逾期、今日与未来待办");
        Find<RadioButton>("CalendarTab").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout(760, 580);
        var todayCell = window.CalendarDays.Single(day => day.IsToday);
        Check(todayCell.Entries.Count == 11 && todayCell.Summary == "2/11" && todayCell.HasOverflow, "月历包含所有任务、每日完成统计与溢出入口");
        var more = Descendants<Button>(Find<Border>("CalendarPage")).First(button => button.DataContext == todayCell && Equals(button.Content, todayCell.OverflowLabel));
        more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout(760, 580);
        Check(Visible("CalendarDayOverlay") && Find<ItemsControl>("CalendarDayEntries").Items.Count == 11, "点击还有N项展开完整当天清单");
        var completedButton = Descendants<Button>(Find<ItemsControl>("CalendarDayEntries")).First(button => button.DataContext is CalendarEntry { TaskId: var id } && id == done.Id);
        completedButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout(760, 580);
        Check(window.SelectedTask?.Id == done.Id && window.IsCompactDetailOpen && Find<MarkdownNoteView>("TaskNoteEditor").Text == done.Note
            && window.SelectedTask.CompletionLabel.Contains("已完成"), "窄窗口打开已完成任务的笔记与完成时间");
        Find<RadioButton>("CalendarTab").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Find<RadioButton>("CalendarDoneFilter").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout(760, 580);
        Check(window.CalendarDays.SelectMany(day => day.Entries).All(entry => entry.IsDone)
            && window.CalendarDays.Single(day => day.IsToday).Summary == "2/11", "已完成筛选不改变每日完整统计");
        Find<RadioButton>("CalendarPendingFilter").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(window.CalendarDays.Single(day => day.IsToday).Entries.Count == 9 && window.CalendarDays.SelectMany(day => day.Entries).All(entry => !entry.IsDone), "未完成筛选保留待办");
        Find<RadioButton>("CalendarAllFilter").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(window.CalendarDays.Single(day => day.IsToday).Entries.Count == 11, "全部筛选恢复完整月历");
        Layout(760, 580);
        todayCell = window.CalendarDays.Single(day => day.IsToday);
        Descendants<Button>(Find<Border>("CalendarPage")).First(button => button.DataContext == todayCell && Equals(button.Content, todayCell.OverflowLabel))
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var liveTask = window.Tasks.First(item => !item.IsDone);
        liveTask.IsDone = true;
        Check(window.CalendarDays.Single(day => day.IsToday).Summary == "3/11"
            && Find<ItemsControl>("CalendarDayEntries").Items.Cast<CalendarEntry>().Single(entry => entry.TaskId == liveTask.Id).IsDone,
            "其他入口完成任务时月历与已展开清单同步更新");
        liveTask.IsDone = false;
        Check(window.CalendarDays.Single(day => day.IsToday).Summary == "2/11", "恢复待办后月历统计同步恢复");
        var stickyTasks = new System.Collections.ObjectModel.ObservableCollection<DiaryTask>();
        for (var index = 0; index < 8; index++)
        {
            var item = repository.AddTask(DateTime.Today.AddDays(1), index == 5
                ? "准备面试（八股文：redis、回顾计网、mysql），整理错题并复习常见问题"
                : $"第 {index + 1} 项任务：整理简历与复习资料");
            item.IsDone = index < 5; item.ReminderTime = new TimeSpan(18, 30, 0); stickyTasks.Add(item);
        }
        var daily = new DailyTodoWindow(DateTime.Today.AddDays(1), stickyTasks, _ => { }, _ => { });
        var dailyRoot = (FrameworkElement)daily.Content;
        void LayoutDaily(int width, int height)
        { dailyRoot.Measure(new Size(width, height)); dailyRoot.Arrange(new Rect(0, 0, width, height)); dailyRoot.UpdateLayout(); }
        LayoutDaily(365, 480);
        var dailyList = (ListBox)daily.FindName("TodoList");
        var dailyScroll = Descendants<ScrollViewer>(dailyList).First();
        dailyScroll.ScrollToHorizontalOffset(100);
        foreach (var size in new[] { (285, 480), (365, 480), (600, 600), (285, 290) })
        {
            LayoutDaily(size.Item1, size.Item2);
            dailyScroll.ScrollToHorizontalOffset(100); LayoutDaily(size.Item1, size.Item2);
            // Virtualized, offscreen rows can retain an old width estimate after resizing.
            // Verify horizontal scrolling is disabled and an actual scroll request cannot move the content.
            Check(dailyScroll.ComputedHorizontalScrollBarVisibility != Visibility.Visible
                && !Descendants<VirtualizingStackPanel>(dailyList).First().CanHorizontallyScroll && dailyScroll.HorizontalOffset == 0,
                $"当日便签 {size.Item1}×{size.Item2} 长任务不产生横向滚动");
            var firstRow = (ListBoxItem)dailyList.ItemContainerGenerator.ContainerFromIndex(0);
            var taskCheck = Descendants<CheckBox>(firstRow).First();
            var bounds = taskCheck.TransformToAncestor(dailyList).TransformBounds(new Rect(taskCheck.RenderSize));
            Check(bounds.Left >= 0 && bounds.Right <= dailyList.ActualWidth && firstRow.ActualWidth <= dailyScroll.ViewportWidth + 1,
                $"当日便签 {size.Item1}×{size.Item2} 保持勾选框和任务卡片在视口内");
        }
        Check(dailyScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible && dailyScroll.ScrollableHeight > 0,
            "当日便签缩矮后出现正常的纵向滚动条");
        dailyScroll.ScrollToEnd(); LayoutDaily(285, 290);
        Check(dailyScroll.VerticalOffset > 0 && dailyScroll.HorizontalOffset == 0, "当日便签可以上下翻阅且不会横移");
        window.ShowTask(DateTime.Today, task.Id); Layout(760, 580);
        Find<Button>("BackToTasksButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout(760, 580);
        var completedRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(window.Tasks.Single(t => t.Id == task.Id));
        var completionCheck = Descendants<CheckBox>(completedRow).First();
        completionCheck.IsChecked = true;
        completionCheck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(Visible("CompletionNotice") && repository.FindTask(task.Id)!.IsDone, "完成任务保存后显示可恢复提示");
        Find<Button>("RestoreNoticeButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(!Visible("CompletionNotice") && !repository.FindTask(task.Id)!.IsDone, "提示中的恢复调用现有恢复流程并写回待办");
        Console.WriteLine($"响应式布局：{_checks} 项检查通过，未显示窗口。");
    }
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); _checks++; Console.WriteLine($"通过：{name}"); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
