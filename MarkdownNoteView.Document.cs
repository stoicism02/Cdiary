using System.Windows;
using Application = System.Windows.Application;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace Cdiary;

public partial class MarkdownNoteView
{
    private DocumentNoteHost? _documentHost;
    private bool _sourceChosen;
    private System.Threading.Tasks.Task? _finishingDocument;
    private System.Threading.Tasks.Task? _openingDocument;
    private int _documentEpoch;
    public bool RetainDocumentForReading { get; set; }
    public bool DisplayOnly { get; set; }
    public bool WritingOnly { get; set; }
    public DiaryRepository? DocumentRepository { get; set; }
    public bool IsDocumentReady => _documentHost?.IsReady == true;
    public void ShowSaveNotice(string text)
    {
        DocumentNotice.Text = text; DocumentNotice.Visibility = Visibility.Visible;
        Status.Text = text; Status.Visibility = Visibility.Visible;
    }
    private void InitializeDocumentMode()
    {
        Loaded += async (_, _) =>
        {
            if (WritingOnly && !_sourceChosen && IsVisible) await OpenDocumentAsync();
            else if (DisplayOnly || (!_sourceChosen && Application.Current is App)) SetMode(NoteViewMode.Preview);
        };
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible && _documentHost is not null) await FinishDocumentAsync(NoteViewMode.Preview);
            else if (IsLoaded && IsVisible && WritingOnly && !_sourceChosen) await OpenDocumentAsync();
        };
        Unloaded += (_, _) => { _documentEpoch++; _documentHost?.Dispose(); _documentHost = null; DocumentContainer.Child = null; };
    }
    private async void Document_Click(object sender, RoutedEventArgs args)
    { _sourceChosen = false; await OpenDocumentAsync(); }
    public System.Threading.Tasks.Task OpenDocumentAsync()
    {
        if (DisplayOnly) return System.Threading.Tasks.Task.CompletedTask;
        if (_openingDocument is { IsCompleted: false }) return _openingDocument;
        return _openingDocument = OpenDocumentCoreAsync();
    }
    private async System.Threading.Tasks.Task OpenDocumentCoreAsync()
    {
        DocumentNoteHost? openingHost = null;
        var epoch = ++_documentEpoch;
        var task = Task;
        var position = CaptureReadingPosition();
        try
        {
            if (_finishingDocument is not null) await _finishingDocument;
            if (epoch != _documentEpoch || Task != task) return;
            if (_documentHost is { IsReady: true }) return;
            if (_documentHost is null)
            {
                var createdHost = new DocumentNoteHost { Repository = DocumentRepository, StickyMode = RetainDocumentForReading, WritingOnly = WritingOnly };
                createdHost.SetPresentationVisible(false);
                _documentHost = createdHost;
                DocumentNotice.Visibility = Visibility.Collapsed;
                AttachDocumentOutline(createdHost);
                createdHost.Notice += text => { if (!ReferenceEquals(_documentHost, createdHost)) return; DocumentNotice.Text = text; DocumentNotice.Visibility = Visibility.Visible; Status.Text = text; Status.Visibility = Visibility.Visible; };
                createdHost.SourceRequested += async () => { if (ReferenceEquals(_documentHost, createdHost)) await FinishDocumentAsync(NoteViewMode.Edit); };
                createdHost.PreviewRequested += async () => { if (ReferenceEquals(_documentHost, createdHost)) await FinishDocumentAsync(NoteViewMode.Preview); };
                DocumentContainer.Child = _documentHost;
            }
            openingHost = _documentHost;
            openingHost.SetTask(Task);
            if (WritingOnly) NativePanel.Visibility = Visibility.Collapsed;
            DocumentContainer.Background = System.Windows.Media.Brushes.Transparent;
            DocumentPanel.IsHitTestVisible = false; NativePanel.IsHitTestVisible = false;
            DocumentPanel.Visibility = Visibility.Visible;
            NativeReadHint.Text = "正在准备编辑…";
            _renderTimer.Stop();
            await openingHost.PreparePresentationAsync(position);
            if (epoch != _documentEpoch || Task != task || !ReferenceEquals(openingHost, _documentHost)) return;
            NativePanel.Visibility = Visibility.Collapsed;
            DocumentContainer.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "PaperSurface");
            DocumentPanel.IsHitTestVisible = true;
            openingHost.SetPresentationVisible(true);
            ResetOutline(); PlaceOutline();
            openingHost.SetOutlineEnabled(OutlineTracking);
            if (ReferenceEquals(openingHost, _documentHost)) await openingHost.FocusEditorAsync();
        }
        catch
        {
            if (epoch == _documentEpoch && openingHost is not null && ReferenceEquals(openingHost, _documentHost))
            { ShowSource(); openingHost.Dispose(); _documentHost = null; DocumentContainer.Child = null; }
        }
        finally { NativePanel.IsHitTestVisible = true; ApplyMode(); }
    }
    private void ShowSource()
    {
        _sourceChosen = true;
        NativePanel.Visibility = Visibility.Visible;
        DocumentPanel.Visibility = Visibility.Collapsed;
        ResetOutline(); PlaceOutline();
        SyncFromTask();
        SetMode(NoteViewMode.Edit);
    }
    public async System.Threading.Tasks.Task FlushDocumentAsync(bool deferComposition = false)
    {
        if (_finishingDocument is not null) await _finishingDocument;
        if (_documentHost is not null) await _documentHost.FlushAsync(deferComposition);
    }
    private async void OpenDocumentWhenSelected()
    {
        _documentEpoch++;
        if (_documentHost is not null) await FinishDocumentAsync(NoteViewMode.Preview);
        else if (!_sourceChosen && Application.Current is App) SetMode(NoteViewMode.Preview);
        if (WritingOnly && IsLoaded && IsVisible && Task is not null)
        {
            if (_openingDocument is { IsCompleted: false }) await _openingDocument;
            await OpenDocumentAsync();
        }
    }
    public System.Threading.Tasks.Task FinishDocumentAsync(NoteViewMode mode = NoteViewMode.Preview)
    {
        if (_finishingDocument is { IsCompleted: false }) return _finishingDocument;
        return _finishingDocument = FinishDocumentCoreAsync(mode);
    }
    private async System.Threading.Tasks.Task FinishDocumentCoreAsync(NoteViewMode mode)
    {
        _documentEpoch++;
        var host = _documentHost;
        var task = Task;
        try
        {
            if (host is not null) await host.FlushAsync();
            if (!ReferenceEquals(_documentHost, host)) return;
            var position = host is not null && host.TaskId == task?.Id && Task == task ? await host.ReadPositionAsync() : null;
            if (!ReferenceEquals(_documentHost, host)) return;
            _sourceChosen = mode != NoteViewMode.Preview;
            NativePanel.Visibility = Visibility.Visible;
            SyncFromTask(true);
            SetMode(mode);
            UpdateLayout();
            if (Task == task && mode == NoteViewMode.Preview) RestoreReadingPosition(position);
            if (mode == NoteViewMode.Preview)
            {
                await SettleReadingImagesAsync();
                if (Task == task) RestoreReadingPosition(position);
            }
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            if (!ReferenceEquals(_documentHost, host)) return;
            _documentHost = null;
            DocumentContainer.Child = null; host?.Dispose();
            DocumentPanel.Visibility = Visibility.Collapsed;
            NativePanel.IsHitTestVisible = true;
            ResetOutline(); PlaceOutline();
            if (_outlineOpen) RefreshOutline();
        }
        catch (Exception error)
        { DocumentNotice.Text = "尚未结束编辑：" + error.Message; DocumentNotice.Visibility = Visibility.Visible; }
    }
    private async void Preview_Click(object sender, System.Windows.Input.MouseButtonEventArgs args)
    {
        if (Task is null || DisplayOnly) return;
        for (var target = args.OriginalSource as DependencyObject; target is not null;)
        {
            if (target is Hyperlink or ScrollBar or Thumb or NotePreviewImage or System.Windows.Controls.Primitives.RepeatButton) return;
            target = target is FrameworkContentElement content ? content.Parent : target is Visual ? VisualTreeHelper.GetParent(target) : null;
        }
        await OpenDocumentAsync();
        args.Handled = true;
    }
}
