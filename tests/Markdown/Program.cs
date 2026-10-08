using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using Cdiary;

internal static class Program
{
    private static int _checks;
    private const string Sample = """
        # 面试复习小记

        今天重点复习 **Redis**，把 *容易忘记的地方* 留下来。

        ## 要做的事
        - 整理缓存击穿与雪崩
        - 复习 MySQL 索引

        > 先讲清楚问题，再解释取舍。

        [常用资料](https://example.com) · 命令：`GET key`

        ```sql
        SELECT title FROM tasks;
        ```

        | 内容 | 进度 |
        | --- | --- |
        | Redis | 已复习 |
        | MySQL | 明天继续 |
        """;
    [STAThread]
    private static void Main(string[] args)
    {
        Exception? failure = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try { await Run(args); } catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        Dispatcher.Run();
        if (failure is not null) throw new Exception("Markdown 检查失败", failure);
        Console.WriteLine($"Markdown：{_checks} 项检查通过，未显示窗口、未打开外部链接。");
    }
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); _checks++; Console.WriteLine("通过：" + label); }
    private static async Task Run(string[] args)
    {
        var repository = new DiaryRepository(Path.Combine(Path.GetTempPath(), "CdiaryMarkdownTests", Guid.NewGuid().ToString("N"), "diary.db"));
        var first = repository.AddTask(DateTime.Today, "面试复习"); first.Note = "已有笔记\r\n第二行"; repository.SaveTask(first);
        var second = repository.AddTask(DateTime.Today, "另一任务"); second.Note = "另一份笔记"; repository.SaveTask(second);
        var note = new TaskNoteWindow(first, repository);
        var view = (MarkdownNoteView)note.FindName("NoteEditor");
        var editor = (TextEditor)view.FindName("Editor");
        Layout((FrameworkElement)note.Content, 365, 480);
        Check(view.Text == first.Note && editor.SyntaxHighlighting is not null, "读取旧笔记原文并启用语法高亮");
        view.Text="## 旧标题\r\n正文"; view.Select(0,view.Text.Length); view.ApplyHeading(3);
        Check(view.Text=="### 旧标题\r\n### 正文","源码标题转换替换已有级别并保留 CRLF");
        view.ApplyHeading(0);
        Check(view.Text=="旧标题\r\n正文","源码恢复正文移除标题标记");
        editor.Undo(); Check(view.Text=="### 旧标题\r\n### 正文","源码标题转换支持一次撤销");
        view.Text="已有笔记\r\n第二行";
        view.SetMode(NoteViewMode.Preview);
        Check(view.RenderCount == 1 && view.Text == "已有笔记\r\n第二行", "预览不会改写旧笔记或换行");
        foreach (var markdown in new[] { "# 标题", "**加粗**和*斜体*", "- 第一项\n- 第二项", "> 引用文字", "[资料](https://example.com)", "`GET key`", "```sql\nSELECT 1;\n```", "| A | B |\n| --- | --- |\n| 1 | 2 |" })
        {
            view.Text = markdown;
            var before = view.RenderCount; view.RenderPreview();
            var status = (TextBlock)view.FindName("Status");
            Check(view.RenderCount == before + 1 && status.Visibility == Visibility.Collapsed, "排版成功：" + markdown.Split('\n')[0] + (status.ToolTip is null ? "" : " / " + status.ToolTip));
        }
        Check(Descendants<Table>(view.PreviewDocument!).Any(), "表格渲染为原生表格");
        view.Text = "```text\n" + new string('x', 200) + "\n```"; view.RenderPreview();
        Check(!Descendants<TextEditor>(view.PreviewDocument!).Any()
            && new TextRange(view.PreviewDocument!.ContentStart, view.PreviewDocument.ContentEnd).Text.Contains(new string('x', 200)),
            "长代码块保留全文并使用流式正文，避免嵌套编辑器和横向滚动");
        view.SetMode(NoteViewMode.Edit); view.Text = "复习缓存"; view.Select(0, 4); view.ApplyFormat("bold");
        Check(first.Note == "**复习缓存**" && editor.CanUndo, "格式按钮修改原文并同步任务");
        editor.Undo(); Check(first.Note == "复习缓存", "一次撤销恢复格式操作之前的内容");
        editor.Redo(); Check(first.Note == "**复习缓存**", "重做恢复格式");
        view.Text = "一\r\n二"; view.Select(0, view.Text.Length); view.ApplyFormat("list");
        Check(view.Text.Replace("\r\n", "\n") == "- 一\n- 二", "多行选择生成列表");
        editor.Undo(); Check(view.Text == "一\r\n二", "列表撤销保留原始换行");
        view.Text = "保留光标"; view.CaretIndex = 3; var oldDocument = editor.Document;
        view.Task = second; view.Task = first;
        Check(ReferenceEquals(editor.Document, oldDocument) && view.CaretIndex == 3, "切换任务恢复原编辑文档和光标");
        var sibling = new TaskNoteWindow(first, repository);
        var siblingView = (MarkdownNoteView)sibling.FindName("NoteEditor");
        Layout((FrameworkElement)sibling.Content, 365, 480);
        view.Text = "中文输入内容与 emoji 🌿";
        Check(siblingView.Text == first.Note, "两个窗口共享同一任务笔记");
        siblingView.Text += "\n便签补充";
        Check(view.Text == siblingView.Text, "从另一便签编辑时原窗口同步");
        await Task.Delay(700);
        Check(repository.LoadTasks(DateTime.Today).Single(task => task.Id == first.Id).Note == view.Text, "编辑后自动保存 Markdown 原文");
        note.FlushEdits(); sibling.FlushEdits();
        Check(repository.LoadTasks(DateTime.Today).Single(task => task.Id == first.Id).Note == view.Text, "Markdown 原文保存后可重新读取");
        Check(repository.LoadTasks(DateTime.Today).Single(task => task.Id == second.Id).Note == "另一份笔记", "编辑任务不会覆盖其他任务笔记");
        Check(MarkdownNoteView.IsWebLink("https://example.com") && !MarkdownNoteView.IsWebLink("file:///C:/test.exe") && !MarkdownNoteView.IsWebLink("javascript:alert(1)"), "链接只通过网页浏览入口打开");
        view.SetMode(NoteViewMode.Edit); var count = view.RenderCount;
        view.Text = Sample; await Task.Delay(450);
        Check(view.RenderCount == count, "编辑模式不额外渲染预览");
        Layout((FrameworkElement)note.Content, 820, 720); view.SetMode(NoteViewMode.Split);
        Check(((Border)view.FindName("EditorFrame")).Visibility == Visibility.Visible && ((Border)view.FindName("PreviewFrame")).Visibility == Visibility.Visible, "宽笔记区域支持并排");
        var document = editor.Document; view.Select(5, 2);
        Layout((FrameworkElement)note.Content, 260, 400);
        Check(((Border)view.FindName("PreviewFrame")).Visibility == Visibility.Collapsed && ReferenceEquals(document, editor.Document) && editor.SelectionLength == 2,
            "窄便签自动收起并排且保留选择");
        Layout((FrameworkElement)note.Content, 260, 260);
        Check(((Border)view.FindName("EditorFrame")).ActualHeight >= 90, "最小便签收紧标题留白并保留正文空间");
        Layout((FrameworkElement)note.Content, 820, 720);
        // A non-visible native surface supplies normal WPF visibility/dispatcher
        // behavior for debounce checks without showing or focusing a window.
        using var surface = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Markdown test surface")
            { Width = 800, Height = 600, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000) });
        var liveView = new MarkdownNoteView { Task = first }; surface.RootVisual = liveView;
        Layout(liveView, 800, 600); liveView.SetMode(NoteViewMode.Split);
        var beforeRender = liveView.RenderCount;
        liveView.Text = Sample + "\n第一次"; liveView.Text = Sample + "\n最终内容";
        await Task.Delay(500);
        Check(liveView.RenderCount == beforeRender + 1 && new TextRange(liveView.PreviewDocument!.ContentStart, liveView.PreviewDocument.ContentEnd).Text.Contains("最终内容"), "连续编辑合并为一次预览刷新");
        liveView.Visibility = Visibility.Collapsed; beforeRender = liveView.RenderCount;
        liveView.Text += "\n隐藏修改"; await Task.Delay(450);
        Check(liveView.RenderCount == beforeRender, "隐藏编辑器时不渲染");
        liveView.Visibility = Visibility.Visible; await Task.Delay(450);
        Check(liveView.RenderCount == beforeRender + 1, "重新显示后补上最新预览");
        if (args.Length > 0)
        {
            var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            view.Text = Sample;
            Layout((FrameworkElement)note.Content, 365, 480); view.SetMode(NoteViewMode.Edit);
            Render((FrameworkElement)note.Content, Path.Combine(output, "markdown-edit.png"), 365, 480);
            view.SetMode(NoteViewMode.Preview);
            Render((FrameworkElement)note.Content, Path.Combine(output, "markdown-preview.png"), 365, 620);
            Render((FrameworkElement)note.Content, Path.Combine(output, "markdown-small.png"), 260, 260);
            view.SetMode(NoteViewMode.Split);
            Render((FrameworkElement)note.Content, Path.Combine(output, "markdown-split.png"), 920, 720);
            using var scheduler = new ReminderScheduler(repository, Dispatcher.CurrentDispatcher, _ => { });
            var main = new MainWindow(repository, scheduler);
            main.ShowTask(first.Date, first.Id);
            Layout((FrameworkElement)main.Content, 1100, 720);
            var mainView = (MarkdownNoteView)main.FindName("TaskNoteEditor"); mainView.Text = Sample;
            mainView.SetMode(NoteViewMode.Preview);
            Render((FrameworkElement)main.Content, Path.Combine(output, "markdown-main.png"), 1100, 720);
            Check(ReferenceEquals(mainView.Task, main.SelectedTask), "主页面接入共用 Markdown 控件");
        }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var found in Descendants<T>(child)) yield return found;
    }
    private static void Layout(FrameworkElement root, int width, int height)
    { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static void Render(FrameworkElement root, string path, int width, int height)
    {
        Layout(root, width, height);
        var content = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); content.Render(root);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        { dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(248, 248, 240)), null, new Rect(0, 0, width, height)); dc.DrawImage(content, new Rect(0, 0, width, height)); }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); png.Save(stream);
    }
}
