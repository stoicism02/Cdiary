using System.Threading;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Cdiary;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _appIcon;
    private ReminderScheduler? _scheduler;
    private ScreenshotService? _screenshots;
    private MainWindow? _window;
    private (DateTime Date, long Id)? _notificationTarget;
    private bool _screenshotNotification;
    private StyleHotReload? _styleReload;
    private bool _development;
    private FocusService? _focus;
    private FocusRuntime? _focusRuntime;
    private bool _focusNotification;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var background = e.Args.Any(arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));
        _development = e.Args.Contains("--dev", StringComparer.OrdinalIgnoreCase);
        _singleInstance = new Mutex(true, _development ? @"Local\CdiaryDevelopmentPreview" : @"Local\CdiarySingleInstance", out var isFirst);
        if (!isFirst)
        {
            if (!background)
                System.Windows.MessageBox.Show(_development ? "开发预览已经在运行，请从开发预览托盘打开。" : "小岛日记已经在运行，请从系统托盘打开。", "小岛日记",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            var repository = _development ? DevelopmentPreview.OpenRepository(DevelopmentPreview.DefaultDataDirectory) : new DiaryRepository();
            if (!_development) StartupSettings.RefreshPathIfEnabled();
            if (_development)
            {
                var index = Array.FindIndex(e.Args, arg => string.Equals(arg, "--styles", StringComparison.OrdinalIgnoreCase));
                if (index >= 0 && index + 1 >= e.Args.Length) throw new ArgumentException("--styles 后需要共享样式文件路径。");
                var path = index >= 0 ? e.Args[index + 1] : Path.Combine(Environment.CurrentDirectory, "Styles", "SharedStyles.xaml");
                _styleReload = new StyleHotReload(path, Dispatcher);
            }
            CreateTray();
            _scheduler = new ReminderScheduler(repository, Dispatcher, ShowReminders);
            _screenshots = new ScreenshotService(new ScreenshotStore(_development
                ? Path.Combine(DevelopmentPreview.DefaultDataDirectory, "Screenshots") : null), Dispatcher, !_development);
            _screenshots.Notice += ShowScreenshotNotice;
            var focusStore = new FocusStore(repository);
            _focus = new FocusService(focusStore);
            repository.TaskEnding += (id,reason) => { if(_focus.Current?.TaskId==id) _focus.Finish(reason); };
            _focusRuntime = new FocusRuntime(_focus,Dispatcher);
            _focusRuntime.SaveFailed += error => _tray?.ShowBalloonTip(5000,"专注暂未保存",error.Message,Forms.ToolTipIcon.Warning);
            _focus.Finished += session =>
            {
                if(session.EndReason!=FocusEndReason.Elapsed)return;
                _notificationTarget=null;_screenshotNotification=false;_focusNotification=true;
                _tray?.ShowBalloonTip(5000,session.IsBreak?"休息结束":"本轮专注完成",session.IsBreak?"准备好后开始下一轮。":"已保存投入时间，可以休息一下了。",Forms.ToolTipIcon.Info);
                if(focusStore.LoadSettings().SoundEnabled)System.Media.SystemSounds.Asterisk.Play();
            };
            _focus.Changed += () =>
            {
                var s=_focus.Snapshot();
                if(_tray!=null)_tray.Text=s is{State:not FocusState.Finished}?$"小岛日记 · {(s.State==FocusState.Paused?"已暂停":"专注中")}":"小岛日记";
            };
            _window = new MainWindow(repository, _scheduler, _screenshots,focusStore,_focus);
            if (_development)
            {
                _window.ConfigureDevelopmentPreview();
                _styleReload!.StatusChanged += _window.ShowStyleReloadStatus;
                _window.ShowStyleReloadStatus(_styleReload.LastReloadSucceeded, _styleReload.Status);
            }
            MainWindow = _window;
            if (!background) _window.Show();
            if (!_development) _scheduler.Start();
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
        _appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem(_development ? "打开开发预览" : "打开小岛日记");
        openItem.Click += (_, _) => Dispatcher.Invoke(ShowWindow);
        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Dispatcher.Invoke(RequestExit);
        menu.Items.Add(openItem);
        var focusItem=new Forms.ToolStripMenuItem("打开专注");
        focusItem.Click+=(_,_)=>Dispatcher.Invoke(()=>{ShowWindow();_window?.ShowFocusPage();});menu.Items.Add(focusItem);
        var pauseFocus=new Forms.ToolStripMenuItem("暂停 / 继续专注");
        pauseFocus.Click+=(_,_)=>Dispatcher.Invoke(()=>
        {
            try{if(_focus?.Snapshot()?.State==FocusState.Paused)_focus.Resume();else _focus?.Pause();}
            catch(Exception error){_tray?.ShowBalloonTip(3000,"专注暂未保存",error.Message,Forms.ToolTipIcon.Warning);}
        });menu.Items.Add(pauseFocus);
        var screenshotItem = new Forms.ToolStripMenuItem("截一张");
        screenshotItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(async () =>
        {
            // Let the native tray menu close before freezing the desktop.
            await Task.Delay(160);
            if (_screenshots is not null) await _screenshots.CaptureAsync();
        }));
        menu.Items.Add(screenshotItem);
        if (_development)
        {
            var reloadItem = new Forms.ToolStripMenuItem("重新加载样式");
            reloadItem.Click += (_, _) => Dispatcher.Invoke(() => _styleReload?.ReloadNow());
            menu.Items.Add(reloadItem);
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _tray = new Forms.NotifyIcon
        {
            Icon = _appIcon ?? System.Drawing.SystemIcons.Application,
            Text = _development ? "小岛日记 · 开发预览" : "小岛日记",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        _tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() =>
        {
            if (_focusNotification && _window is not null) { ShowWindow(); _window.ShowFocusPage(); }
            else if (_screenshotNotification && _window is not null)
            {
                ShowWindow();
                _window.ShowScreenshots();
            }
            else if (_notificationTarget is { } target && _window is not null)
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
        _window.RefreshTodayOverview();
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ShowReminders(IReadOnlyList<DiaryTask> due)
    {
        _focusNotification=false;
        if (_tray is null || due.Count == 0) return;
        _screenshotNotification = false;
        _notificationTarget = (due[0].Date, due[0].Id);
        var title = due.Count == 1 ? "任务提醒" : $"{due.Count} 项任务待完成";
        var body = due.Count == 1
            ? due[0].Title
            : string.Join("、", due.Take(3).Select(task => task.Title)) + (due.Count > 3 ? "……" : "");
        _tray.ShowBalloonTip(5000, title, body, Forms.ToolTipIcon.Info);
    }

    private void ShowScreenshotNotice(string message)
    {
        _focusNotification=false;
        _notificationTarget = null;
        _screenshotNotification = true;
        _tray?.ShowBalloonTip(2500, "截图箱", message, Forms.ToolTipIcon.Info);
    }

    private bool _exitPending;
    private async void RequestExit()
    {
        if (_exitPending) return;
        _exitPending = true;
        try
        {
            if (_window is not null)
            {
                await _window.PrepareDocumentExitAsync();
                _focusRuntime?.Dispose();
                _window.AllowExit = true;
                _window.Close();
            }
            Shutdown();
        }
        catch (Exception error) { System.Windows.MessageBox.Show("数据尚未保存，请稍后重试退出。" + error.Message, "小岛日记"); }
        finally { _exitPending = false; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _styleReload?.Dispose();
        _scheduler?.Dispose();
        _screenshots?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
        }
        _appIcon?.Dispose();
        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); }
            catch (ApplicationException) { }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }
}
