using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Cdiary;

public sealed class ScreenshotViewer : Window
{
    private readonly BitmapSource _bitmap;
    private readonly System.Windows.Controls.Image _image;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _status = new() { FontSize = 11, Margin = new Thickness(16, 8, 16, 10) };
    private double _zoom = 1;
    private bool _fit = true;
    private Point? _pan;
    private double _panX, _panY;

    public ScreenshotViewer(ScreenshotEntry entry, BitmapSource bitmap, Window owner)
        : this($"截图 · {entry.CapturedAt:MM-dd HH:mm:ss}",bitmap,owner,entry.OriginalPath) { }
    public ScreenshotViewer(string title, BitmapSource bitmap, Window owner, string originalPath)
    {
        _bitmap = bitmap;
        Title = title; Icon = owner.Icon;
        Resources.MergedDictionaries.Add(new LiveStyleDictionary());
        if (new System.Windows.Interop.WindowInteropHelper(owner).Handle != IntPtr.Zero) Owner = owner;
        Width = 920; Height = 650; MinWidth = 660; MinHeight = 420;
        WindowStyle = WindowStyle.None; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(248, 248, 240));
        Foreground = new SolidColorBrush(Color.FromRgb(114, 93, 66)); FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 58, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0) });
        var grid = new Grid { Margin = new Thickness(10) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var toolbar = new DockPanel { Margin = new Thickness(6, 4, 6, 10) };
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right); toolbar.Children.Add(actions);
        Button Add(string title, Action action)
        {
            var button = new Button { Content = title, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(5, 0, 0, 0) };
            button.SetResourceReference(Button.StyleProperty, "Pill");
            WindowChrome.SetIsHitTestVisibleInChrome(button, true); button.Click += (_, _) => action(); actions.Children.Add(button); return button;
        }
        Add("适应", () => { _fit = true; Fit(); }); Add("100%", () => { _fit = false; Zoom(1); });
        Add("复制", async () => await CopyAsync());
        Add("另存为", () => Export(originalPath)); Add("×", Close);
        toolbar.Children.Add(new TextBlock { Text = Title, FontSize = 16, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        _image = new System.Windows.Controls.Image { Source = bitmap, Stretch = Stretch.Fill };
        _scroll = new ScrollViewer { Content = _image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(240, 232, 216)), Cursor = System.Windows.Input.Cursors.Hand };
        _scroll.SizeChanged += (_, _) => { if (_fit) Fit(); };
        _scroll.PreviewMouseWheel += (_, e) => { _fit = false; Zoom(Math.Clamp(_zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), 0.02, 8)); e.Handled = true; };
        _image.MouseLeftButtonDown += (_, e) =>
        { _pan = e.GetPosition(_scroll); _panX = _scroll.HorizontalOffset; _panY = _scroll.VerticalOffset; _image.CaptureMouse(); e.Handled = true; };
        _image.MouseMove += (_, e) =>
        {
            if (_pan is not { } start) return;
            var current = e.GetPosition(_scroll); _scroll.ScrollToHorizontalOffset(_panX + start.X - current.X); _scroll.ScrollToVerticalOffset(_panY + start.Y - current.Y);
        };
        _image.MouseLeftButtonUp += (_, _) => { _pan = null; _image.ReleaseMouseCapture(); };
        _image.LostMouseCapture += (_, _) => _pan = null;
        grid.Children.Add(toolbar); Grid.SetRow(_scroll, 1); grid.Children.Add(_scroll);
        Grid.SetRow(_status, 2); grid.Children.Add(_status); Content = grid;
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { await CopyAsync(); e.Handled = true; }
        };
        Loaded += (_, _) => Fit();
    }
    private void Fit()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        Zoom(Math.Min(1, Math.Min(Math.Max(1, _scroll.ActualWidth - 24) * dpi.DpiScaleX / _bitmap.PixelWidth,
            Math.Max(1, _scroll.ActualHeight - 24) * dpi.DpiScaleY / _bitmap.PixelHeight)));
    }
    private void Zoom(double scale)
    {
        _zoom = scale; var dpi = VisualTreeHelper.GetDpi(this);
        _image.Width = _bitmap.PixelWidth * scale / dpi.DpiScaleX; _image.Height = _bitmap.PixelHeight * scale / dpi.DpiScaleY;
        _status.Text = $"{_bitmap.PixelWidth} × {_bitmap.PixelHeight} · {scale:P0} · 滚轮缩放，拖动查看。";
    }
    private async Task CopyAsync()
    {
        try { await ScreenshotService.CopyAsync(_bitmap); _status.Text = "已复制图片，可以直接粘贴。"; }
        catch (Exception error) { _status.Text = $"复制失败：{error.Message}"; }
    }
    private void Export(string originalPath)
    {
        var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = $"图片_{DateTime.Now:yyyyMMdd_HHmmss}.png" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (Path.GetFullPath(dialog.FileName).StartsWith(Path.GetDirectoryName(originalPath)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("请选择图片存储目录以外的位置。");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(_bitmap));
            using var stream = File.Create(dialog.FileName); encoder.Save(stream); _status.Text = "已另存，导出的图片不会自动清理。";
        }
        catch (Exception error) { _status.Text = $"另存失败：{error.Message}"; }
    }
}
