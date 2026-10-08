using System.Windows;
using System.Windows.Input;

namespace Cdiary;

public partial class ScreenshotSettingsView : System.Windows.Controls.UserControl
{
    private ScreenshotService? _service;
    private uint _modifiers = 3, _key = 0x53;
    private ScreenshotOptions? _pending;
    private bool? _stacked;
    private void Fields_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < 560;
        if (_stacked == stacked) return;
        _stacked = stacked;
        LabelColumn.Width = new GridLength(stacked ? 0 : 145);
        FrameworkElement[] labels = [HotkeyLabel, DaysLabel, CapacityLabel];
        FrameworkElement[] fields = [HotkeyField, DaysField, CapacityField];
        for (var index = 0; index < fields.Length; index++)
        {
            System.Windows.Controls.Grid.SetColumnSpan(labels[index], stacked ? 2 : 1);
            System.Windows.Controls.Grid.SetRow(fields[index], index * 2 + (stacked ? 1 : 0));
            System.Windows.Controls.Grid.SetColumn(fields[index], stacked ? 0 : 1);
            System.Windows.Controls.Grid.SetColumnSpan(fields[index], stacked ? 2 : 1);
            fields[index].Margin = new Thickness(0, stacked ? 7 : index == 0 ? 0 : 13, 0, 0);
        }
    }
    public ScreenshotSettingsView()
    {
        InitializeComponent(); HotkeyBox.Text = new ScreenshotOptions().HotkeyLabel;
        DaysBox.TextChanged += (_, _) => ResetPending(); CapacityBox.TextChanged += (_, _) => ResetPending();
        EnabledCheck.Click += (_, _) => ResetPending();
        HotkeyBox.GotKeyboardFocus += (_, _) => _service?.EditHotkey(true);
        HotkeyBox.LostKeyboardFocus += (_, _) => _service?.EditHotkey(false);
        IsVisibleChanged += (_, _) => { if (!IsVisible) _service?.EditHotkey(false); };
    }
    public void Attach(ScreenshotService service)
    {
        _service = service; var options = service.Options;
        DaysBox.Text = options.RetentionDays.ToString(); CapacityBox.Text = options.CapacityMb.ToString();
        EnabledCheck.IsChecked = options.Enabled; _modifiers = options.Modifiers; _key = options.Key;
        HotkeyBox.Text = options.HotkeyLabel; Status.Text = service.HotkeyStatus;
    }
    private void ResetPending() { _pending = null; ApplyButton.Visibility = Visibility.Collapsed; }
    private void Hotkey_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Tab) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) return;
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Windows) != 0) { Status.Text = "请使用 Ctrl 或 Alt 组合，保留 Windows 系统快捷键。"; return; }
        var native = (uint)(((modifiers & ModifierKeys.Control) != 0 ? 2 : 0) |
            ((modifiers & ModifierKeys.Alt) != 0 ? 1 : 0) | ((modifiers & ModifierKeys.Shift) != 0 ? 4 : 0));
        try
        {
            var options = new ScreenshotOptions(Modifiers: native, Key: (uint)KeyInterop.VirtualKeyFromKey(key)); options.Validate();
            _modifiers = native; _key = options.Key; HotkeyBox.Text = options.HotkeyLabel; ResetPending(); Status.Text = "点击保存后生效。";
        }
        catch (ArgumentException error) { Status.Text = error.Message; }
    }
    private ScreenshotOptions ReadOptions()
    {
        if (!int.TryParse(DaysBox.Text, out var days) || !int.TryParse(CapacityBox.Text, out var capacity))
            throw new ArgumentException("保留天数和容量请输入整数。");
        var options = new ScreenshotOptions(days, capacity, _modifiers, _key, EnabledCheck.IsChecked == true);
        options.Validate(); return options;
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        IsEnabled = false;
        try
        {
            var options = ReadOptions();
            var entries = await Task.Run(_service.Store.Load);
            var count = entries.Count(entry => ScreenshotStore.Expiry(entry.CapturedAt, options.RetentionDays) <= DateTime.Now);
            if (count > 0)
            {
                _pending = options; ApplyButton.Visibility = Visibility.Visible;
                Status.Text = $"新规则将立即清理 {count} 张已过期截图。请先另存重要图片，再确认保存。";
            }
            else await SaveAsync(options);
        }
        catch (Exception error) { Status.Text = error.Message; }
        finally { IsEnabled = true; }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e) { if (_pending is { } options) await SaveAsync(options); }
    private async Task SaveAsync(ScreenshotOptions options)
    {
        if (_service is null) return;
        IsEnabled = false;
        try
        {
            await _service.SaveOptionsAsync(options); ResetPending();
            Status.Text = $"已保存。保留 {options.RetentionDays} 个自然日，下次定时清理：{DateTime.Today.AddDays(1):M月d日 HH:mm}。{_service.HotkeyStatus}";
        }
        catch (Exception error) { Status.Text = $"保存失败：{error.Message}"; }
        finally { IsEnabled = true; }
    }
}
