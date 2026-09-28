using System.Windows;
using System.Windows.Input;

namespace Cdiary;

public partial class RepeatRuleWindow : Window
{
    private readonly DiaryRepository _repository;
    private readonly DiaryTask _task;
    private readonly System.Windows.Controls.CheckBox[] _days;

    public RepeatRuleWindow(DiaryRepository repository, DiaryTask task)
    {
        _repository = repository;
        _task = task;
        InitializeComponent();
        _days = [Sunday, Monday, Tuesday, Wednesday, Thursday, Friday, Saturday];
        var rule = task.RecurrenceRuleId is long id ? repository.LoadRule(id) : null;
        WeeklyRadio.IsChecked = rule?.IsWeekly == true;
        DailyRadio.IsChecked = rule?.IsWeekly != true;
        var mask = rule?.WeekdaysMask ?? 1 << (int)task.Date.DayOfWeek;
        for (var day = 0; day < 7; day++) _days[day].IsChecked = (mask & (1 << day)) != 0;
        TitleBox.Text = rule?.Title ?? task.Title;
        HourBox.ItemsSource = Enumerable.Range(0, 24).Select(hour => hour.ToString("00")).ToArray();
        MinuteBox.ItemsSource = Enumerable.Range(0, 60).Select(minute => minute.ToString("00")).ToArray();
        var reminder = rule?.ReminderTime ?? task.ReminderTime;
        HourBox.SelectedIndex = reminder?.Hours ?? 9;
        MinuteBox.SelectedIndex = reminder?.Minutes ?? 0;
        ReminderCheck.IsChecked = reminder is not null;
        StopButton.Visibility = rule is null || rule.EndDate is not null ? Visibility.Collapsed : Visibility.Visible;
        WeekdaysPanel.Visibility = WeeklyRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TimePanel.IsEnabled = ReminderCheck.IsChecked == true;
    }

    private void FrequencyChanged(object sender, RoutedEventArgs e)
    {
        if (WeekdaysPanel is not null)
            WeekdaysPanel.Visibility = WeeklyRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ReminderChanged(object sender, RoutedEventArgs e)
    {
        if (TimePanel is not null) TimePanel.IsEnabled = ReminderCheck.IsChecked == true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        if (title.Length == 0) { Warn("请填写后续任务的标题。"); return; }
        var mask = 0;
        for (var day = 0; day < 7; day++) if (_days[day].IsChecked == true) mask |= 1 << day;
        if (WeeklyRadio.IsChecked == true && mask == 0) { Warn("每周重复请至少选择一天。"); return; }
        TimeSpan? reminder = ReminderCheck.IsChecked == true
            ? new TimeSpan(HourBox.SelectedIndex, MinuteBox.SelectedIndex, 0)
            : null;
        try
        {
            _repository.SaveRule(_task, WeeklyRadio.IsChecked == true, mask, title, reminder);
            DialogResult = true;
        }
        catch (Exception error) { Warn($"保存失败：{error.Message}"); }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_task.RecurrenceRuleId is not long id) return;
        try
        {
            _repository.StopRule(id, DateTime.Today.AddDays(-1));
            DialogResult = true;
        }
        catch (Exception error) { Warn($"停止失败：{error.Message}"); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Warn(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }
}
