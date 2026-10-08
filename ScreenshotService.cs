using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Cdiary;

public sealed class ScreenshotService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly HwndSource? _source;
    private readonly DispatcherTimer _cleanupTimer;
    private readonly bool _backgroundServices;
    private readonly Func<System.Drawing.Bitmap?> _captureRegion;
    private readonly Func<BitmapSource, Task> _copyImage;
    private bool _capturing, _cleaning, _disposed;
    private bool _editingHotkey;
    private int _hotkeyId = 0x5343;
    public ScreenshotStore Store { get; }
    public ScreenshotOptions Options { get; private set; }
    public string HotkeyStatus { get; private set; } = "";
    public event Action? Changed;
    public event Action<string>? Notice;

    public ScreenshotService(ScreenshotStore store, Dispatcher dispatcher, bool backgroundServices = true,
        Func<System.Drawing.Bitmap?>? captureRegion = null, Func<BitmapSource, Task>? copyImage = null)
    {
        Store = store; Options = store.LoadOptions(); _dispatcher = dispatcher; _backgroundServices = backgroundServices;
        _captureRegion = captureRegion ?? ScreenshotCapture.SelectRegion; _copyImage = copyImage ?? CopyAsync;
        _cleanupTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _cleanupTimer.Tick += CleanupTick;
        if (!backgroundServices) return;
        _source = new HwndSource(new HwndSourceParameters("Cdiary.Screenshots")
        { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WindowMessage);
        try { Register(Options); }
        catch (InvalidOperationException error) { HotkeyStatus = error.Message; }
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.TimeChanged += TimeChanged;
        _dispatcher.BeginInvoke(new Action(async () =>
        {
            if (Options.Enabled && !HotkeyStatus.Contains("可在后台")) Notice?.Invoke(HotkeyStatus);
            await CleanupAsync();
        }));
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == _hotkeyId && !_editingHotkey)
        { handled = true; _ = CaptureAsync(); }
        return IntPtr.Zero;
    }

    public void EditHotkey(bool editing)
    {
        if (_source is null || _disposed || _editingHotkey == editing) return;
        _editingHotkey = editing;
        if (editing) ScreenshotNative.UnregisterHotKey(_source.Handle, _hotkeyId);
        else
        {
            try { Register(Options); }
            catch (InvalidOperationException error) { HotkeyStatus = error.Message; Notice?.Invoke(error.Message); }
        }
    }
    private void Register(ScreenshotOptions options)
    {
        if (_source is null) return;
        if (!options.Enabled)
        {
            ScreenshotNative.UnregisterHotKey(_source.Handle, _hotkeyId); HotkeyStatus = "全局快捷键已关闭，仍可从截图箱截图。"; return;
        }
        // Register the replacement first; a conflict leaves the previous shortcut usable.
        var nextId = _hotkeyId == 0x5343 ? 0x5344 : 0x5343;
        if (!ScreenshotNative.RegisterHotKey(_source.Handle, nextId, options.Modifiers | 0x4000, options.Key))
            throw new InvalidOperationException("快捷键已被占用或无法注册，请换一个组合。原设置已保留。");
        ScreenshotNative.UnregisterHotKey(_source.Handle, _hotkeyId); _hotkeyId = nextId;
        HotkeyStatus = $"{options.HotkeyLabel} 可在后台截图";
    }

    public async Task SaveOptionsAsync(ScreenshotOptions options)
    {
        options.Validate(); var previous = Options;
        var hotkeyChanged = previous.Enabled != options.Enabled || previous.Key != options.Key || previous.Modifiers != options.Modifiers;
        if (hotkeyChanged || (options.Enabled && !HotkeyStatus.Contains("可在后台"))) Register(options);
        try { await Task.Run(() => Store.SaveOptions(options)); Options = options; }
        catch
        {
            if (hotkeyChanged)
            {
                try { Register(previous); }
                catch (InvalidOperationException error) { HotkeyStatus = error.Message; Notice?.Invoke(error.Message); }
            }
            throw;
        }
        await CleanupAsync(); Changed?.Invoke();
    }

    public async Task CaptureAsync()
    {
        if (_disposed || _capturing) return;
        _capturing = true;
        try
        {
            using var region = _captureRegion();
            if (region is null) return;
            var capturedAt = DateTime.Now;
            var bytes = await Task.Run(() =>
            {
                using var stream = new MemoryStream(); region.Save(stream, System.Drawing.Imaging.ImageFormat.Png); return stream.ToArray();
            });
            var bitmap = Decode(bytes);
            string copied;
            try { await _copyImage(bitmap); copied = "已复制"; }
            catch (ExternalException) { copied = "复制失败（剪贴板被占用），可在截图箱重新复制"; }
            string saved;
            try
            {
                await CleanupAsync();
                await Task.Run(() => Store.Save(bytes, Thumbnail(bitmap), bitmap.PixelWidth, bitmap.PixelHeight, capturedAt));
                saved = "已暂存";
            }
            catch (Exception error) { saved = $"未暂存：{error.Message}"; }
            Changed?.Invoke(); Notice?.Invoke($"{copied}；{saved}");
        }
        catch (Exception error) { Notice?.Invoke($"截图失败：{error.Message}"); }
        finally { _capturing = false; }
    }

    public static BitmapSource Decode(byte[] bytes, int decodeWidth = 0)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
    public static byte[] Thumbnail(BitmapSource bitmap)
    {
        var scale = Math.Min(1, Math.Min(320d / bitmap.PixelWidth, 200d / bitmap.PixelHeight));
        BitmapSource small = scale < 1 ? new TransformedBitmap(bitmap, new ScaleTransform(scale, scale)) : bitmap;
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(small));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    public static async Task CopyAsync(BitmapSource bitmap)
    {
        // Persist the bitmap itself, independent of the lifetime of its temporary file or this process.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var data = new System.Windows.DataObject(); data.SetImage(bitmap);
                System.Windows.Clipboard.SetDataObject(data, true); return;
            }
            catch (ExternalException) when (attempt < 4) { await Task.Delay(60 * (attempt + 1)); }
        }
    }

    public async Task CleanupAsync()
    {
        if (_disposed || _cleaning) return;
        _cleaning = true; var retry = false;
        try
        {
            retry = await Task.Run(() => Store.Cleanup(DateTime.Now)) > 0;
            if (retry) Notice?.Invoke("部分过期截图正在被占用，将在 5 分钟后重试清理。");
            Changed?.Invoke();
        }
        catch (Exception error) { retry = true; Notice?.Invoke($"截图清理未完成：{error.Message}"); }
        finally
        {
            _cleaning = false;
            if (_backgroundServices && !_disposed)
            {
                _cleanupTimer.Stop();
                var now = DateTime.Now;
                _cleanupTimer.Interval = retry ? TimeSpan.FromMinutes(5) : now.Date.AddDays(1) - now + TimeSpan.FromMilliseconds(150);
                _cleanupTimer.Start();
            }
        }
    }
    private async void CleanupTick(object? sender, EventArgs e) { _cleanupTimer.Stop(); await CleanupAsync(); }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    { if (e.Mode == PowerModes.Resume) _dispatcher.BeginInvoke(new Action(async () => await CleanupAsync())); }
    private void TimeChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(new Action(async () => await CleanupAsync()));
    public void Dispose()
    {
        _disposed = true; _cleanupTimer.Stop();
        if (_backgroundServices) { SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.TimeChanged -= TimeChanged; }
        if (_source is not null)
        { ScreenshotNative.UnregisterHotKey(_source.Handle, _hotkeyId); _source.RemoveHook(WindowMessage); _source.Dispose(); }
    }
}
