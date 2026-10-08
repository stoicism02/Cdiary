using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Cdiary;

public partial class ScreenshotPage : System.Windows.Controls.UserControl
{
    private ScreenshotService? _service;
    private IReadOnlyList<ScreenshotEntry> _entries = [];
    private readonly HashSet<string> _selected = [];
    private int _version;
    private bool _busy;
    private string? _anchor, _pendingViewportAnchor;
    private int _columns = 3, _layoutVersion;
    private static readonly DependencyProperty ThumbnailTokenProperty = DependencyProperty.RegisterAttached(
        "ThumbnailToken", typeof(object), typeof(ScreenshotPage));
    public event Action? SettingsRequested;
    public ScreenshotPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible) _ = RefreshAsync(); };
    }
    public void Attach(ScreenshotService service)
    {
        _service = service;
        service.Changed += () => { if (IsVisible) _ = RefreshAsync(); };
        service.Notice += message => Status.Text = message;
        _ = RefreshAsync();
    }
    public async Task RefreshAsync()
    {
        if (_service is null) return;
        var version = ++_version;
        try
        {
            var entries = await Task.Run(_service.Store.Load);
            if (version != _version) return;
            _entries = entries;
            _selected.IntersectWith(entries.Select(entry => entry.Id));
            RebuildRows();
            Summary.Text = $"{entries.Count} 张截图 · {entries.Sum(entry => entry.Bytes) / 1048576d:N1} / {_service.Options.CapacityMb} MB · 保留 {_service.Options.RetentionDays} 个自然日";
            EmptyHint.Text = _service.Options.Enabled ? $"按 {_service.Options.HotkeyLabel}，框选想留下的画面" : "点击“截一张”，框选想留下的画面";
            EmptyState.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateSelection();
        }
        catch (Exception error) { Status.Text = $"读取截图箱失败：{error.Message}"; }
    }
    public sealed record PhotoRow(string Header, ScreenshotEntry[] Items, int Columns);
    private void RebuildRows()
    {
        var scroll = FindChild<ScrollViewer>(Rows);
        var oldIndex = (int)(scroll?.VerticalOffset ?? 0);
        var anchor = _pendingViewportAnchor ?? (oldIndex < Rows.Items.Count ? (Rows.Items[oldIndex] as PhotoRow)?.Items.FirstOrDefault()?.Id : null);
        var rows = new List<PhotoRow>();
        foreach (var group in _entries.GroupBy(entry => entry.CapturedAt.Date))
        {
            var chunks = group.Chunk(_columns).ToArray();
            for (var index = 0; index < chunks.Length; index++)
                rows.Add(new(index == 0 ? $"{chunks[index][0].DateLabel}  ·  {group.Count()} 张" : "", chunks[index], _columns));
        }
        var version = ++_layoutVersion;
        Rows.ItemsSource = rows;
        _pendingViewportAnchor = null;
        if (anchor is null) return;
        var newIndex = rows.FindIndex(row => row.Items.Any(entry => entry.Id == anchor));
        if (newIndex < 0) return;
        _pendingViewportAnchor = anchor;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (version != _layoutVersion) return;
            FindChild<ScrollViewer>(Rows)?.ScrollToVerticalOffset(newIndex);
            _pendingViewportAnchor = null;
        }));
    }
    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } descendant) return descendant;
        }
        return null;
    }
    private void Rows_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0) return;
        var columns = ResponsiveLayout.GalleryColumns(e.NewSize.Width - 20);
        if (columns == _columns) return;
        _columns = columns;
        RebuildRows();
    }
    private void ThumbnailFrame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && e.NewSize.Width > 0)
            ((Border)sender).Height = Math.Clamp(e.NewSize.Width * 9 / 16, 100, 210);
    }
    private async void Capture_Click(object sender, RoutedEventArgs e) { if (_service is not null) await _service.CaptureAsync(); }
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void Selection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox { Tag: string id } check) return;
        if (check.IsChecked == true) _selected.Add(id); else _selected.Remove(id);
        _anchor = id; UpdateSelection(); e.Handled = true;
    }
    private void Selection_Loaded(object sender, RoutedEventArgs e)
    { if (sender is System.Windows.Controls.CheckBox { Tag: string id } check) check.IsChecked = _selected.Contains(id); }
    private void UpdateSelection()
    {
        CopyButton.IsEnabled = _selected.Count == 1; ExportButton.IsEnabled = DeleteButton.IsEnabled = _selected.Count > 0 && !_busy;
        DeletePrompt.Visibility = Visibility.Collapsed;
    }
    private void RefreshChecks(DependencyObject element)
    {
        if (element is System.Windows.Controls.CheckBox { Tag: string id } check) check.IsChecked = _selected.Contains(id);
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); i++)
            RefreshChecks(System.Windows.Media.VisualTreeHelper.GetChild(element, i));
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_selected.Count == _entries.Count) _selected.Clear(); else foreach (var entry in _entries) _selected.Add(entry.Id);
        RefreshChecks(Rows); UpdateSelection();
    }
    private async void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: ScreenshotEntry entry } || e.OriginalSource is System.Windows.Controls.CheckBox) return;
        for (var source = e.OriginalSource as DependencyObject; source is not null && !ReferenceEquals(source, sender);
             source = System.Windows.Media.VisualTreeHelper.GetParent(source))
            if (source is System.Windows.Controls.CheckBox) return;
        if (e.ClickCount == 2)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(entry.OriginalPath);
                var viewer = new ScreenshotViewer(entry, ScreenshotService.Decode(bytes), Window.GetWindow(this)); viewer.Show();
            }
            catch (Exception error) { Status.Text = $"无法打开图片，可能已经到期：{error.Message}"; await RefreshAsync(); }
        }
        else
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _anchor is not null)
            {
                var list = _entries.ToList(); var start = list.FindIndex(item => item.Id == _anchor); var end = list.IndexOf(entry);
                if (start >= 0) foreach (var item in list.Skip(Math.Min(start, end)).Take(Math.Abs(end - start) + 1)) _selected.Add(item.Id);
            }
            else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            { if (!_selected.Add(entry.Id)) _selected.Remove(entry.Id); _anchor = entry.Id; }
            else { _selected.Clear(); _selected.Add(entry.Id); _anchor = entry.Id; }
            RefreshChecks(Rows); UpdateSelection(); Rows.Focus();
        }
    }
    private async void Copy_Click(object sender, RoutedEventArgs e) => await CopySelectedAsync();
    private async Task CopySelectedAsync()
    {
        var entry = _entries.SingleOrDefault(item => _selected.Contains(item.Id) && _selected.Count == 1);
        if (entry is null) return;
        try { await ScreenshotService.CopyAsync(ScreenshotService.Decode(await File.ReadAllBytesAsync(entry.OriginalPath))); Status.Text = "已复制图片，可以直接粘贴。"; }
        catch (Exception error) { Status.Text = $"复制失败：{error.Message}"; }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var entries = _entries.Where(entry => _selected.Contains(entry.Id)).ToArray();
        if (entries.Length == 0 || _busy) return;
        string destination;
        if (entries.Length == 1)
        {
            var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = $"截图_{entries[0].CapturedAt:yyyyMMdd_HHmmss}.png" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            destination = dialog.FileName;
        }
        else
        {
            var dialog = new OpenFolderDialog { Title = "将选中的截图另存到" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            destination = dialog.FolderName;
        }
        _busy = true; UpdateSelection();
        try
        {
            var copied = await Task.Run(() =>
            {
                var count = 0;
                foreach (var entry in entries)
                {
                    if (!File.Exists(entry.OriginalPath)) continue;
                    var target = entries.Length == 1 ? destination : Path.Combine(destination, $"截图_{entry.CapturedAt:yyyyMMdd_HHmmss}_{entry.Id}.png");
                    if (_service is not null && Path.GetFullPath(target).StartsWith(_service.Store.DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("请选择截图箱以外的目录，避免另存的图片被自动清理。");
                    File.Copy(entry.OriginalPath, target, entries.Length == 1); count++;
                }
                return count;
            });
            Status.Text = $"已另存 {copied} / {entries.Length} 张，导出的图片不会自动清理。";
        }
        catch (Exception error) { Status.Text = $"另存未全部完成：{error.Message}"; }
        finally { _busy = false; UpdateSelection(); }
    }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected.Count == 0) return;
        DeleteText.Text = $"删除选中的 {_selected.Count} 张临时截图？无法撤销。";
        DeletePrompt.Visibility = Visibility.Visible;
    }
    private void CancelDelete_Click(object sender, RoutedEventArgs e) => DeletePrompt.Visibility = Visibility.Collapsed;
    private async void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || _busy) return;
        var ids = _selected.ToArray(); _busy = true; UpdateSelection();
        try
        {
            await Task.Run(() => { foreach (var id in ids) _service.Store.Delete(id); });
            Status.Text = $"已删除 {ids.Length} 张截图。";
        }
        catch (Exception error) { Status.Text = $"部分图片未删除：{error.Message}"; }
        finally { _busy = false; await RefreshAsync(); }
    }
    private async void Gallery_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { SelectAll_Click(sender, e); e.Handled = true; }
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { await CopySelectedAsync(); e.Handled = true; }
        if (e.Key == Key.Delete) { Delete_Click(sender, e); e.Handled = true; }
    }
    private async void Thumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Image { Tag: string path } image) return;
        var token = new object(); image.SetValue(ThumbnailTokenProperty, token);
        image.Source = null;
        try
        {
            var bitmap = await Task.Run(() => ScreenshotService.Decode(File.ReadAllBytes(path)));
            if (ReferenceEquals(image.GetValue(ThumbnailTokenProperty), token) && Equals(image.Tag, path)) image.Source = bitmap;
        }
        catch (IOException) { image.Source = null; } catch (UnauthorizedAccessException) { image.Source = null; }
        catch (NotSupportedException) { image.Source = null; }
        catch (System.IO.FileFormatException) { image.Source = null; }
    }
    private void Thumbnail_Unloaded(object sender, RoutedEventArgs e)
    {
        var image = (System.Windows.Controls.Image)sender;
        image.ClearValue(ThumbnailTokenProperty); image.Source = null;
    }
}
