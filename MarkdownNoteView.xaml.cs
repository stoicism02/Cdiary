using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Cdiary;

public enum NoteViewMode { Edit, Preview, Split }

public partial class MarkdownNoteView : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty TaskProperty = DependencyProperty.Register(nameof(Task), typeof(DiaryTask), typeof(MarkdownNoteView),
        new PropertyMetadata(null, (owner, args) => ((MarkdownNoteView)owner).SwitchTask((DiaryTask?)args.OldValue, (DiaryTask?)args.NewValue)));
    public DiaryTask? Task { get => (DiaryTask?)GetValue(TaskProperty); set => SetValue(TaskProperty, value); }
    public string Text { get => Editor.Text; set => Editor.Text = value ?? ""; }
    public int CaretIndex { get => Editor.CaretOffset; set => Editor.CaretOffset = Math.Clamp(value, 0, Editor.Document.TextLength); }
    public void Select(int start, int length) => Editor.Select(start, length);
    public NoteViewMode Mode { get; private set; }
    public FlowDocument? PreviewDocument => Preview.Document;
    public int RenderCount { get; private set; }
    private readonly DispatcherTimer _renderTimer;
    private readonly Dictionary<long, EditState> _states = new();
    private bool _updating;
    private string? _renderedText;
    private sealed record EditState(TextDocument Document, int Caret, int SelectionStart, int SelectionLength, double Offset);

    public MarkdownNoteView()
    {
        InitializeComponent();
        InitializeDocumentMode();
        InitializeImages();
        var group = Guid.NewGuid().ToString();
        EditMode.GroupName = PreviewMode.GroupName = SplitMode.GroupName = group;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.SyntaxHighlighting = CreateHighlighting();
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); RenderPreview(); if (_outlineOpen) RefreshOutline(); };
        InitializeOutline();
        IsVisibleChanged += (_, _) => { if (IsVisible) SchedulePreview(); else _renderTimer.Stop(); };
        Unloaded += (_, _) => { _renderTimer.Stop(); if (Task is { } task) task.PropertyChanged -= Task_Changed; };
        Loaded += (_, _) => { if (Task is { } task) { task.PropertyChanged -= Task_Changed; task.PropertyChanged += Task_Changed; SyncFromTask(); } SchedulePreview(); };
    }

    private void SwitchTask(DiaryTask? oldTask, DiaryTask? newTask)
    {
        if (Editor is null) return;
        _imageEpoch++;
        ResetOutline();
        if(newTask is not null)DocumentRepository?.Attachments.Protect(newTask.Note);
        _renderTimer.Stop();
        if (oldTask is not null)
        {
            oldTask.PropertyChanged -= Task_Changed;
            _states[oldTask.Id] = new(Editor.Document, Editor.CaretOffset, Editor.SelectionStart, Editor.SelectionLength, Editor.VerticalOffset);
        }
        // Keep a bounded set of undo histories instead of retaining every visited note.
        if (_states.Count > 20) _states.Remove(_states.Keys.First());
        _updating = true;
        var text = newTask?.Note ?? "";
        var state = newTask is not null && _states.TryGetValue(newTask.Id, out var saved) && saved.Document.Text == text ? saved : null;
        Editor.Document = state?.Document ?? new TextDocument(text);
        Editor.Select(state?.SelectionStart ?? 0, state?.SelectionLength ?? 0);
        Editor.CaretOffset = state?.Caret ?? 0;
        Editor.ScrollToVerticalOffset(state?.Offset ?? 0);
        _updating = false;
        if (newTask is not null) newTask.PropertyChanged += Task_Changed;
        _renderedText = null;
        Preview.Document = null;
        SchedulePreview();
        OpenDocumentWhenSelected();
        ScheduleOutline();
    }
    private void Task_Changed(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(DiaryTask.Note) && !_updating) { SyncFromTask(); ScheduleOutline(); } }
    private void SyncFromTask(bool duringHandoff = false)
    {
        if (!duringHandoff && DocumentPanel.Visibility == Visibility.Visible) return;
        if (Task is null || Text == Task.Note) return;
        var caret = CaretIndex;
        _updating = true;
        Editor.Document = new TextDocument(Task.Note);
        CaretIndex = caret;
        _updating = false;
        SchedulePreview();
    }
    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        if (_updating || _renderTimer is null) return;
        _updating = true;
        try { if (Task is { } task) { DocumentRepository?.Attachments.Protect(Text); task.Note = Text; } }
        finally { _updating = false; }
        SchedulePreview();
        ScheduleOutline();
    }
    private bool PreviewVisible => NativePanel.Visibility == Visibility.Visible && (Mode == NoteViewMode.Preview || Mode == NoteViewMode.Split && NoteContentWidth >= 640);
    private void SchedulePreview()
    {
        _renderTimer.Stop();
        if (IsVisible && PreviewVisible && _renderedText != Text) _renderTimer.Start();
    }
    public void SetMode(NoteViewMode mode)
    {
        if (DisplayOnly) mode = NoteViewMode.Preview;
        Mode = mode; ApplyMode();
        if (PreviewVisible) RenderPreview();
        if (_outlineOpen) RefreshOutline();
    }
    private void Mode_Click(object sender, RoutedEventArgs e)
    { _sourceChosen = sender != PreviewMode; SetMode(sender == PreviewMode ? NoteViewMode.Preview : sender == SplitMode ? NoteViewMode.Split : NoteViewMode.Edit); }
    private void View_SizeChanged(object sender, SizeChangedEventArgs e) { PlaceOutline(); ApplyMode(); SchedulePreview(); ScheduleOutline(); QueueOutlinePosition(); }
    private void ApplyMode()
    {
        var compactPaper = ActualHeight > 0 && ActualHeight < 220;
        NativeToolbarRow.Height = new GridLength(DisplayOnly || WritingOnly ? 32 : compactPaper ? 34 : 44);
        ModeButtons.Visibility = DisplayOnly || WritingOnly ? Visibility.Collapsed : Visibility.Visible;
        FormatButton.Visibility = DisplayOnly || WritingOnly ? Visibility.Collapsed : Visibility.Visible;
        Preview.ToolTip = DisplayOnly ? "通过“打开便签”编辑笔记" : "点击正文开始书写";
        var compactSource = compactPaper && Mode != NoteViewMode.Preview;
        NativeFooter.MinHeight = compactSource ? 0 : compactPaper ? 20 : 25;
        NativeFooter.Padding = new Thickness(9, compactSource ? 0 : 3, 9, compactSource ? 0 : 3);
        NativeReadHint.Visibility = compactSource ? Visibility.Collapsed : Visibility.Visible;
        var split = Mode == NoteViewMode.Split && NoteContentWidth >= 640;
        var preview = Mode == NoteViewMode.Preview;
        SplitMode.Visibility = NoteContentWidth >= 640 ? Visibility.Visible : Visibility.Collapsed;
        EditMode.IsChecked = !preview && !split; PreviewMode.IsChecked = preview; SplitMode.IsChecked = split;
        EditorFrame.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        PreviewFrame.Visibility = preview || split ? Visibility.Visible : Visibility.Collapsed;
        EditColumn.Width = preview ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        GapColumn.Width = new GridLength(split ? 10 : 0);
        PreviewColumn.Width = preview || split ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        FormatButton.IsEnabled = !preview;
        NativeReadHint.Text = DisplayOnly ? "在便签中编辑" : WritingOnly ? "自动保存" : preview ? "点击正文开始书写" : RetainDocumentForReading ? "自动保存 · Ctrl+S 阅读" : "自动保存";
    }
    public void RenderPreview()
    {
        _renderTimer.Stop();
        if (_renderedText == Text) return;
        try
        {
            var engine = new MdXaml.Markdown { DisabledTag = true, DisabledContextMenu = true, HyperlinkCommand = null,
                OnHyperLinkClicked = OpenLink };
            DocumentRepository?.Attachments.Protect(Text);
            var attachments=new NoteAttachmentPreview(DocumentRepository?.Attachments,Preview);
            var plugins=MdXaml.Plugins.MdXamlPlugins.Default.Clone();
            plugins.Inline.Add(attachments);engine.Plugins=plugins;
            var ink = (Brush)FindResource("TextInk");
            engine.NormalParagraphStyle = ParagraphStyle(13, new Thickness(0, 0, 0, 8));
            engine.Heading1Style = OutlineHeadingStyle(1, 22, new Thickness(0, 9, 0, 8));
            engine.Heading2Style = OutlineHeadingStyle(2, 19, new Thickness(0, 8, 0, 7));
            engine.Heading3Style = OutlineHeadingStyle(3, 16, new Thickness(0, 7, 0, 6));
            engine.Heading4Style = OutlineHeadingStyle(4, 15, new Thickness(0, 7, 0, 6));
            engine.Heading5Style = OutlineHeadingStyle(5, 14, new Thickness(0, 7, 0, 6));
            engine.Heading6Style = OutlineHeadingStyle(6, 13, new Thickness(0, 7, 0, 6));
            engine.CodeStyle = new Style(typeof(Run));
            engine.CodeStyle.Setters.Add(new Setter(TextElement.FontFamilyProperty, new FontFamily("Consolas")));
            engine.CodeStyle.Setters.Add(new Setter(TextElement.BackgroundProperty, new SolidColorBrush(Color.FromRgb(240, 232, 216))));
            engine.CodeBlockStyle = new Style(typeof(Block));
            engine.CodeBlockStyle.Setters.Add(new Setter(TextElement.FontFamilyProperty, new FontFamily("Consolas")));
            engine.CodeBlockStyle.Setters.Add(new Setter(TextElement.FontSizeProperty, 12d));
            engine.CodeBlockStyle.Setters.Add(new Setter(TextElement.BackgroundProperty, new SolidColorBrush(Color.FromRgb(240, 232, 216))));
            engine.CodeBlockStyle.Setters.Add(new Setter(Block.PaddingProperty, new Thickness(9)));
            engine.CodeBlockStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0, 4, 0, 10)));
            engine.BlockquoteStyle = new Style(typeof(Section));
            engine.BlockquoteStyle.Setters.Add(new Setter(Block.BorderBrushProperty, FindResource("Accent")));
            engine.BlockquoteStyle.Setters.Add(new Setter(Block.BorderThicknessProperty, new Thickness(3, 0, 0, 0)));
            engine.BlockquoteStyle.Setters.Add(new Setter(Block.PaddingProperty, new Thickness(10, 2, 0, 2)));
            engine.BlockquoteStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0, 4, 0, 10)));
            engine.LinkStyle = new Style(typeof(Hyperlink));
            engine.LinkStyle.Setters.Add(new Setter(TextElement.ForegroundProperty, new SolidColorBrush(Color.FromRgb(23, 122, 112))));
            var document = engine.Transform(attachments.Prepare(Text));
            if (string.IsNullOrWhiteSpace(Text))
                document.Blocks.Add(new Paragraph(new Run(DisplayOnly ? "暂无笔记，打开便签开始记录" : "点击这里开始记录")) { Foreground = (Brush)FindResource("Muted") });
            FormatBlocks(document.Blocks, engine.CodeBlockStyle);
            attachments.RestoreCodeLiterals(document);
            if (document.Blocks.FirstBlock is { } first)
                first.Margin = new Thickness(first.Margin.Left, 0, first.Margin.Right, first.Margin.Bottom);
            document.FontFamily = new FontFamily("Microsoft YaHei UI"); document.FontSize = 13; document.Foreground = ink;
            document.LineHeight = 22.75; document.PagePadding = new Thickness(13,14,13,36); document.ColumnWidth = double.PositiveInfinity;
            Preview.Document = document; _renderedText = Text; RenderCount++;
            Status.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            Preview.Document = new FlowDocument(new Paragraph(new Run(Text))) { FontSize = 13, PagePadding = new Thickness(8) };
            Status.Text = "暂时无法排版，原文已保留。"; Status.ToolTip = error.Message; Status.Visibility = Visibility.Visible;
        }
    }
    private Style ParagraphStyle(double fontSize, Thickness margin, bool bold = false)
    {
        var style = new Style(typeof(Paragraph));
        style.Setters.Add(new Setter(TextElement.FontSizeProperty, fontSize));
        style.Setters.Add(new Setter(Block.MarginProperty, margin));
        style.Setters.Add(new Setter(Block.LineHeightProperty, bold ? fontSize * 1.4 : 22.75));
        if (bold) style.Setters.Add(new Setter(TextElement.FontWeightProperty, FontWeights.Bold));
        return style;
    }
    private static void FormatBlocks(BlockCollection blocks, Style codeStyle)
    {
        foreach (var block in blocks.ToArray())
        {
            // MdXaml 1.27 creates a nested AvalonEdit for each fenced code block.
            // Use flowing text in the preview so long lines wrap with the note.
            if (block is BlockUIContainer { Child: { } child } && FindCodeEditor(child) is { } code)
            {
                blocks.InsertBefore(block, new Paragraph(new Run(code.Text)) { Style = codeStyle });
                blocks.Remove(block);
            }
            else if (block is Section section) FormatBlocks(section.Blocks, codeStyle);
            else if (block is System.Windows.Documents.List list)
                foreach (var item in list.ListItems) FormatBlocks(item.Blocks, codeStyle);
            else if (block is Table table)
            {
                table.CellSpacing = 0;
                table.Margin = new Thickness(0, 6, 0, 10);
                for (var groupIndex = 0; groupIndex < table.RowGroups.Count; groupIndex++)
                    foreach (var row in table.RowGroups[groupIndex].Rows)
                        foreach (var cell in row.Cells)
                        {
                            cell.Padding = new Thickness(7, 5, 7, 5);
                            cell.BorderThickness = new Thickness(0, 0, 0, 1);
                            cell.BorderBrush = new SolidColorBrush(Color.FromRgb(229, 220, 199));
                            if (groupIndex == 0)
                            { cell.FontWeight = FontWeights.SemiBold; cell.Background = new SolidColorBrush(Color.FromRgb(230, 246, 238)); }
                            FormatBlocks(cell.Blocks, codeStyle);
                        }
            }
        }
    }
    private static ICSharpCode.AvalonEdit.TextEditor? FindCodeEditor(DependencyObject element)
    {
        if (element is ICSharpCode.AvalonEdit.TextEditor editor) return editor;
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            if (FindCodeEditor(child) is { } found) return found;
        return null;
    }
    public static bool IsWebLink(string target) => Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
    private void OpenLink(string target)
    {
        if (!IsWebLink(target)) { Status.Text = "仅支持打开 http / https 网页链接。"; Status.Visibility = Visibility.Visible; return; }
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { Status.Text = "无法打开链接，可以复制网址后重试。"; Status.Visibility = Visibility.Visible; }
    }
    private void Format_Click(object sender, RoutedEventArgs e)
    { FormatButton.ContextMenu.PlacementTarget = FormatButton; FormatButton.ContextMenu.IsOpen = true; }
    private void FormatItem_Click(object sender, RoutedEventArgs e)
    { if (sender is MenuItem { Tag: string format }) ApplyFormat(format); }
    private void View_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DocumentPanel.Visibility == Visibility.Visible) return;
        if ((RetainDocumentForReading || WritingOnly) && e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (_outlineComposing) { Status.Text = "请先确认输入法候选文字"; Status.Visibility = Visibility.Visible; return; }
            try
            {
                if (Task is not null && DocumentRepository is not null) DocumentRepository.SaveDocumentNote(Task);
                if (!WritingOnly) SetMode(NoteViewMode.Preview);
            }
            catch (Exception error) { Status.Text = "保存失败，仍保持编辑：" + error.Message; Status.Visibility = Visibility.Visible; }
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control || !EditorFrame.IsVisible) return;
        if(e.Key==Key.V && TryPasteImage()){e.Handled=true;return;}
        var headingLevel = e.Key >= Key.D0 && e.Key <= Key.D6 ? (int)e.Key - (int)Key.D0
            : e.Key >= Key.NumPad0 && e.Key <= Key.NumPad6 ? (int)e.Key - (int)Key.NumPad0 : -1;
        if (headingLevel >= 0) { ApplyHeading(headingLevel); e.Handled = true; return; }
        var format = e.Key switch { Key.B => "bold", Key.I => "italic", Key.K => "link", _ => null };
        if (format is not null) { ApplyFormat(format); e.Handled = true; }
    }
    public void ApplyFormat(string format)
    {
        var start = Editor.SelectionStart; var length = Editor.SelectionLength;
        var selected = length > 0 ? Editor.SelectedText : "文字";
        string prefix, suffix;
        switch (format)
        {
            case "bold": prefix = "**"; suffix = "**"; break;
            case "italic": prefix = "*"; suffix = "*"; break;
            case "link": prefix = "["; suffix = "](https://example.com)"; break;
            case "code": prefix = "\n```text\n"; suffix = "\n```\n"; break;
            case "table": prefix = "\n| 项目 | 内容 |\n| --- | --- |\n| "; selected = length > 0 ? selected : "名称"; suffix = " | 说明 |\n"; break;
            case "heading": case "list": case "quote":
                var line = Editor.Document.GetLineByOffset(start);
                start = line.Offset;
                var last = Editor.Document.GetLineByOffset(Math.Max(start, Editor.SelectionStart + length - (length > 0 ? 1 : 0)));
                length = last.EndOffset - start;
                selected = Editor.Document.GetText(start, length);
                var marker = format == "heading" ? "## " : format == "list" ? "- " : "> ";
                selected = marker + selected.Replace("\r\n", "\n").Replace("\n", "\n" + marker);
                prefix = suffix = ""; break;
            default: return;
        }
        using (Editor.Document.RunUpdate()) Editor.Document.Replace(start, length, prefix + selected + suffix);
        Editor.Select(start + prefix.Length, selected.Length); Editor.Focus();
    }
    public void ApplyHeading(int level)
    {
        if (level is < 0 or > 6) return;
        var first = Editor.Document.GetLineByOffset(Editor.SelectionStart);
        var last = Editor.Document.GetLineByOffset(Editor.SelectionStart + Math.Max(0, Editor.SelectionLength - 1));
        var start = first.Offset;
        var length = last.EndOffset - start;
        var text = Editor.Document.GetText(start, length);
        var prefix = level == 0 ? "" : new string('#', level) + " ";
        var replacement = System.Text.RegularExpressions.Regex.Replace(text,
            @"(?m)^(?: {0,3}#{1,6}(?:[ \t]+|(?=\r?$)))?", prefix);
        using (Editor.Document.RunUpdate()) Editor.Document.Replace(start, length, replacement);
        Editor.Select(start, replacement.Length); Editor.Focus();
    }
    private static IHighlightingDefinition CreateHighlighting()
    {
        const string definition = """
        <SyntaxDefinition name="IslandMarkdown" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
         <Color name="Heading" foreground="#986323" fontWeight="bold"/>
         <Color name="Emphasis" foreground="#177A70" fontWeight="bold"/>
         <Color name="Code" foreground="#7C6394"/>
         <Color name="Link" foreground="#357D92"/>
         <RuleSet>
          <Span color="Code" multiline="true"><Begin>^\s*```.*$</Begin><End>^\s*```\s*$</End></Span>
          <Rule color="Heading">^\s{0,3}\#{1,6}\s.*$</Rule>
          <Rule color="Emphasis">\*\*[^*]+\*\*</Rule>
          <Rule color="Code">`[^`]+`</Rule>
          <Rule color="Link">\[[^\]]*\]\([^)]*\)</Rule>
          <Rule color="Emphasis">^\s*(?:[-*+]|\d+\.)\s</Rule>
         </RuleSet>
        </SyntaxDefinition>
        """;
        using var reader = XmlReader.Create(new StringReader(definition));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
