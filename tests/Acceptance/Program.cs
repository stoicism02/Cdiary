using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cdiary;

internal static class Program
{
    static int checks;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
    [STAThread] static void Main(string[] args)
    {
        Exception? failure = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try { await Run(args); } catch (Exception e) { failure = e; }
            finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        Dispatcher.Run();
        if (failure is not null) throw failure;
    }
    static async Task Run(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 1 ? args[1] : "build-check/acceptance.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var data = Path.Combine(Path.GetTempPath(), "CdiaryAcceptance", Guid.NewGuid().ToString("N"));
        // Environment override keeps the benchmark browser process group separate from personal WebView2 sessions.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(data, "webview"));
        var repo = new DiaryRepository(Path.Combine(data, "diary.db"));
        for (int i = 0; i < 100; i++)
        {
            var task = repo.AddTask(DateTime.Today, $"验收任务 {i + 1}：中文输入、长标题与任务笔记的布局检查");
            task.Note = string.Concat(Enumerable.Repeat("## 消息可靠性\n\n中文内容，**加粗**与链接。\n\n- 持续记录\n- 检查边界\n\n", 80));
            repo.SaveTask(task);
        }
        using var scheduler = new ReminderScheduler(repo, Dispatcher.CurrentDispatcher, _ => { });
        var store = new FocusStore(repo); var service = new FocusService(store);
        var start = Stopwatch.StartNew();
        var window = new MainWindow(repo, scheduler, focusStore: store, focus: service)
        { Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, AllowExit = true };
        var root = (FrameworkElement)window.Content;
        if (args.FirstOrDefault() == "--perf")
        {
            var notes = new List<TaskNoteWindow>();
            try
            {
                window.Show(); await Task.Delay(1000);
                var initializeMs = start.Elapsed.TotalMilliseconds;
                var samples = new List<object>();
                samples.Add(await Sample("main-window", notes));
                window.Hide(); await Task.Delay(1000);
                samples.Add(await Sample("hidden-main", notes));
                for (int i = 0; i < 3; i++)
                {
                    var task = window.Tasks[i];
                    var note = new TaskNoteWindow(task, repo)
                    { Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, Width = 400, Height = 600 };
                    note.AttachFocus(store, service, _ => { }); notes.Add(note); note.Show();
                    var editor = (MarkdownNoteView)note.FindName("NoteEditor");
                    var wait = Stopwatch.StartNew();
                    while (!editor.IsDocumentReady && wait.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(100);
                    Check(editor.IsDocumentReady, "Real WebView2 editor initialized");
                    if (i is 0 or 2) { await Task.Delay(2000); samples.Add(await Sample(i == 0 ? "one-note" : "three-notes", notes)); }
                }
                await Write(output, new { method = "Same Release harness, isolated sample data, offscreen inactive windows; includes own WebView2 processes", assembly = typeof(MainWindow).Assembly.ManifestModule.ModuleVersionId, logicalProcessors = Environment.ProcessorCount, windowInitializationIncluding1000msSettle = initializeMs, samples });
            }
            finally { foreach (var note in notes) { await note.PrepareDocumentExitAsync(); note.Close(); } window.Close(); }
            return;
        }
        var records = new List<object>();
        // SetRootDpi exercises WPF scale propagation. It does not emulate physical monitor movement or IME input.
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
        {
            VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
            Check(Math.Abs(VisualTreeHelper.GetDpi(root).DpiScaleX - scale) < .001, "WPF DPI applied");
            foreach (var size in new[] { (760, 580), (1100, 720), (1500, 900), (1920, 1080) })
            {
                void Layout() { root.Measure(new Size(size.Item1, size.Item2)); root.Arrange(new Rect(0, 0, size.Item1, size.Item2)); root.UpdateLayout(); }
                foreach (var tab in new[] { "HomeTab", "CalendarTab", "FocusTab", "SearchTab", "CompletedTab", "SettingsTab", "ScreenshotTab" })
                {
                    ((RadioButton)window.FindName(tab)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Layout();
                    var pageName = tab switch { "HomeTab" => "HomePage", "FocusTab" => "FocusView", "ScreenshotTab" => "ScreenshotGallery", _ => tab.Replace("Tab", "Page") };
                    var page = (FrameworkElement)window.FindName(pageName);
                    var bounds = page.TransformToAncestor(root).TransformBounds(new Rect(page.RenderSize));
                    Check(page.Visibility == Visibility.Visible && bounds.Left >= -.5 && bounds.Right <= size.Item1 + .5 && bounds.Top >= -.5 && bounds.Bottom <= size.Item2 + .5, $"{tab} fits {size} @ {scale}: {bounds}");
                    records.Add(new { scale, width = size.Item1, height = size.Item2, tab, bounds = new { bounds.Left, bounds.Top, bounds.Width, bounds.Height } });
                }
            }
        }
        window.Close();
        await Write(output, new { method = "WPF simulated root DPI, logical layout units; no foreground window", checks, records });
        Console.WriteLine($"Acceptance: {checks} checks passed.");
    }
    static async Task<object> Sample(string phase, List<TaskNoteWindow> notes)
    {
        HashSet<int> Ids()
        {
            var ids = new HashSet<int> { Environment.ProcessId };
            foreach (var note in notes)
            {
                var editor = (MarkdownNoteView)note.FindName("NoteEditor");
                if (((Border)editor.FindName("DocumentContainer")).Child is DocumentNoteHost { Browser: { } core })
                    foreach (var info in core.Environment.GetProcessInfos()) ids.Add(info.ProcessId);
            }
            return ids;
        }
        var before = new Dictionary<int, TimeSpan>();
        foreach (int id in Ids()) { using var p = Process.GetProcessById(id); before[id] = p.TotalProcessorTime; }
        var watch = Stopwatch.StartNew(); var privateBytes = new List<long>(); var workingBytes = new List<long>(); double cpuMs = 0;
        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(750); long priv = 0, working = 0;
            foreach (int id in Ids())
            {
                try { using var p = Process.GetProcessById(id); priv += p.PrivateMemorySize64; working += p.WorkingSet64; } catch (ArgumentException) { }
            }
            privateBytes.Add(priv); workingBytes.Add(working);
        }
        foreach (var (id, cpu) in before)
        { try { using var p = Process.GetProcessById(id); cpuMs += (p.TotalProcessorTime - cpu).TotalMilliseconds; } catch (ArgumentException) { } }
        return new { phase, processCount = Ids().Count, durationMs = watch.Elapsed.TotalMilliseconds, meanPrivateMiB = privateBytes.Average() / 1048576, maxPrivateMiB = privateBytes.Max() / 1048576d, meanWorkingMiB = workingBytes.Average() / 1048576, cpuMachinePercent = cpuMs / watch.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100 };
    }
    static Task Write(string output, object value) => File.WriteAllTextAsync(output, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}
