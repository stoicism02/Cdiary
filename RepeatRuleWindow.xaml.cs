using System.Globalization;
using System.Windows;
using System.Windows.Controls;

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
        NoteBox.Text = rule?.NoteTemplate ?? task.Note;
        ReminderBox.Text = rule?.ReminderTime?.ToString(@"hh\:mm") ?? task.ReminderLabel;
        StopButton.Visibility = rule is null || rule.EndDate is not null ? Visibility.Collapsed : Visibility.Visible;
        WeekdaysPanel.IsEnabled = WeeklyRadio.IsChecked == true;
    }

    private void FrequencyChanged(object sender, RoutedEventArgs e)
    {
        if (WeekdaysPanel is not null) WeekdaysPanel.IsEnabled = WeeklyRadio.IsChecked == true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        if (title.Length == 0) { Warn("请填写后续任务的标题。"); return; }
        var mask = 0;
        for (var day = 0; day < 7; day++) if (_days[day].IsChecked == true) mask |= 1 << day;
        if (WeeklyRadio.IsChecked == true && mask == 0) { Warn("每周重复请至少选择一天。"); return; }
        TimeSpan? reminder = null;
        var reminderText = ReminderBox.Text.Trim();
        if (reminderText.Length > 0)
        {
            if (!TimeSpan.TryParseExact(reminderText, @"hh\:mm", CultureInfo.InvariantCulture, out var time)
                || time >= TimeSpan.FromDays(1))
            { Warn("提醒时间请使用 00:00 至 23:59，例如 14:30。"); return; }
            reminder = time;
        }
        try
        {
            _repository.SaveRule(_task, WeeklyRadio.IsChecked == true, mask, title, NoteBox.Text, reminder);
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
    private void Warn(string message) => System.Windows.MessageBox.Show(this, message, "重复任务", MessageBoxButton.OK, MessageBoxImage.Information);
}
