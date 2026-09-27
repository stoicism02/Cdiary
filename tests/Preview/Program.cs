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
repository.SaveDayMemo(today, "今天慢慢来，先做好眼前的事。");

var scheduler = new ReminderScheduler(repository, Dispatcher.CurrentDispatcher, _ => { });
var window = new MainWindow(repository, scheduler);
var root = (FrameworkElement)window.Content;

Render(root, Path.Combine(output, "preview-home.png"));
((UIElement)window.FindName("HomePage")!).Visibility = Visibility.Collapsed;
((UIElement)window.FindName("SearchPage")!).Visibility = Visibility.Visible;
((RadioButton)window.FindName("SearchTab")!).IsChecked = true;
Render(root, Path.Combine(output, "preview-search.png"));
((UIElement)window.FindName("SearchPage")!).Visibility = Visibility.Collapsed;
((UIElement)window.FindName("SettingsPage")!).Visibility = Visibility.Visible;
((RadioButton)window.FindName("SettingsTab")!).IsChecked = true;
Render(root, Path.Combine(output, "preview-settings.png"));
var repeat = new RepeatRuleWindow(repository, first);
Render((FrameworkElement)repeat.Content, Path.Combine(output, "preview-repeat.png"), 445, 575);
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
