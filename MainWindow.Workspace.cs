using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Cdiary;

public partial class MainWindow
{
    private readonly DispatcherTimer _completionNoticeTimer = new() { Interval = TimeSpan.FromSeconds(8) };

    private void InitializeWorkspaceFeedback()
    {
        _completionNoticeTimer.Tick += (_, _) => DismissCompletionNotice();
        Closed += (_, _) => _completionNoticeTimer.Stop();
    }

    private void ShowCompletionNotice(DiaryTask task)
    {
        CompletionNoticeText.Text = $"已完成：{task.Title}";
        RestoreNoticeButton.Tag = task;
        CompletionNotice.Visibility = Visibility.Visible;
        _completionNoticeTimer.Stop();
        _completionNoticeTimer.Start();
    }

    private void DismissCompletionNotice()
    {
        _completionNoticeTimer.Stop();
        CompletionNotice.Visibility = Visibility.Collapsed;
        RestoreNoticeButton.Tag = null;
    }

    private void RestoreNotice_Click(object sender, RoutedEventArgs e)
    {
        if (RestoreNoticeButton.Tag is DiaryTask task && _repository.FindTask(task.Id) is not null)
            RestoreCompleted_Click(RestoreNoticeButton, e);
        DismissCompletionNotice();
    }

    private void SettingsSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string section }) return;
        FrameworkElement target = section switch
        { "capture" => ScreenshotSettings, "images" => AttachmentSection, _ => StartupSection };
        target.BringIntoView();
    }
}
