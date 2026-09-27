using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Cdiary;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private Forms.NotifyIcon? _tray;
    private ReminderScheduler? _scheduler;
    private MainWindow? _window;
    private (DateTime Date, long Id)? _notificationTarget;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var background = e.Args.Any(arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));
        _singleInstance = new Mutex(true, @"Local\CdiarySingleInstance", out var isFirst);
        if (!isFirst)
        {
            if (!background)
                System.Windows.MessageBox.Show("小岛日记已经在运行，请从系统托盘打开。", "小岛日记",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            var repository = new DiaryRepository();
            StartupSettings.RefreshPathIfEnabled();
            CreateTray();
            _scheduler = new ReminderScheduler(repository, Dispatcher, ShowReminders);
            _window = new MainWindow(repository, _scheduler);
            MainWindow = _window;
            if (!background) _window.Show();
            _scheduler.Start();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show($"启动失败：{exception.Message}", "小岛日记", MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("打开小岛日记");
        openItem.Click += (_, _) => Dispatcher.Invoke(ShowWindow);
        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Dispatcher.Invoke(RequestExit);
        menu.Items.Add(openItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "小岛日记",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        _tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() =>
        {
            if (_notificationTarget is { } target && _window is not null)
            {
                ShowWindow();
                _window.ShowTask(target.Date, target.Id);
            }
            else ShowWindow();
        });
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ShowReminders(IReadOnlyList<DiaryTask> due)
    {
        if (_tray is null || due.Count == 0) return;
        _notificationTarget = (due[0].Date, due[0].Id);
        var title = due.Count == 1 ? "任务提醒" : $"{due.Count} 项任务待完成";
        var body = due.Count == 1
            ? due[0].Title
            : string.Join("、", due.Take(3).Select(task => task.Title)) + (due.Count > 3 ? "……" : "");
        _tray.ShowBalloonTip(5000, title, body, Forms.ToolTipIcon.Info);
    }

    private void RequestExit()
    {
        if (_window is not null)
        {
            _window.AllowExit = true;
            _window.Close();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _scheduler?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
        }
        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); }
            catch (ApplicationException) { }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }
}
