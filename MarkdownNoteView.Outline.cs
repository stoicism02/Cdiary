using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace Cdiary;

public partial class MarkdownNoteView
{
    private NoteOutlinePanel? _outlinePanel;
    private DispatcherTimer? _outlineTimer, _outlineScrollTimer, _outlineFlashTimer;
    private bool _outlineOpen, _placingOutline, _outlineComposing;
    private long _outlineRevision = -1;
    private int _outlineGeneration, _webOutlineVersion;
    private readonly Dictionary<string, Paragraph> _outlineParagraphs = new();
    private Paragraph? _flashedHeading;
    private object? _flashedBackground;
    private Window? _outlineOwner;
    public IReadOnlyList<NoteHeading> OutlineItems { get; private set; } = Array.Empty<NoteHeading>();
    public string? ActiveOutlineId { get; private set; }
    public bool IsOutlineOpen => _outlineOpen;
    public bool IsOutlineDocked => _outlineOpen && ActualWidth >= 720;
    private bool OutlineTracking => _outlineOpen && IsVisible;
    private double NoteContentWidth => Math.Max(0, ActualWidth - (IsOutlineDocked ? 168 : 0));

    private void InitializeOutline()
    {
        _outlinePanel = new NoteOutlinePanel();
        _outlinePanel.NavigateRequested += async id => await NavigateOutlineAsync(id);
        _outlinePanel.CloseRequested += () => SetOutlineOpen(false);
        _outlineTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        _outlineTimer.Tick += (_, _) => { _outlineTimer.Stop(); if (OutlineTracking && !_outlineComposing) RefreshOutline(); };
        _outlineScrollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        _outlineScrollTimer.Tick += (_, _) => { _outlineScrollTimer.Stop(); if (OutlineTracking) UpdateNativeOutlinePosition(); };
        _outlineFlashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _outlineFlashTimer.Tick += (_, _) => ClearOutlineFlash();
        Preview.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => QueueOutlinePosition()));
        Preview.LayoutUpdated += (_, _) => { if (OutlineTracking && PreviewVisible) QueueOutlinePosition(); };
        Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => QueueOutlinePosition();
        TextCompositionManager.AddPreviewTextInputStartHandler(Editor, (_, _) => { _outlineComposing = true; _outlineTimer.Stop(); });
        TextCompositionManager.AddPreviewTextInputHandler(Editor, (_, _) => { _outlineComposing = false; ScheduleOutline(); });
        Editor.LostKeyboardFocus += (_, _) => { _outlineComposing = false; ScheduleOutline(); };
        // Handle outside clicks ourselves: Popup's mouse capture would close it before
        // the toolbar Click arrives, causing that same click to open it again.
        PreviewMouseDown += OutlineOutsideMouseDown;
        PreviewKeyDown += OutlineEscape;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { OutlinePopup.IsOpen = false; StopOutlineWork(); }
            else if (_outlineOpen) { PlaceOutline(); RefreshOutline(); }
        };
        Loaded += (_, _) =>
        {
            _outlineOwner = Window.GetWindow(this);
            if (_outlineOwner is not null)
            {
                _outlineOwner.LocationChanged += OutlineOwnerMoved; _outlineOwner.Deactivated += OutlineOwnerDeactivated;
                _outlineOwner.PreviewMouseDown += OutlineOutsideMouseDown; _outlineOwner.PreviewKeyDown += OutlineEscape;
            }
        };
        Unloaded += (_, _) =>
        {
            StopOutlineWork(); OutlinePopup.IsOpen = false;
            if (_outlineOwner is not null)
            {
                _outlineOwner.LocationChanged -= OutlineOwnerMoved; _outlineOwner.Deactivated -= OutlineOwnerDeactivated;
                _outlineOwner.PreviewMouseDown -= OutlineOutsideMouseDown; _outlineOwner.PreviewKeyDown -= OutlineEscape;
                _outlineOwner = null;
            }
        };
    }
    private void OutlineOwnerMoved(object? sender, EventArgs args) { if (OutlinePopup.IsOpen) SetOutlineOpen(false); }
    private void OutlineOwnerDeactivated(object? sender, EventArgs args) { if (OutlinePopup.IsOpen) SetOutlineOpen(false); }
    private void DismissNarrowOutline() { if (_outlineOpen && !IsOutlineDocked) SetOutlineOpen(false); }
    private void OutlineOutsideMouseDown(object sender, MouseButtonEventArgs args)
    {
        for (var target = args.OriginalSource as DependencyObject; target is not null;)
        {
            // WebView2 forwards its own pointer events, including its HTML toolbar.
            if (target == OutlineButton || target == _outlinePanel || target == DocumentContainer) return;
            target = target is FrameworkContentElement content ? content.Parent
                : target is Visual ? VisualTreeHelper.GetParent(target) : LogicalTreeHelper.GetParent(target);
        }
        DismissNarrowOutline();
    }
    private void OutlineEscape(object sender, System.Windows.Input.KeyEventArgs args)
    {
        if (args.Key != Key.Escape || !_outlineOpen || IsOutlineDocked) return;
        DismissNarrowOutline(); args.Handled = true;
    }
    private void StopOutlineWork(bool clearFlash = true)
    { _outlineTimer?.Stop(); _outlineScrollTimer?.Stop(); if (clearFlash) ClearOutlineFlash(); _documentHost?.SetOutlineEnabled(false); }
    private void ClearOutlineFlash()
    {
        _outlineFlashTimer?.Stop();
        if (_flashedHeading is not null)
        {
            if (_flashedBackground == DependencyProperty.UnsetValue) _flashedHeading.ClearValue(TextElement.BackgroundProperty);
            else _flashedHeading.SetValue(TextElement.BackgroundProperty, _flashedBackground);
        }
        _flashedHeading = null;
    }
    private void Outline_Click(object sender, RoutedEventArgs args) => SetOutlineOpen(!_outlineOpen);
    public void SetOutlineOpen(bool open)
    {
        _outlineOpen = open;
        PlaceOutline(); ApplyMode(); SchedulePreview();
        if (open) RefreshOutline(); else StopOutlineWork(false);
    }
    private void PlaceOutline()
    {
        if (_outlinePanel is null) return;
        if (IsOutlineDocked && ReferenceEquals(OutlineSidebar.Child, _outlinePanel)) return;
        _placingOutline = true;
        try
        {
            OutlinePopup.IsOpen = false; OutlineSidebar.Child = null; OutlinePopup.Child = null;
            OutlineColumn.Width = new GridLength(IsOutlineDocked ? 168 : 0);
            OutlineSidebar.Visibility = IsOutlineDocked ? Visibility.Visible : Visibility.Collapsed;
            OutlineButton.ToolTip = _outlineOpen ? "收起笔记目录" : "展开笔记目录";
            if (!_outlineOpen) return;
            _outlinePanel.Width = double.NaN; _outlinePanel.Height = double.NaN;
            if (IsOutlineDocked) OutlineSidebar.Child = _outlinePanel;
            else
            {
                _outlinePanel.Width = Math.Clamp(ActualWidth - 12, 180, 280);
                _outlinePanel.Height = Math.Clamp(ActualHeight - 42, 120, 360);
                OutlinePopup.Child = _outlinePanel;
                OutlinePopup.PlacementTarget = DocumentPanel.Visibility == Visibility.Visible ? DocumentContainer : OutlineButton;
                OutlinePopup.Placement = DocumentPanel.Visibility == Visibility.Visible ? PlacementMode.RelativePoint : PlacementMode.Bottom;
                OutlinePopup.HorizontalOffset = DocumentPanel.Visibility == Visibility.Visible ? Math.Max(0, ActualWidth - _outlinePanel.Width - 6) : -Math.Max(0, _outlinePanel.Width - OutlineButton.ActualWidth);
                OutlinePopup.VerticalOffset = DocumentPanel.Visibility == Visibility.Visible ? (ActualHeight < 220 ? 34 : 44) : 4;
                // Background verification must never create a visible popup HWND.
                if (Application.Current is App && IsVisible) OutlinePopup.IsOpen = true;
            }
            _documentHost?.SetOutlineEnabled(OutlineTracking);
        }
        finally { _placingOutline = false; }
    }
    private void OutlinePopup_Closed(object sender, EventArgs args)
    { if (!_placingOutline && !IsOutlineDocked && _outlineOpen) SetOutlineOpen(false); }
    private void ResetOutline()
    {
        _outlineGeneration++; _outlineRevision = -1; _webOutlineVersion = 0;
        _outlineParagraphs.Clear(); OutlineItems = Array.Empty<NoteHeading>(); ActiveOutlineId = null;
        _outlinePanel?.SetEntries(OutlineItems); ClearOutlineFlash(); ScheduleOutline();
    }
    private void ScheduleOutline()
    {
        _outlineTimer?.Stop();
        if (OutlineTracking && !_outlineComposing && DocumentPanel.Visibility != Visibility.Visible) _outlineTimer?.Start();
    }
    public void RefreshOutline()
    {
        _outlineTimer?.Stop();
        if (!_outlineOpen || _outlinePanel is null) return;
        if (DocumentPanel.Visibility == Visibility.Visible)
        { _documentHost?.SetOutlineEnabled(true); return; }
        _outlineRevision = Task?.NoteRevision ?? 0;
        _outlineGeneration++; _outlineParagraphs.Clear();
        if (PreviewVisible)
        {
            RenderPreview();
            var headings = new List<NoteHeading>();
            if (Preview.Document is not null)
                foreach (var paragraph in OutlineParagraphs(Preview.Document))
                    if (paragraph.Tag is int level)
                    {
                        var id = $"preview-{_outlineGeneration}-{headings.Count}";
                        var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Trim();
                        headings.Add(new(id, string.IsNullOrEmpty(text) ? "未命名标题" : text, level));
                        _outlineParagraphs[id] = paragraph;
                    }
            OutlineItems = headings;
        }
        else OutlineItems = NoteOutline.Parse(Text).Select(item => item with { Id = $"source-{_outlineGeneration}-{item.Offset}" }).ToArray();
        _outlinePanel.SetEntries(OutlineItems); UpdateNativeOutlinePosition();
    }
    private static IEnumerable<Paragraph> OutlineParagraphs(DependencyObject root)
    {
        if (root is Paragraph paragraph) { yield return paragraph; yield break; }
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in OutlineParagraphs(child)) yield return item;
    }
    private Style OutlineHeadingStyle(int level, double size, Thickness margin)
    {
        var style = ParagraphStyle(size, margin, true); style.Setters.Add(new Setter(FrameworkContentElement.TagProperty, level)); return style;
    }
    private void QueueOutlinePosition()
    { if (OutlineTracking && _outlineScrollTimer is { IsEnabled: false }) _outlineScrollTimer.Start(); }
    private void SetActiveOutline(string? id) { ActiveOutlineId = id; _outlinePanel?.SetActive(id); }
    private void UpdateNativeOutlinePosition()
    {
        if (!_outlineOpen || DocumentPanel.Visibility == Visibility.Visible || OutlineItems.Count == 0) return;
        var active = OutlineItems[0].Id;
        if (PreviewVisible)
        {
            foreach (var heading in OutlineItems)
            {
                if (!_outlineParagraphs.TryGetValue(heading.Id, out var paragraph)) continue;
                if (!paragraph.ContentStart.HasValidLayout) continue;
                var rect = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                if (!rect.IsEmpty && rect.Top <= 28) active = heading.Id;
            }
        }
        else if (Editor.TextArea.TextView.VisualLinesValid && Editor.TextArea.TextView.VisualLines.Count > 0)
        {
            var line = Editor.TextArea.TextView.VisualLines[0].FirstDocumentLine.LineNumber;
            active = OutlineItems.LastOrDefault(item => item.Line <= line)?.Id ?? active;
        }
        SetActiveOutline(active);
    }
    public async System.Threading.Tasks.Task NavigateOutlineAsync(string id)
    {
        var heading = OutlineItems.FirstOrDefault(item => item.Id == id); if (heading is null) return;
        if (DocumentPanel.Visibility == Visibility.Visible)
        {
            if (_documentHost is not null) await _documentHost.JumpToOutlineAsync(id, _webOutlineVersion);
        }
        else
        {
            if (_outlineRevision != (Task?.NoteRevision ?? 0)) { RefreshOutline(); return; }
            if (PreviewVisible && _outlineParagraphs.TryGetValue(id, out var paragraph))
            {
                if (paragraph.ContentStart.HasValidLayout && FindOutlineScroll(Preview) is { } scroll)
                {
                    var rect = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                    if (!rect.IsEmpty) scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + rect.Top - 12));
                    else paragraph.BringIntoView();
                }
                else paragraph.BringIntoView();
                ClearOutlineFlash(); _flashedHeading = paragraph;
                _flashedBackground = paragraph.ReadLocalValue(TextElement.BackgroundProperty);
                paragraph.SetValue(TextElement.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromRgb(221, 247, 241)));
                _outlineFlashTimer?.Start();
            }
            else { Editor.CaretOffset = Math.Min(heading.Offset, Editor.Document.TextLength); Editor.ScrollToLine(heading.Line); Editor.Focus(); }
        }
        SetActiveOutline(id);
        if (!IsOutlineDocked) SetOutlineOpen(false);
    }
    private void AttachDocumentOutline(DocumentNoteHost host)
    {
        host.OutlineRequested += () => { if (ReferenceEquals(_documentHost, host)) SetOutlineOpen(!_outlineOpen); };
        host.OutlineDismissRequested += () => { if (ReferenceEquals(_documentHost, host)) DismissNarrowOutline(); };
        host.OutlineChanged += (taskId, version, items) =>
        {
            if (!ReferenceEquals(_documentHost, host) || Task?.Id != taskId || !_outlineOpen) return;
            _webOutlineVersion = version; OutlineItems = items; _outlinePanel?.SetEntries(items);
        };
        host.OutlineActiveChanged += (taskId, version, id) =>
        { if (ReferenceEquals(_documentHost, host) && Task?.Id == taskId && _outlineOpen && version == _webOutlineVersion) SetActiveOutline(id); };
    }
    private static ScrollViewer? FindOutlineScroll(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindOutlineScroll(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
}
