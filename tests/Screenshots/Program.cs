using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cdiary;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static void Main(string[] args)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        Exception? failure = null;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await Run(args); }
            catch (Exception error) { failure = error; }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        Dispatcher.Run();
        if (failure is not null) throw new Exception("截图测试失败", failure);
        Console.WriteLine($"截图模块：{_checks} 项检查通过。未显示窗口、未截图桌面、未修改剪贴板。");
    }
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); _checks++; Console.WriteLine($"通过：{name}"); }
    private static async Task Run(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "CdiaryScreenshotTests", Guid.NewGuid().ToString("N"));
        var store = new ScreenshotStore(Path.Combine(root, "captures"));
        Check(store.LoadOptions() == new ScreenshotOptions(), "默认 1 个自然日、500 MB、Ctrl+Alt+S");
        var captured = new DateTime(2026, 10, 1, 23, 58, 0);
        Check(ScreenshotStore.Expiry(captured, 1) == new DateTime(2026, 10, 2), "临近零点截图按自然日到期");
        Check(ScreenshotStore.Expiry(captured, 3) == new DateTime(2026, 10, 4), "3 天保留覆盖今天和前两天");
        Check(ScreenshotStore.Expiry(new DateTime(2026, 12, 31, 23, 59, 59), 1) == new DateTime(2027, 1, 1), "跨年到期时间");
        var png = SampleImage(960, 540, 0); var image = ScreenshotService.Decode(png); var thumb = ScreenshotService.Thumbnail(image);
        var small = ScreenshotService.Decode(thumb);
        Check(image.IsFrozen && small.PixelWidth <= 320 && small.PixelHeight <= 200, "原图冻结且缩略图控制尺寸");
        var expired = store.Save(png, thumb, 960, 540, captured);
        var boundary = store.Save(png, thumb, 960, 540, new DateTime(2026, 10, 2, 0, 0, 0));
        Check(new ScreenshotStore(store.DirectoryPath).Load().Count == 2, "重开索引后截图可恢复");
        Check(store.Cleanup(new DateTime(2026, 10, 1, 23, 59, 59)) == 0 && store.Load().Count == 2, "到期前不删除");
        using (var locked = File.Open(expired.OriginalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Check(store.Cleanup(new DateTime(2026, 10, 2)) == 1 && store.Load().Count == 2, "占用导致清理失败时保留索引以供重试");
        }
        store.Cleanup(new DateTime(2026, 10, 2));
        Check(store.Load().Single().Id == boundary.Id && !File.Exists(expired.OriginalPath) && !File.Exists(expired.ThumbnailPath), "零点删除过期原图与缩略图，保留新一天截图");
        store.SaveOptions(new ScreenshotOptions(3, 10));
        Check(store.Load().Single().ExpiresAt == new DateTime(2026, 10, 5), "修改保留天数同步已有图片到期时间");
        var before = store.Load().Count; var refused = false;
        try { store.Save(new byte[10 * 1024 * 1024], thumb, 1, 1, captured); }
        catch (IOException) { refused = true; }
        Check(refused && store.Load().Count == before && Directory.GetFiles(store.DirectoryPath, "*.png").Length == 2, "容量不足不产生索引或残留图片");
        var orphan = Path.Combine(store.DirectoryPath, Guid.NewGuid().ToString("N") + ".png"); File.WriteAllBytes(orphan, png);
        var unrelated = Path.Combine(store.DirectoryPath, "user-export.png"); File.WriteAllBytes(unrelated, png);
        store.Cleanup(new DateTime(2026, 10, 2));
        Check(!File.Exists(orphan) && File.Exists(unrelated), "恢复孤立文件，只清理本模块命名的图片");
        var traversalRejected = false;
        try { store.Delete("..\\user-export"); } catch (ArgumentException) { traversalRejected = true; }
        Check(traversalRejected && File.Exists(unrelated), "删除接口拒绝路径穿越");
        File.Delete(boundary.OriginalPath); store.Cleanup(new DateTime(2026, 10, 2));
        Check(store.Load().Count == 0 && !File.Exists(boundary.ThumbnailPath), "修复外部删除原图后的索引和缩略图");
        var final = store.Save(png, thumb, 960, 540, captured);
        store.Cleanup(new DateTime(2026, 10, 10));
        Check(store.Load().Count == 0 && !File.Exists(final.OriginalPath), "错过多个零点后一次补清理");

        var flowStore = new ScreenshotStore(Path.Combine(root, "flow"));
        var clipboardWrites = 0;
        using (var cancelled = new ScreenshotService(flowStore, Dispatcher.CurrentDispatcher, false,
                   () => null, _ => { clipboardWrites++; return Task.CompletedTask; }))
            await cancelled.CaptureAsync();
        Check(clipboardWrites == 0 && flowStore.Load().Count == 0, "取消截图不触碰剪贴板、不保存图片");
        using (var clipboardBusy = new ScreenshotService(flowStore, Dispatcher.CurrentDispatcher, false,
                   CaptureFixture, _ => throw new System.Runtime.InteropServices.ExternalException("busy")))
        {
            string notice = ""; clipboardBusy.Notice += text => notice = text;
            await clipboardBusy.CaptureAsync();
            Check(flowStore.Load().Count == 1 && notice.Contains("复制失败") && notice.Contains("已暂存"), "剪贴板失败不影响截图暂存");
        }
        flowStore.SaveOptions(new ScreenshotOptions(1, 10));
        var currentBytes = flowStore.Load().Sum(item => item.Bytes);
        flowStore.Save(new byte[10 * 1024 * 1024 - currentBytes - thumb.Length - 100], thumb, 1, 1, DateTime.Now);
        using (var full = new ScreenshotService(flowStore, Dispatcher.CurrentDispatcher, false,
                   CaptureFixture, bitmap => { clipboardWrites++; return Task.CompletedTask; }))
        {
            string notice = ""; full.Notice += text => notice = text;
            await full.CaptureAsync();
            Check(clipboardWrites == 1 && flowStore.Load().Count == 2 && notice.Contains("已复制") && notice.Contains("未暂存"), "暂存容量满不影响复制图片");
        }

        // UI checks render into bitmaps; they never call Window.Show or take the user's focus.
        var galleryStore = new ScreenshotStore(Path.Combine(root, "gallery")); galleryStore.SaveOptions(new ScreenshotOptions(3));
        for (var i = 0; i < 39; i++)
        {
            var sample = SampleImage(960, 540, i % 3); var source = ScreenshotService.Decode(sample);
            galleryStore.Save(sample, ScreenshotService.Thumbnail(source), 960, 540, DateTime.Today.AddDays(-(i / 15)).AddHours(16).AddMinutes(-i));
        }
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var service = new ScreenshotService(galleryStore, dispatcher, false);
        var diary = new DiaryRepository(Path.Combine(root, "diary.db"));
        using var reminders = new ReminderScheduler(diary, dispatcher, _ => { });
        var window = new MainWindow(diary, reminders, service); var rootElement = (FrameworkElement)window.Content;
        window.ShowScreenshots();
        var gallery = (ScreenshotPage)window.FindName("ScreenshotGallery"); await gallery.RefreshAsync();
        Layout(rootElement, 1100, 720); await Dispatcher.Yield(DispatcherPriority.Background);
        Layout(rootElement, 1100, 720);
        Check(gallery.Visibility == Visibility.Visible && ((UIElement)window.FindName("HomePage")).Visibility == Visibility.Collapsed, "截图页独立导航");
        var rows = (ListBox)gallery.FindName("Rows");
        Check(rows.Items.Count >= 13 && VirtualizingPanel.GetIsVirtualizing(rows) && rows.ItemContainerGenerator.ContainerFromIndex(rows.Items.Count - 1) is null,
            "截图按日期和三列排列，屏幕外的行没有生成控件");
        var selection = Descendants<CheckBox>(rows).First();
        var selectedId = (string)selection.Tag;
        selection.IsChecked = true; selection.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var scroll = Descendants<ScrollViewer>(rows).First();
        scroll.ScrollToVerticalOffset(3); Layout(rootElement, 1100, 720);
        var anchorId = ((ScreenshotPage.PhotoRow)rows.Items[(int)scroll.VerticalOffset]).Items[0].Id;
        Layout(rootElement, 760, 580); await Dispatcher.Yield(DispatcherPriority.Background); Layout(rootElement, 760, 580);
        Check(((ScreenshotPage.PhotoRow)rows.Items[0]).Columns == 2 && ((ScreenshotPage.PhotoRow)rows.Items[(int)scroll.VerticalOffset]).Items.Any(entry => entry.Id == anchorId),
            "缩窄后变为两列并保留顶部浏览图片");
        var rapidAnchor = ((ScreenshotPage.PhotoRow)rows.Items[(int)scroll.VerticalOffset]).Items[0].Id;
        Layout(rootElement, 1100, 720); Layout(rootElement, 1500, 900);
        await Dispatcher.Yield(DispatcherPriority.Background); Layout(rootElement, 1500, 900);
        Check(((ScreenshotPage.PhotoRow)rows.Items[(int)scroll.VerticalOffset]).Items.Any(entry => entry.Id == rapidAnchor),
            "连续跨越列数断点仍保留最初的浏览锚点");
        Layout(rootElement, 760, 580); await Dispatcher.Yield(DispatcherPriority.Background);
        scroll.ScrollToTop(); Layout(rootElement, 760, 580); await Dispatcher.Yield(DispatcherPriority.Background);
        foreach (var check in Descendants<CheckBox>(rows)) check.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Check(Descendants<CheckBox>(rows).Any(check => Equals(check.Tag, selectedId) && check.IsChecked == true), "重排图片后保留选中状态");
        Layout(rootElement, 1500, 900); await Dispatcher.Yield(DispatcherPriority.Background); Layout(rootElement, 1500, 900);
        Check(((ScreenshotPage.PhotoRow)rows.Items[0]).Columns == 5 && rows.ItemContainerGenerator.ContainerFromIndex(rows.Items.Count - 1) is null,
            "宽屏增加到五列并继续虚拟化");
        var settings = (ScreenshotSettingsView)window.FindName("ScreenshotSettings");
        ((RadioButton)window.FindName("SettingsTab")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(gallery.Visibility == Visibility.Collapsed && ((TextBox)settings.FindName("DaysBox")).Text == "3", "设置页显示当前清理规则");
        Layout(rootElement, 1100, 720);
        if (args.Length == 1)
        {
            var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            Render(rootElement, Path.Combine(output, "screenshots-settings.png"), 1100, 720);
            Render(rootElement, Path.Combine(output, "screenshots-settings-compact.png"), 760, 580);
            window.ShowScreenshots(); await gallery.RefreshAsync(); Layout(rootElement, 1100, 720); await Dispatcher.Yield(DispatcherPriority.Background);
            // Offscreen trees do not receive the native presentation source's Loaded event.
            foreach (var thumbnail in Images(rootElement)) thumbnail.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            for (var retry = 0; retry < 30 && !Images(rootElement).Any(image => image.Source is not null); retry++) await Task.Delay(50);
            Check(Images(rootElement).Any(image => image.Source is not null), "可见缩略图异步加载完成");
            Render(rootElement, Path.Combine(output, "screenshots-gallery.png"), 1100, 720);
            foreach (var size in new[] { (760, 580), (1500, 900), (1920, 1080) })
            {
                Layout(rootElement, size.Item1, size.Item2); await Dispatcher.Yield(DispatcherPriority.Background);
                foreach (var thumbnail in Images(rootElement)) thumbnail.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                await Task.Delay(150);
                Render(rootElement, Path.Combine(output, $"screenshots-gallery-{size.Item1}.png"), size.Item1, size.Item2);
            }
            var entry = galleryStore.Load()[0]; var previewImage = ScreenshotService.Decode(File.ReadAllBytes(entry.OriginalPath));
            var viewer = new ScreenshotViewer(entry, previewImage, window);
            Render((FrameworkElement)viewer.Content, Path.Combine(output, "screenshots-viewer.png"), 920, 650);
            galleryStore.Delete(entry.Id);
            Check(previewImage.PixelWidth == 960, "预览按内存快照读取，到期删除文件后仍能使用");
        }
        await service.SaveOptionsAsync(new ScreenshotOptions(7, 300));
        Check(galleryStore.LoadOptions().RetentionDays == 7 && galleryStore.LoadOptions().CapacityMb == 300, "设置通过服务保存并重新读取");
    }
    private static void Layout(FrameworkElement root, int width, int height)
    { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static void Render(FrameworkElement root, string path, int width, int height)
    {
        Layout(root, width, height); var content = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); content.Render(root);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(248, 248, 240)), null, new Rect(0, 0, width, height));
            drawing.DrawImage(content, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static IEnumerable<Image> Images(DependencyObject root)
    {
        if (root is Image image) yield return image;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Images(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static System.Drawing.Bitmap CaptureFixture()
    {
        var bitmap = new System.Drawing.Bitmap(400, 240, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.FromArgb(25, 200, 185)); return bitmap;
    }
    private static byte[] SampleImage(int width, int height, int variant)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(248, 248, 240)), null, new Rect(0, 0, width, height));
            context.DrawRoundedRectangle(new SolidColorBrush(variant == 1 ? Color.FromRgb(221, 247, 241) : Color.FromRgb(247, 243, 223)), null,
                new Rect(40, 40, width - 80, height - 80), 28, 28);
            context.DrawText(new FormattedText(new[] { "今天的小计划", "想留下的灵感", "常用的快捷方式" }[variant],
                System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 38,
                new SolidColorBrush(Color.FromRgb(121, 79, 39)), 1), new Point(80, 85));
            for (var row = 0; row < 4; row++)
            {
                context.DrawEllipse(new SolidColorBrush(Color.FromRgb(25, 200, 185)), null, new Point(96, 210 + row * 55), 10, 10);
                context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(216, 203, 179)), null, new Rect(130, 200 + row * 55, 430 - row * 65, 18), 9, 9);
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
