using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace Cdiary;

public sealed class StyleHotReload : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly FileSystemWatcher _watcher;
    private readonly DispatcherTimer _debounce;
    private readonly LiveStyleDictionary.StyleScope _scope;
    private readonly Dictionary<object, Type> _required;
    private readonly Dictionary<object, Type> _styleTargets;
    private int _queued;
    private int _attempt;
    private bool _disposed;
    private string? _lastApplied;
    public string FilePath { get; }
    public string Status { get; private set; } = "等待样式加载";
    public bool LastReloadSucceeded { get; private set; }
    public int Revision { get; private set; }
    public event Action<bool, string>? StatusChanged;

    public StyleHotReload(string path, Dispatcher dispatcher)
    {
        FilePath = Path.GetFullPath(path); _dispatcher = dispatcher;
        dispatcher.VerifyAccess(); _scope = LiveStyleDictionary.ForDispatcher(dispatcher);
        _required = _scope.Current.Keys.Cast<object>().ToDictionary(key => key, key => _scope.Current[key].GetType());
        _styleTargets = _scope.Current.Keys.Cast<object>().Where(key => _scope.Current[key] is Style)
            .ToDictionary(key => key, key => ((Style)_scope.Current[key]).TargetType);
        _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += OnDebounce;
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(FilePath)!, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false
        };
        _watcher.Changed += FileChanged; _watcher.Created += FileChanged; _watcher.Deleted += FileChanged;
        _watcher.Renamed += FileRenamed; _watcher.Error += WatcherError;
        _watcher.EnableRaisingEvents = true;
        ReloadNow();
    }

    private void FileChanged(object sender, FileSystemEventArgs e) => QueueReload();
    private void FileRenamed(object sender, RenamedEventArgs e) => QueueReload();
    private void WatcherError(object sender, ErrorEventArgs e)
    {
        QueueReload();
        if (!_disposed && !_dispatcher.HasShutdownStarted)
            _dispatcher.BeginInvoke(new Action(() => Report(false, $"样式监听异常：{e.GetException().Message}。可用托盘“重新加载样式”重试。")));
    }
    private void QueueReload()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _queued, 1) != 0) return;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref _queued, 0);
            if (_disposed) return;
            _attempt = 0; _debounce.Stop(); _debounce.Start();
        }));
    }
    private void OnDebounce(object? sender, EventArgs e)
    {
        _debounce.Stop();
        if (!TryReload(out var error) && ++_attempt < 3) _debounce.Start();
        else if (error is not null) Report(false, error);
    }
    public bool ReloadNow()
    {
        _dispatcher.VerifyAccess(); _debounce.Stop();
        var success = TryReload(out var error);
        if (!success && error is not null) Report(false, error);
        return success;
    }
    private bool TryReload(out string? error)
    {
        error = null;
        if (_disposed) return false;
        try
        {
            // Editors may replace the file by rename or hold it briefly while saving.
            using var file = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file); var text = reader.ReadToEnd();
            if (text == _lastApplied)
            { if (!LastReloadSucceeded) Report(true, "样式已恢复，保留当前界面。"); return true; }
            using var xml = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            if (XamlReader.Load(xml) is not ResourceDictionary candidate)
                throw new InvalidDataException("样式文件的根元素必须是 ResourceDictionary。");
            if (candidate.MergedDictionaries.Count != 0)
                throw new InvalidDataException("共享样式请放在同一个文件中，以便完整验证和更新。");
            foreach (var pair in _required)
            {
                if (!candidate.Contains(pair.Key) || !pair.Value.IsInstanceOfType(candidate[pair.Key]))
                    throw new InvalidDataException($"缺少样式资源或类型不匹配：{pair.Key}");
            }
            foreach (var pair in _styleTargets)
                if (((Style)candidate[pair.Key]).TargetType != pair.Value)
                    throw new InvalidDataException($"不能更改共享样式的控件类型：{pair.Key}");
            // Force deferred styles/templates to load before changing any live resource scope.
            foreach (var key in candidate.Keys.Cast<object>().ToArray())
            {
                if (candidate[key] is not Style style) continue;
                style.Seal();
                if (Activator.CreateInstance(style.TargetType) is FrameworkElement probe)
                {
                    probe.Resources.MergedDictionaries.Add(candidate);
                    probe.Style = style; probe.ApplyTemplate();
                    probe.Measure(new System.Windows.Size(800, 600)); probe.Arrange(new Rect(probe.DesiredSize));
                }
            }
            _scope.Replace(candidate); _lastApplied = text; Revision++;
            Report(true, $"样式已更新 {DateTime.Now:HH:mm:ss}"); return true;
        }
        catch (Exception exception)
        {
            error = $"样式未更新，已保留上一版：{exception.GetBaseException().Message}"; return false;
        }
    }
    private void Report(bool success, string message)
    {
        LastReloadSucceeded = success; Status = message;
        if (!_disposed) StatusChanged?.Invoke(success, message);
    }
    public void Dispose()
    {
        _dispatcher.VerifyAccess(); _disposed = true; _debounce.Stop();
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= FileChanged; _watcher.Created -= FileChanged; _watcher.Deleted -= FileChanged;
        _watcher.Renamed -= FileRenamed; _watcher.Error -= WatcherError; _watcher.Dispose();
        StatusChanged = null;
    }
}
