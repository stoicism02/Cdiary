using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Application = System.Windows.Application;

namespace Cdiary;

// All editor resources are local. The task model remains the authority for note contents.
public sealed partial class DocumentNoteHost : Grid, IDisposable
{
    private const string Origin = "https://island-editor.local";
    private static Task<CoreWebView2Environment>? _environment;
    private static Task<CoreWebView2Environment>? _compositionEnvironment;
    private readonly bool _transparent;
    private static int _activeViews;
    private readonly IWebView2 _web;
    private readonly Dictionary<string, DiaryTask> _sessions = new();
    private readonly Dictionary<long, DiaryTask> _dirty = new();
    private readonly Dictionary<long, string> _originals = new();
    private readonly HashSet<long> _backedUp = new();
    private readonly DispatcherTimer _saveTimer;
    private TaskCompletionSource? _ready;
    private DiaryTask? _task;
    private string _token = "";
    private bool _applying, _disposed, _initialized;
    public DiaryRepository? Repository { get; set; }
    public bool IsReady { get; private set; }
    public long? TaskId => _task?.Id;
    public CoreWebView2? Browser => _web.CoreWebView2;
    public event Action? SourceRequested;
    public event Action? PreviewRequested;
    public event Action<string>? Notice;
    public event Action? Ready;

