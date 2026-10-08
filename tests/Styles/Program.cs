using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Cdiary;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("请指定 SharedStyles.xaml 路径。");
        Exception? failure = null;
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await Run(Path.GetFullPath(args[0])); }
            catch (Exception error) { failure = error; }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
        }));
        Dispatcher.Run();
        if (failure is not null) throw new Exception("样式热加载验证失败", failure);
        Console.WriteLine($"{_checks} 项样式验证通过；未显示窗口、未修改正式数据或源样式。");
    }
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        _checks++; Console.WriteLine($"通过：{label}");
    }
    private static async Task WaitFor(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(8);
        while (!predicate() && DateTime.UtcNow < timeout) await Task.Delay(40);
        if (!predicate()) throw new TimeoutException("样式监听没有按预期完成。");
    }
    private static async Task Run(string source)
    {
        var root = Path.Combine(Path.GetTempPath(), "CdiaryStyleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "SharedStyles.xaml");
        var original = await File.ReadAllTextAsync(source); await File.WriteAllTextAsync(path, original);
        var repository = DevelopmentPreview.OpenRepository(Path.Combine(root, "data"));
        Check(repository.DatabasePath.StartsWith(root) && repository.LoadTasks(DateTime.Today).Count == 3,
            "开发预览创建独立示例数据");
        var task = repository.LoadTasks(DateTime.Today)[0];
        using var scheduler = new ReminderScheduler(repository, Dispatcher.CurrentDispatcher, _ => { });
        var main = new MainWindow(repository, scheduler);
        main.ConfigureDevelopmentPreview();
        Check(!((CheckBox)main.FindName("AutostartCheck")).IsEnabled, "开发预览禁止修改开机自启");
        var note = new TaskNoteWindow(task, repository);
        var daily = new DailyTodoWindow(DateTime.Today, main.Tasks, _ => { }, _ => { });
        Layout(note); Layout(daily); Layout(main);
        var noteRoot = note.Content;
        var close = Descendants<Button>((DependencyObject)note.Content).Single(button => Equals(button.ToolTip, "关闭便签"));
        var dailyClose = Descendants<Button>((DependencyObject)daily.Content).Single(button => Equals(button.ToolTip, "关闭便签"));
        var editor = (MarkdownNoteView)note.FindName("NoteEditor");
        editor.Text = "正在输入的笔记，不要丢失。热加载后继续编辑。";
        editor.Select(4, 3);
        var originalText = editor.Text;
        Check(close.Width == 30 && close.Height == 30, "正式内嵌样式保留圆润关闭按钮");
        using var reload = new StyleHotReload(path, Dispatcher.CurrentDispatcher);
        Check(reload.LastReloadSucceeded, "外部共享样式首次加载：" + reload.Status);
        var changed = original.Replace("<Setter Property=\"Width\" Value=\"30\"/>", "<Setter Property=\"Width\" Value=\"38\"/>")
            .Replace("<Setter Property=\"Height\" Value=\"30\"/>", "<Setter Property=\"Height\" Value=\"38\"/>")
            .Replace("Color=\"#19C8B9\"", "Color=\"#70A080\"");
        var revision = reload.Revision;
        File.WriteAllText(path, changed);
        await WaitFor(() => reload.Revision > revision);
        Layout(note); Layout(daily); Layout(main);
        Check(close.Width == 38 && close.Height == 38 && dailyClose.Width == 38,
            "文件保存自动刷新两个已有便签的按钮尺寸");
        Check(ReferenceEquals(note.Content, noteRoot) && ReferenceEquals(note.Task, task)
            && editor.Text == originalText && task.Note == originalText, "热加载不重建窗口、不丢失正在编辑的笔记");
        var primary = Descendants<Button>((DependencyObject)main.Content).First(button => ReferenceEquals(button.Style, main.FindResource("PrimaryPill")));
        Check(primary.Background is SolidColorBrush brush && brush.Color == Color.FromRgb(0x70, 0xA0, 0x80),
            "主操作按钮动态使用新的主题色");
        var later = new TaskNoteWindow(task, repository); Layout(later);
        Check(Descendants<Button>((DependencyObject)later.Content).Single(button => Equals(button.ToolTip, "关闭便签")).Width == 38,
            "后打开的窗口使用最新样式");

        revision = reload.Revision;
        File.WriteAllText(path, "<ResourceDictionary><broken>");
        await WaitFor(() => !reload.LastReloadSucceeded);
        Check(reload.Revision == revision && close.Width == 38 && editor.Text == originalText,
            "无效 XAML 保留上一个有效样式和编辑内容");
        File.WriteAllText(path, changed.Replace("x:Key=\"NoteCloseButton\"", "x:Key=\"MissingCloseButton\""));
        Check(!reload.ReloadNow() && reload.Status.Contains("NoteCloseButton") && close.Width == 38,
            "删除必需资源时拒绝应用整批样式");
        File.WriteAllText(path, changed.Replace("x:Key=\"NoteTitle\" TargetType=\"TextBlock\"", "x:Key=\"NoteTitle\" TargetType=\"TextBox\""));
        Check(!reload.ReloadNow() && reload.Status.Contains("NoteTitle") && close.Width == 38,
            "控件类型不匹配时拒绝应用，避免现有窗口异常");
        File.WriteAllText(path, changed.Replace("<Setter Property=\"Height\" Value=\"38\"/>", "<Setter Property=\"Height\" Value=\"-5\"/>"));
        Check(!reload.ReloadNow() && close.Height == 38, "模板探测拒绝无效控件尺寸");

        var replacement = Path.Combine(root, "saved.tmp");
        File.WriteAllText(replacement, original); File.Move(replacement, path, true);
        await WaitFor(() => reload.Revision > revision && reload.LastReloadSucceeded);
        Check(close.Width == 30 && close.Height == 30, "编辑器原子替换文件后自动恢复");
        revision = reload.Revision;
        for (var i = 0; i < 5; i++) File.WriteAllText(path, changed);
        await WaitFor(() => reload.Revision > revision);
        await Task.Delay(500);
        Check(reload.Revision == revision + 1, "连续保存事件合并为一次样式更新");

        var isolatedWidth = 0d; Exception? isolatedError = null;
        var thread = new Thread(() =>
        {
            try
            {
                var isolated = new TaskNoteWindow(new DiaryTask(0, DateTime.Today, "独立窗口", "", false, null, null), repository); Layout(isolated);
                isolatedWidth = Descendants<Button>((DependencyObject)isolated.Content).Single(button => Equals(button.ToolTip, "关闭便签")).Width;
            }
            catch (Exception error) { isolatedError = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(5)) && isolatedError is null && isolatedWidth == 30,
            "未启用开发模式的独立作用域只使用内嵌样式");
        reload.Dispose(); revision = reload.Revision;
        File.WriteAllText(path, original); await Task.Delay(600);
        Check(reload.Revision == revision && close.Width == 38, "退出热加载后停止文件监听");
        note.FlushEdits();
        Check(repository.LoadTasks(task.Date).Single(item => item.Id == task.Id).Note == originalText,
            "热加载后笔记仍能正常保存");
    }
    private static void Layout(Window window)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(window.Width, window.Height));
        root.Arrange(new Rect(0, 0, window.Width, window.Height)); root.UpdateLayout();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
}
