using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Cdiary;

internal static class ScreenshotNative
{
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] internal struct NativePoint { public int X; public int Y; }
}

// This overlay uses physical pixels throughout, including monitors left of the primary display.
internal sealed class ScreenshotCapture : Forms.Form
{
    private readonly Bitmap _desktop;
    private readonly Rectangle _bounds;
    private Point? _start;
    private Rectangle _selection;
    private readonly Font _labelFont = new("Microsoft YaHei UI", 11);
    public Bitmap? Result { get; private set; }

    private ScreenshotCapture(Bitmap desktop, Rectangle bounds)
    {
        _desktop = desktop; _bounds = bounds;
        FormBorderStyle = Forms.FormBorderStyle.None; StartPosition = Forms.FormStartPosition.Manual;
        AutoScaleMode = Forms.AutoScaleMode.None; Bounds = bounds; TopMost = true;
        ShowInTaskbar = false; KeyPreview = true; DoubleBuffered = true;
        Cursor = Forms.Cursors.Cross; Text = "框选截图 · Esc 取消";
    }

    public static Bitmap? SelectRegion()
    {
        var previousContext = ScreenshotNative.SetThreadDpiAwarenessContext(new IntPtr(-4));
        var previousWindow = ScreenshotNative.GetForegroundWindow();
        try
        {
            var bounds = new Rectangle(ScreenshotNative.GetSystemMetrics(76), ScreenshotNative.GetSystemMetrics(77),
                ScreenshotNative.GetSystemMetrics(78), ScreenshotNative.GetSystemMetrics(79));
            if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("当前桌面不可截图。");
            using var desktop = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(desktop))
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            using var overlay = new ScreenshotCapture(desktop, bounds);
            overlay.ShowDialog();
            return overlay.Result;
        }
        finally
        {
            ScreenshotNative.SetThreadDpiAwarenessContext(previousContext);
            if (previousWindow != IntPtr.Zero) ScreenshotNative.SetForegroundWindow(previousWindow);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ScreenshotNative.SetWindowPos(Handle, new IntPtr(-1), _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0x0040);
        Activate();
    }

    private Point CursorPoint()
    {
        ScreenshotNative.GetCursorPos(out var point);
        return new(Math.Clamp(point.X - _bounds.X, 0, _bounds.Width), Math.Clamp(point.Y - _bounds.Y, 0, _bounds.Height));
    }

    protected override void OnMouseDown(Forms.MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == Forms.MouseButtons.Right) { Close(); return; }
        if (e.Button != Forms.MouseButtons.Left) return;
        _start = CursorPoint(); Capture = true;
    }
    protected override void OnMouseMove(Forms.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_start is { } start)
        {
            var current = CursorPoint();
            _selection = Rectangle.FromLTRB(Math.Min(start.X, current.X), Math.Min(start.Y, current.Y),
                Math.Max(start.X, current.X), Math.Max(start.Y, current.Y));
        }
        Invalidate();
    }
    protected override void OnMouseUp(Forms.MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != Forms.MouseButtons.Left || _start is null) return;
        OnMouseMove(e); Capture = false;
        if (_selection.Width < 2 || _selection.Height < 2) { _start = null; Invalidate(); return; }
        Result = _desktop.Clone(_selection, PixelFormat.Format32bppRgb); Close();
    }
    protected override void OnKeyDown(Forms.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Forms.Keys.Escape) Close();
    }
    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        e.Graphics.DrawImageUnscaled(_desktop, 0, 0);
        using var shade = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
        e.Graphics.FillRectangle(shade, ClientRectangle);
        if (_selection.Width > 0 && _selection.Height > 0)
        {
            e.Graphics.DrawImage(_desktop, _selection, _selection, GraphicsUnit.Pixel);
            using var pen = new Pen(Color.FromArgb(25, 200, 185), 2);
            e.Graphics.DrawRectangle(pen, _selection);
        }
        var cursor = CursorPoint();
        var label = _start is null ? "拖动框选 · Esc / 右键取消" : $"{_selection.Width} × {_selection.Height} px · 松开完成 · Esc 取消";
        var size = e.Graphics.MeasureString(label, _labelFont);
        // Keep instructions beside the pointer, on the monitor being used.
        var location = new PointF(Math.Clamp(cursor.X + 20, 0, Math.Max(0, Width - size.Width - 24)),
            Math.Clamp(cursor.Y + 25, 0, Math.Max(0, Height - size.Height - 20)));
        using var paper = new SolidBrush(Color.FromArgb(248, 248, 240));
        using var ink = new SolidBrush(Color.FromArgb(114, 93, 66));
        e.Graphics.FillRectangle(paper, location.X, location.Y, size.Width + 20, size.Height + 12);
        e.Graphics.DrawString(label, _labelFont, ink, location.X + 10, location.Y + 6);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _labelFont.Dispose();
        base.Dispose(disposing);
    }
}
