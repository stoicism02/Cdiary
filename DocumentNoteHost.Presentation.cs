using System.Text.Json;
using System.Windows;

namespace Cdiary;

public sealed record NoteReadingPosition(string Text, int Occurrence, int Index, double Fraction, double Ratio);

public sealed partial class DocumentNoteHost
{
    private TaskCompletionSource? _contentReady;
    private TaskCompletionSource? _presentationReady;
    private string? _presentationRequest;
    private bool _savingReadOnly;
    public bool StickyMode { get; set; }
    public bool WritingOnly { get; set; }
    private async void SaveNowRequested(string token)
    {
        if (_disposed || token != _token) return;
        try { await FlushAsync(); }
        catch (Exception error)
        {
            if (!_disposed && token == _token) Send(new { kind = "status", text = "保存失败，输入仍保留：" + error.Message });
        }
    }
    public bool IsReadOnly { get; private set; }
    public void SetPresentationVisible(bool visible) => _web.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
    public async Task FocusEditorAsync()
    {
        if (_disposed || !IsReady) return;
        // DOM focus alone cannot move keyboard input from WPF into WebView2.
        // Never activate a window when the user has already switched away.
        if (Window.GetWindow(this) is { IsActive: false }) return;
        _web.Focus();
        await _web.ExecuteScriptAsync("window.island?.focus()");
    }

    public async Task PreparePresentationAsync(NoteReadingPosition? position)
    {
        await EnsureReadyAsync();
        while (!_disposed)
        {
            var pending = _contentReady;
            if (pending is null) return;
            await pending.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (pending != _contentReady) continue;
            _presentationRequest = Guid.NewGuid().ToString("N");
            _presentationReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _web.ExecuteScriptAsync("window.island?.preparePosition(" + JsonSerializer.Serialize(position) + "," + JsonSerializer.Serialize(_presentationRequest) + ")");
            await _presentationReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (pending != _contentReady) continue;
            return;
        }
    }
    public async Task<NoteReadingPosition?> ReadPositionAsync()
    {
        if (!IsReady || _disposed) return null;
        var json = await _web.ExecuteScriptAsync("window.island?.readingPosition() ?? null");
        return JsonSerializer.Deserialize<NoteReadingPosition>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    private async void SaveReadOnlyRequested(string token)
    {
        if (_savingReadOnly || !StickyMode || _disposed || token != _token) return;
        _savingReadOnly = true;
        try
        {
            await FlushAsync();
            if (_disposed || token != _token) return;
            IsReadOnly = true;
            Send(new { kind = "readOnlyResult", token, success = true });
        }
        catch (Exception error)
        {
            if (!_disposed && token == _token)
                Send(new { kind = "readOnlyResult", token, success = false, error = "未能结束编辑：" + error.Message });
        }
        finally { _savingReadOnly = false; }
    }
}
