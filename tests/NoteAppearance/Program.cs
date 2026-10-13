using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cdiary;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("通过：" + message); }
    [STAThread] private static void Main()
    {
        Exception? failure = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () => {
            try { await Run(); } catch (Exception e) { failure = e; }
            finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        Dispatcher.Run();
        if (failure is not null) throw failure;
    }
    private static async Task Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        var repo = new DiaryRepository(Path.Combine(root, "diary.db"));
        var task = repo.AddTask(DateTime.Today, "RocketMQ 学习笔记");
        task.Note = "## 消息可靠性\n\n把复杂的知识，整理成自己的理解。\n\n- 生产者确认发送结果\n- Broker 持久化消息\n- 消费者处理后确认\n\n> 今天先理解完整链路。\n\n**下一步**：补上失败重试的例子。";
        repo.SaveTask(task);
        var store = new NoteAppearanceStore(repo.DatabasePath, task.Id);
        Check(store.Load() == new NoteAppearance(), "首次使用简约背景默认值");
        store.Save(new NoteAppearance(-4, 8));
        Check(store.Load().Opacity == .1 && store.Load().ImageStrength == 1, "越界参数归一化");
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen()) {
            dc.DrawRectangle(new LinearGradientBrush(Colors.LightBlue, Colors.LightPink, 35), null, new Rect(0, 0, 800, 500));
            dc.DrawEllipse(Brushes.White, null, new Point(630, 140), 100, 100);
        }
        var source = new RenderTargetBitmap(800, 500, 96, 96, PixelFormats.Pbgra32); source.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        var original = Path.Combine(root, "original.png"); using (var stream = File.Create(original)) encoder.Save(stream);
        var imported = store.Import(original);
        Check(imported.PixelWidth == 800 && imported.IsFrozen, "图片解码后不放大小图并冻结供显示");
        File.Delete(original);
        Check(store.ReadImage() is not null, "原图片移走后本地背景仍可读取");
        store.Save(new NoteAppearance(.65, .4, true));
        var window = new TaskNoteWindow(task, repo) { Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, Width = 400, Height = 600 };
        var focusStore = new FocusStore(repo);
        window.AttachFocus(focusStore, new FocusService(focusStore), _ => { });
        try {
            var layer = (Border)window.FindName("BackgroundLayer");
            var image = (Border)window.FindName("PictureLayer");
            Check(window.AllowsTransparency && window.Opacity == 1 && layer.Opacity == .65 && image.Background is ImageBrush, "透明度仅作用于背景图层，窗口和前景保持不透明");
            var editor = (MarkdownNoteView)window.FindName("NoteEditor");
            Check(editor.MinimalSticky && editor.Opacity == 1, "便签启用透明编辑器并保持文字不透明");
            ((Slider)window.FindName("BackgroundOpacitySlider")).Value = 75;
            await Task.Delay(450);
            Check(store.Load().Opacity == .75, "调节透明度后自动保存为全局设置");
            Check(new NoteAppearanceStore(repo.DatabasePath, task.Id + 1).Load().Opacity == .75, "其他任务读取同一份全局外观");
            window.Show(); // Offscreen, never activated; exercises the real layered window.
            var pin = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("PinToggle");
            var choices = new[] { "PinLineChoice", "PinWindowsChoice", "PinTopChoice" };
            var sample = new Grid { Width = 360, Height = 126, Background = Brushes.White };
            for (var i = 0; i < 3; i++) sample.ColumnDefinitions.Add(new ColumnDefinition());
            for (var i = 0; i < choices.Length; i++) {
                ((RadioButton)window.FindName(choices[i])).IsChecked = true;
                pin.IsChecked = true; window.UpdateLayout();
                Check(store.Load().PinStyle == i && window.Topmost && pin.ToolTip?.ToString() == "取消置顶", $"第 {i + 1} 款图标保存且置顶绑定生效");
                pin.IsChecked = false; window.UpdateLayout();
                Check(!window.Topmost && pin.ToolTip?.ToString() == "置顶便签", $"第 {i + 1} 款图标可取消置顶");
                var group = new StackPanel { Margin = new Thickness(8) };
                group.Children.Add(new TextBlock { Text = new[] { "细线图钉", "叠窗置顶", "顶线箭头" }[i], HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,0,0,8) });
                foreach (var selected in new[] { false, true }) {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                    row.Children.Add(new System.Windows.Controls.Primitives.ToggleButton { Style = pin.Style, Tag = pin.Tag, IsChecked = selected });
                    row.Children.Add(new TextBlock { Text = selected ? "已置顶" : "未置顶", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8,0,0,0) });
                    group.Children.Add(row);
                }
                Grid.SetColumn(group, i); sample.Children.Add(group);
            }
            sample.Measure(new Size(360,126)); sample.Arrange(new Rect(0,0,360,126)); sample.UpdateLayout();
            var pinCapture = new RenderTargetBitmap(720,252,192,192,PixelFormats.Pbgra32); pinCapture.Render(sample);
            var pinPng = new PngBitmapEncoder(); pinPng.Frames.Add(BitmapFrame.Create(pinCapture));
            Directory.CreateDirectory("build-check"); using (var stream = File.Create("build-check/pin-styles.png")) pinPng.Save(stream);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!editor.IsDocumentReady && DateTime.UtcNow < deadline) await Task.Delay(100);
            Check(editor.IsDocumentReady, "透明便签中的实际编辑器正常初始化");
            var host = (DocumentNoteHost)((Border)editor.FindName("DocumentContainer")).Child;
            await Task.Delay(600);
            Check(JsonSerializer.Deserialize<bool>(await host.Browser!.ExecuteScriptAsync("document.documentElement.classList.contains('minimal-sticky')")), "实际便签加载简约正文主题");
            var second = new TaskNoteWindow(task, repo) { Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
            try {
                second.Show();
                var otherEditor = (MarkdownNoteView)second.FindName("NoteEditor");
                deadline = DateTime.UtcNow.AddSeconds(15);
                while (!otherEditor.IsDocumentReady && DateTime.UtcNow < deadline) await Task.Delay(100);
                Check(otherEditor.IsDocumentReady && ((Border)second.FindName("BackgroundLayer")).Opacity == .75, "多个透明便签可同时打开并恢复保存的外观");
                Check(((RadioButton)second.FindName("PinTopChoice")).IsChecked == true, "重新打开便签恢复所选置顶图标");
                ((Slider)window.FindName("BackgroundOpacitySlider")).Value = 85;
                await Task.Delay(450);
                Check(((Border)second.FindName("BackgroundLayer")).Opacity == .85, "已打开的便签同步全局透明度");
                ((Slider)second.FindName("BackgroundOpacitySlider")).Value = 75;
                await Task.Delay(450);
                Check(layer.Opacity == .75 && store.Load().Opacity == .75, "另一便签修改外观可反向同步和持久化");
                store.Save(store.Load() with { HasImage = false });
                Check(image.Background is null && ((Border)second.FindName("PictureLayer")).Background is null, "全局关闭背景图同步到所有打开的便签");
                store.Save(store.Load() with { HasImage = true });
                Check(image.Background is ImageBrush && ((Border)second.FindName("PictureLayer")).Background is ImageBrush, "全局背景图同步显示在所有打开的便签");
                await second.PrepareDocumentExitAsync();
            } finally { second.Close(); }
            window.Width = 280; window.Height = 280; window.UpdateLayout(); await Task.Delay(250);
            Check(JsonSerializer.Deserialize<bool>(await host.Browser.ExecuteScriptAsync("document.documentElement.scrollWidth<=innerWidth && document.querySelector('.vditor-toolbar').scrollWidth<=innerWidth")), "最小便签尺寸下编辑器没有横向溢出");
            window.Width = 400; window.Height = 600; window.UpdateLayout(); await Task.Delay(250);
            await host.Browser.ExecuteScriptAsync("island.editor.focus()");
            await host.Browser.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = "自动保存验证" }));
            await host.FlushAsync();
            Check(repo.LoadTasks(DateTime.Today).Single().Note.Contains("自动保存验证"), "透明便签真实输入可保存");
            var output = Path.GetFullPath(Path.Combine("build-check", "minimal-sticky.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            window.UpdateLayout();
            var capture = new RenderTargetBitmap(400, 600, 96, 96, PixelFormats.Pbgra32); capture.Render(window);
            var pixels = new byte[400 * 600 * 4]; capture.CopyPixels(pixels, 400 * 4, 0);
            var backgroundAlpha = pixels[(500 * 400 + 200) * 4 + 3];
            Check(backgroundAlpha is > 175 and < 210, "实际渲染背景含透明通道，可透出桌面");
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(capture)); using (var stream = File.Create(output)) png.Save(stream);
            Console.WriteLine("截图：" + output);
            await window.PrepareDocumentExitAsync();
        } finally { window.Close(); }
        store.RemoveImage(); Check(store.ReadImage() is null, "移除背景只删除应用内的背景副本");
        var legacyRoot = Path.Combine(root, "legacy", "NoteAppearance", "42");
        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "appearance.json"), "{\"Opacity\":0.6,\"ImageStrength\":0.2,\"HasImage\":true}");
        var legacyEncoder = new PngBitmapEncoder(); legacyEncoder.Frames.Add(BitmapFrame.Create(source));
        using (var stream = File.Create(Path.Combine(legacyRoot,"background.png"))) legacyEncoder.Save(stream);
        var migrated = new NoteAppearanceStore(Path.Combine(root,"legacy","diary.db"), 999);
        Check(migrated.Load().Opacity == .6 && migrated.ReadImage() is not null, "从其他任务的旧设置迁移已有背景到全局");
        migrated.Save(new NoteAppearance(.8));
        Check(new NoteAppearanceStore(Path.Combine(root,"legacy","diary.db"),42).Load().Opacity == .8 && File.Exists(Path.Combine(legacyRoot,"background.png")), "全局设置不被旧配置覆盖且旧图片保留");
        Console.WriteLine($"便签外观：{checks} 项检查通过。");
    }
}
