using Cdiary;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
[STAThread]
private static void Main(string[] args)
{
if (args.Length != 1) throw new ArgumentException("请指定预览输出目录。");
var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
var database = Path.Combine(Path.GetTempPath(), "CdiaryPreview", Guid.NewGuid().ToString("N"), "preview.db");
var repository = new DiaryRepository(database);
var today = DateTime.Today;
var first = repository.AddTask(today, "整理明天的计划");
first.Note = "准备资料，列出最重要的三件事。";
repository.SaveTask(first);
var second = repository.AddTask(today, "给小花浇水");
second.IsDone = true;
repository.SaveTask(second);
repository.AddTask(today, "读一会儿书");
repository.AddTask(today, "整理桌面");
repository.AddTask(today, "回复消息");
repository.AddTask(today.AddDays(1), "准备周会资料");
repository.AddTask(today.AddDays(2), "学习 WPF 布局");
repository.AddTask(today.AddDays(5), "去公园散步");
repository.AddTask(today.AddDays(-3), "给植物换水");
repository.AddTask(today.AddDays(-2), "整理上周的照片");
repository.AddTask(today.AddDays(-1), "寄出小礼物");
var pastDone = repository.AddTask(today.AddDays(-1), "已经完成的旧任务");
pastDone.IsDone = true;
repository.SaveTask(pastDone);
repository.SaveRule(first, true, (1 << (int)today.DayOfWeek) | (1 << (int)today.AddDays(3).DayOfWeek),
    "整理明天的计划", null);
repository.SaveDayMemo(today, "今天慢慢来，先做好眼前的事。");

var scheduler = new ReminderScheduler(repository, Dispatcher.CurrentDispatcher, _ => { });
var window = new MainWindow(repository, scheduler);
var root = (FrameworkElement)window.Content;

Render(root, Path.Combine(output, "preview-home.png"));
Render(root, Path.Combine(output, "preview-home-compact.png"), 1100, 580);
((UIElement)window.FindName("HomePage")!).Visibility = Visibility.Collapsed;
((RadioButton)window.FindName("CalendarTab")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
Render(root, Path.Combine(output, "preview-calendar.png"));
for (var month = 0; month < 4; month++)
    ((Button)window.FindName("CalendarNextButton")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
Render(root, Path.Combine(output, "preview-calendar-six-rows.png"));
Render(root, Path.Combine(output, "preview-calendar-compact.png"), 1100, 580);
((UIElement)window.FindName("CalendarPage")!).Visibility = Visibility.Collapsed;
((UIElement)window.FindName("SearchPage")!).Visibility = Visibility.Visible;
((RadioButton)window.FindName("SearchTab")!).IsChecked = true;
Render(root, Path.Combine(output, "preview-search.png"));
((UIElement)window.FindName("SearchPage")!).Visibility = Visibility.Collapsed;
((RadioButton)window.FindName("CompletedTab")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
Render(root, Path.Combine(output, "preview-completed.png"));
((UIElement)window.FindName("CompletedPage")!).Visibility = Visibility.Collapsed;
((UIElement)window.FindName("SettingsPage")!).Visibility = Visibility.Visible;
((RadioButton)window.FindName("SettingsTab")!).IsChecked = true;
Render(root, Path.Combine(output, "preview-settings.png"));
var repeat = new RepeatRuleWindow(repository, first);
var repeatRoot = (FrameworkElement)repeat.Content;
((RadioButton)repeat.FindName("DailyRadio")!).IsChecked = true;
repeatRoot.Measure(new Size(445, double.PositiveInfinity));
Render(repeatRoot, Path.Combine(output, "preview-repeat-daily.png"), 445, (int)Math.Ceiling(repeatRoot.DesiredSize.Height));
((RadioButton)repeat.FindName("WeeklyRadio")!).IsChecked = true;
repeatRoot.Measure(new Size(445, double.PositiveInfinity));
Render(repeatRoot, Path.Combine(output, "preview-repeat-weekly.png"), 445, (int)Math.Ceiling(repeatRoot.DesiredSize.Height));
var minuteOptions = new StackPanel();
foreach (var minute in new[] { "00", "05", "10", "15" })
    minuteOptions.Children.Add(new ComboBoxItem
    {
        Content = minute,
        IsSelected = minute == "10",
        Style = (Style)repeat.Resources[typeof(ComboBoxItem)]
    });
var minutePreview = new Border
{
    Background = new SolidColorBrush(Color.FromRgb(247, 243, 223)),
    BorderBrush = new SolidColorBrush(Color.FromRgb(216, 203, 179)),
    BorderThickness = new Thickness(1.5),
    CornerRadius = new CornerRadius(16),
    Padding = new Thickness(4),
    Child = minuteOptions
};
Render(minutePreview, Path.Combine(output, "preview-repeat-minute-options.png"), 86, 180);
}

static void Render(FrameworkElement root, string path, int width = 1100, int height = 720)
{
    root.Measure(new Size(width, height));
    root.Arrange(new Rect(0, 0, width, height));
    root.UpdateLayout();
        var content = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        content.Render(root);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(248, 248, 240)), null,
                new Rect(0, 0, width, height));
            drawing.DrawImage(content, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
    Console.WriteLine(path);
}
}