    public DocumentNoteHost(bool transparent = false)
    {
        _transparent = transparent;
        _web = transparent
            ? new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.Transparent }
            : new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 255, 251, 234) };
        Children.Add((UIElement)_web);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => SavePending();
        IsVisibleChanged += async (_, _) =>
        {
            try
            {
                if (IsVisible)
                {
                    if (IsReady) _web.CoreWebView2.Resume();
                }
                else if (IsReady) { await FlushAsync(); await _web.CoreWebView2.TrySuspendAsync(); }
            }
            catch (Exception error) { if (!_disposed) Notice?.Invoke("文档模式暂不可用，可继续使用源码。" + error.Message); }
        };
        Unloaded += (_, _) => Dispose();
    }
    public void SetTask(DiaryTask? task)
    {
        if (ReferenceEquals(task, _task)) return;
        if (_task is not null) _task.PropertyChanged -= TaskChanged;
        _task = task;
        IsReadOnly = false;
        if(task is not null)Repository?.Attachments.Protect(task.Note);
        if (_task is not null) _task.PropertyChanged += TaskChanged;
        SendDocument();
    }
    private void TaskChanged(object? sender, PropertyChangedEventArgs args)
    { if (!_applying && args.PropertyName == nameof(DiaryTask.Note)) SendDocument(); }

    public async Task EnsureReadyAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DocumentNoteHost));
        if (_ready is not null) { await _ready.Task; return; }
        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "EditorWeb");
            if (!File.Exists(Path.Combine(folder, "index.html"))) throw new FileNotFoundException("未找到本地编辑器资源。");
            var dataFolder = Repository is not null ? Path.GetDirectoryName(Repository.DatabasePath)!
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cdiary");
            var environment = _transparent
                ? (_compositionEnvironment ??= CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(dataFolder, "EditorRuntimeComposition")))
                : (_environment ??= CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(dataFolder, "EditorRuntime")));
            await _web.EnsureCoreWebView2Async(await environment);
            if (_disposed) throw new ObjectDisposedException(nameof(DocumentNoteHost));
            _initialized = true; _activeViews++;
            var core = _web.CoreWebView2;
            // Virtual-host folder mappings bypass WebResourceRequested for missing files.
            // Serve the small local asset allowlist here so attachments share the same origin.
            var resources=Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories)
                .ToDictionary(path=>Origin+"/"+Path.GetRelativePath(folder,path).Replace('\\','/'),StringComparer.Ordinal);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += async (_, args) =>
            {
                if(args.Request.Uri.StartsWith(Origin+"/attachments/",StringComparison.Ordinal))
                    await ServeImageAsync(core,args);
                else if (!args.Request.Uri.StartsWith(Origin + "/", StringComparison.Ordinal))
                    args.Response = core.Environment.CreateWebResourceResponse(Stream.Null, 403, "Local resources only", "Content-Type: text/plain");
                else await ServeLocalResourceAsync(core,args,resources);
            };
            core.NavigationStarting += (_, args) => args.Cancel = args.Uri != Origin + "/index.html";
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.WebMessageReceived += (_, args) =>
            {
                if (args.Source != Origin + "/index.html") return;
                try { Receive(args.WebMessageAsJson); }
                catch (Exception error) { Notice?.Invoke("保存失败，请先复制笔记原文：" + error.Message); }
            };
            core.ProcessFailed += (_, _) =>
            {
                IsReady = false; SavePending(); Notice?.Invoke("文档编辑器已停止，可在源码中继续编辑已同步的内容。"); SourceRequested?.Invoke();
            };
            core.Navigate(Origin + "/index.html");
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception error)
        {
            _ready.TrySetException(error);
            if (!_disposed)
            {
                Notice?.Invoke("无法打开文档模式，已保留源码编辑。" + error.Message);
                SourceRequested?.Invoke();
            }
            throw;
        }
    }
    private void SendDocument()
    {
        if (!IsReady || _disposed) return;
        _token = Guid.NewGuid().ToString("N");
        _contentReady?.TrySetResult();
        _contentReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_task is not null) _sessions[_token] = _task;
        // Retain recent identities until their queued input has reached the host.
        if (_sessions.Count > 32) _sessions.Remove(_sessions.Keys.First());
        Send(new { kind = "load", token = _token, taskId = _task?.Id ?? 0, text = _task?.Note ?? "", revision = _task?.NoteRevision ?? 0, sticky = StickyMode, readOnly = IsReadOnly, writingOnly = WritingOnly });
    }
    private void Send(object message)
    { if (!_disposed && IsReady) _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
    private void Receive(string json)
    {
        using var document = JsonDocument.Parse(json);
        var message = document.RootElement;
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("kind", out var kind)) return;
        switch (kind.GetString())
        {
            case "ready": IsReady = true; SendDocument(); SetOutlineEnabled(_outlineEnabled); _ready?.TrySetResult(); Ready?.Invoke(); break;
            case "change": ApplyChange(message); break;
            case "contentReady": if (message.GetProperty("token").GetString() == _token) _contentReady?.TrySetResult(); break;
            case "presentationReady": if (message.GetProperty("request").GetString() == _presentationRequest) _presentationReady?.TrySetResult(); break;
            case "saveReadOnly": SaveReadOnlyRequested(message.GetProperty("token").GetString() ?? ""); break;
            case "saveNow": SaveNowRequested(message.GetProperty("token").GetString() ?? ""); break;
            case "editingResumed": if (message.GetProperty("token").GetString() == _token) IsReadOnly = false; break;
            case "outlineToggle": OutlineRequested?.Invoke(); break;
            case "outlineDismiss": OutlineDismissRequested?.Invoke(); break;
            case "outline": ReceiveOutline(message, false); break;
            case "outlineActive": ReceiveOutline(message, true); break;
            case "imageImport": BeginImageImport(message.Clone()); break;
            case "imageOpen":
                if(Repository is not null)_=NoteAttachmentViewer.OpenAsync(Repository.Attachments,message.GetProperty("reference").GetString()??"",Window.GetWindow(this));
                break;
            case "source": SourceRequested?.Invoke(); break;
            case "preview": PreviewRequested?.Invoke(); break;
            case "unsupported": Notice?.Invoke(message.GetProperty("text").GetString()!); SourceRequested?.Invoke(); break;
            case "backup": ExportBackup(false); break;
            case "recovery": ExportBackup(true); break;
            case "link":
                var url = message.GetProperty("url").GetString() ?? "";
                if (MarkdownNoteView.IsWebLink(url)) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                break;
            case "error": Notice?.Invoke("编辑器提示：" + message.GetProperty("text").GetString()); break;
        }
    }
    private void ApplyChange(JsonElement message)
    {
        var token = message.GetProperty("token").GetString() ?? "";
        if (!_sessions.TryGetValue(token, out var task) || task.Id != message.GetProperty("taskId").GetInt64()) return;
        var markdown = message.GetProperty("markdown").GetString() ?? "";
        Repository?.Attachments.Protect(markdown);
        if (task.Note == markdown) return;
        if (task.NoteRevision != message.GetProperty("revision").GetInt64() || task.Note != message.GetProperty("baseText").GetString())
        {
            Repository?.SaveNoteRecovery(task.Id, markdown);
            Notice?.Invoke("另一窗口已修改这份笔记，已保留未合并的输入，可从“更多 → 找回”导出。");
            SendDocument(); return;
        }
        if (!_backedUp.Contains(task.Id)) _originals.TryAdd(task.Id, task.Note);
        _applying = true;
        try { task.Note = markdown; } finally { _applying = false; }
        _dirty[task.Id] = task; _saveTimer.Stop(); _saveTimer.Start();
    }
    private bool SavePending()
    {
        _saveTimer.Stop();
        try
        {
            foreach (var task in _dirty.Values)
            {
                if (Repository is null) throw new InvalidOperationException("未连接笔记数据库。");
                if (_originals.TryGetValue(task.Id, out var original))
                {
                    Repository.BackupOriginalNote(task.Id, original);
                    _backedUp.Add(task.Id); _originals.Remove(task.Id);
                }
                Repository.SaveDocumentNote(task);
            }
            _dirty.Clear();
            if (_task is not null) Send(new { kind = "saved", token = _token, revision = _task.NoteRevision });
            return true;
        }
        catch (Exception error) { Notice?.Invoke("保存失败，内容仍保留在编辑器中：" + error.Message); return false; }
    }
    public async Task FlushAsync(bool deferComposition = false)
    {
        if (IsReady && !_disposed)
        {
            _web.CoreWebView2.Resume();
            var composing = await _web.ExecuteScriptAsync("window.island?.composing ?? false");
            if (composing == "true")
            {
                if (deferComposition) return;
                throw new InvalidOperationException("请先确认输入法候选文字，再结束编辑。");
            }
            var pending = await _web.ExecuteScriptAsync("window.island?.flush() ?? null");
            if (pending != "null") Receive(pending);
        }
        if (!SavePending()) throw new IOException("笔记尚未写入数据库。");
    }
    private void ExportBackup(bool recovery)
    {
        if (_task is null) return;
        var text = Repository?.ReadNoteBackup(_task.Id, recovery);
        if (text is null) { Send(new { kind = "status", text = recovery ? "暂无需要找回的输入" : "首次修改后会保留编辑前备份" }); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Markdown 笔记|*.md", FileName = $"任务笔记-{_task.Id}-{(recovery ? "找回" : "备份")}.md" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) File.WriteAllText(dialog.FileName, text);
    }
    public void Dispose()
    {
        if (_disposed) return;
        SavePending(); _disposed = true; IsReady = false;
        _ready?.TrySetException(new ObjectDisposedException(nameof(DocumentNoteHost)));
        _contentReady?.TrySetResult();
        _presentationReady?.TrySetResult();
        if (_task is not null) _task.PropertyChanged -= TaskChanged;
        _sessions.Clear(); _web.Dispose();
        if (_initialized && --_activeViews == 0) { _environment = null; _compositionEnvironment = null; }
    }
}
