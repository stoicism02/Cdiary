using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace Cdiary;

public partial class TaskNoteWindow : Window
{
    private readonly DiaryRepository _repository;
    private readonly DispatcherTimer _saveTimer;
    private bool _dirty;
    private bool _documentCloseReady, _documentClosing;

    public TaskNoteWindow(DiaryTask task, DiaryRepository repository)
    {
        Task = task;
        _repository = repository;
        InitializeComponent();
        NoteEditor.DocumentRepository = repository;
        NoteEditor.WritingOnly = true;
        NoteEditor.LostKeyboardFocus += async (_, _) =>
        {
            try { await NoteEditor.FlushDocumentAsync(deferComposition: true); FlushEdits(); }
            catch (Exception error) { NoteEditor.ShowSaveNotice("尚未保存：" + error.Message); }
        };
        Deactivated += async (_, _) =>
        {
            try { await NoteEditor.FlushDocumentAsync(deferComposition: true); FlushEdits(); }
            catch (Exception error) { NoteEditor.ShowSaveNotice("尚未保存：" + error.Message); }
        };
        DataContext = task;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => FlushEdits();
        Task.PropertyChanged += TaskChanged;
        Title = string.IsNullOrWhiteSpace(Task.Title) ? "任务便签" : $"{Task.Title} · 小岛日记";
    }

    public DiaryTask Task { get; }
    public void AttachFocus(FocusStore store,FocusService service,Action<DiaryTask> open)
    { NoteFocus.Task=Task;NoteFocus.Attach(store,service);NoteFocus.OpenRequested+=open;NoteFocus.Visibility=Visibility.Visible; }

    private void TaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiaryTask.Title))
            Title = string.IsNullOrWhiteSpace(Task.Title) ? "任务便签" : $"{Task.Title} · 小岛日记";
        if (e.PropertyName is nameof(DiaryTask.Title) or nameof(DiaryTask.Note))
        {
            _dirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        }
    }

    public void FlushEdits()
    {
        _saveTimer.Stop();
        if (!_dirty) return;
        _repository.SaveTask(Task);
        _dirty = false;
    }

    public async System.Threading.Tasks.Task PrepareDocumentExitAsync()
    {
        await NoteEditor.FlushDocumentAsync();
        FlushEdits();
        _documentCloseReady = true;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!_documentCloseReady && NoteEditor.IsDocumentReady)
        {
            e.Cancel = true;
            if (_documentClosing) return;
            _documentClosing = true;
            try { await PrepareDocumentExitAsync(); Close(); }
            catch (Exception error) { System.Windows.MessageBox.Show(this, "笔记尚未保存，窗口保持打开。" + error.Message, "小岛日记"); }
            finally { _documentClosing = false; }
            return;
        }
        FlushEdits();
        Task.PropertyChanged -= TaskChanged;
        base.OnClosing(e);
    }

    public void SetPinned(bool pinned) => Topmost = pinned;

    private void Content_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Height < 340;
        TitleEditor.MaxHeight = compact ? 40 : 72;
        TitleEditor.Margin = new Thickness(0, 0, 0, compact ? 6 : 10);
        if (compact) TitleEditor.Padding = new Thickness(8, 4, 8, 4);
        else TitleEditor.ClearValue(System.Windows.Controls.Control.PaddingProperty);
        SaveHint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
