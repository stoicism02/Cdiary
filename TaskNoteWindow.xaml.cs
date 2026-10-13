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
    private NoteAppearanceStore? _appearanceStore;
    private NoteAppearance _appearance = new();
    private bool _appearanceReady;
    private bool _appearanceDirty;
    private readonly DispatcherTimer _appearanceTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    public TaskNoteWindow(DiaryTask task, DiaryRepository repository)
    {
        Task = task;
        _repository = repository;
        InitializeComponent();
        _appearanceStore = new NoteAppearanceStore(repository.DatabasePath, task.Id);
        _appearance = _appearanceStore.Load();
        new[] { PinLineChoice, PinWindowsChoice, PinTopChoice }[_appearance.PinStyle].IsChecked = true;
        BackgroundOpacitySlider.Value = _appearance.Opacity * 100;
        ImageStrengthSlider.Value = _appearance.ImageStrength * 100;
        try { if (_appearance.HasImage) SetBackgroundImage(_appearanceStore.ReadImage()); }
        catch (Exception) { AppearanceNotice.Text = "背景图片无法读取，请重新选择。"; }
        _appearanceReady = true;
        ApplyAppearance();
        _appearanceTimer.Tick += (_, _) => SaveAppearance();
        NoteAppearanceStore.Changed += GlobalAppearanceChanged;
        Closed += (_, _) => { NoteAppearanceStore.Changed -= GlobalAppearanceChanged; _appearanceTimer.Stop(); };
        NoteEditor.DocumentRepository = repository;
        NoteEditor.MinimalSticky = true;
        NoteEditor.Resources["PaperSurface"] = System.Windows.Media.Brushes.Transparent;
        NoteEditor.Resources["TextInk"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 58, 61));
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
    { NoteFocus.Task=Task;NoteFocus.StartImmediately=true;NoteFocus.Attach(store,service);NoteFocus.OpenRequested+=open;NoteFocus.Visibility=Visibility.Visible; }

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
        SaveAppearance();
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
        SaveAppearance();
        Task.PropertyChanged -= TaskChanged;
        base.OnClosing(e);
    }

    public void SetPinned(bool pinned) => Topmost = pinned;

    private void Content_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Height < 340;
        TitleEditor.MaxHeight = compact ? 40 : 72;
        TitleEditor.Margin = new Thickness(0, 0, 0, compact ? 6 : 10);
        TitleEditor.Padding = new Thickness(0, compact ? 3 : 6, 0, compact ? 4 : 8);
        SaveHint.Visibility = Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Planning_Click(object sender, RoutedEventArgs e)
    {
        var menu = TaskPlanningMenu.Create(this, Task, _repository, async () =>
        { await NoteEditor.FlushDocumentAsync(); FlushEdits(); });
        menu.PlacementTarget = (System.Windows.Controls.Button)sender;
        menu.IsOpen = true;
    }

    private void Appearance_Click(object sender, RoutedEventArgs e) => AppearancePopup.IsOpen = !AppearancePopup.IsOpen;
    private void Appearance_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_appearanceReady) return;
        _appearanceDirty = true;
        _appearance = _appearance with { Opacity = BackgroundOpacitySlider.Value / 100, ImageStrength = ImageStrengthSlider.Value / 100 };
        ApplyAppearance();
        _appearanceTimer.Stop(); _appearanceTimer.Start();
    }
    private void ApplyAppearance()
    {
        PinToggle.Tag = FindResource(new[] { "PinLine", "PinWindows", "PinTop" }[_appearance.PinStyle]);
        BackgroundLayer.Opacity = _appearance.Opacity;
        PictureLayer.Opacity = _appearance.ImageStrength;
        OpacityLabel.Text = $"{_appearance.Opacity:P0}";
        ImageLabel.Text = $"{_appearance.ImageStrength:P0}";
    }
    private void SetBackgroundImage(System.Windows.Media.ImageSource? image)
    {
        PictureLayer.Background = image is null ? null : new System.Windows.Media.ImageBrush(image)
        { Stretch = System.Windows.Media.Stretch.UniformToFill, AlignmentX = System.Windows.Media.AlignmentX.Center, AlignmentY = System.Windows.Media.AlignmentY.Center };
    }
    private void SaveAppearance()
    {
        _appearanceTimer.Stop();
        if (!_appearanceDirty) return;
        try { _appearanceStore?.Save(_appearance); _appearanceDirty = false; }
        catch (Exception error) { AppearanceNotice.Text = "外观保存失败：" + error.Message; }
    }
    private void GlobalAppearanceChanged(string key, NoteAppearance appearance)
    {
        if (_appearanceStore?.SettingsKey != key) return;
        _appearanceTimer.Stop();
        _appearanceDirty = false;
        _appearanceReady = false;
        _appearance = appearance;
        BackgroundOpacitySlider.Value = appearance.Opacity * 100;
        ImageStrengthSlider.Value = appearance.ImageStrength * 100;
        new[] { PinLineChoice, PinWindowsChoice, PinTopChoice }[appearance.PinStyle].IsChecked = true;
        try { SetBackgroundImage(appearance.HasImage ? _appearanceStore.ReadImage() : null); }
        catch (Exception) { AppearanceNotice.Text = "背景图片无法读取，请重新选择。"; }
        ApplyAppearance();
        _appearanceReady = true;
    }
    private void PinStyle_Checked(object sender, RoutedEventArgs e)
    {
        if (!_appearanceReady) return;
        if (sender is System.Windows.Controls.RadioButton choice && int.TryParse(choice.Tag?.ToString(), out var value))
        {
            _appearance = (_appearance with { PinStyle = value }).Normalize();
            _appearanceDirty = true;
            ApplyAppearance(); SaveAppearance();
        }
    }
    private void ChooseBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择便签背景", Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SetBackgroundImage(_appearanceStore!.Import(dialog.FileName));
            _appearance = _appearance with { HasImage = true }; _appearanceDirty = true; AppearanceNotice.Text = ""; SaveAppearance();
        }
        catch (Exception error) { AppearanceNotice.Text = "无法载入图片：" + error.Message; }
    }
    private void RemoveBackground_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _appearanceStore!.RemoveImage(); SetBackgroundImage(null);
            _appearance = _appearance with { HasImage = false }; _appearanceDirty = true; AppearanceNotice.Text = ""; SaveAppearance();
        }
        catch (Exception error) { AppearanceNotice.Text = "无法移除图片：" + error.Message; }
    }
}
