using System.Text.Json;

namespace Cdiary;

public sealed partial class DocumentNoteHost
{
    private bool _outlineEnabled;
    public event Action? OutlineRequested;
    public event Action? OutlineDismissRequested;
    public event Action<long, int, IReadOnlyList<NoteHeading>>? OutlineChanged;
    public event Action<long, int, string?>? OutlineActiveChanged;
    public void SetOutlineEnabled(bool enabled)
    { _outlineEnabled = enabled; Send(new { kind = "outlineEnabled", enabled }); }
    public async Task JumpToOutlineAsync(string id, int version)
    {
        if (!IsReady || _disposed) return;
        var request = JsonSerializer.Serialize(new { kind = "outlineJump", token = _token, id, version });
        await _web.ExecuteScriptAsync("window.island?.receive(" + request + ")");
    }
    private void ReceiveOutline(JsonElement message, bool active)
    {
        if (!_outlineEnabled || message.GetProperty("token").GetString() != _token || _task is null) return;
        var version = message.GetProperty("version").GetInt32();
        if (active)
        { OutlineActiveChanged?.Invoke(_task.Id, version, message.GetProperty("id").GetString()); return; }
        var items = message.GetProperty("items").EnumerateArray().Select(item => new NoteHeading(
            item.GetProperty("id").GetString()!, item.GetProperty("text").GetString()!, Math.Clamp(item.GetProperty("level").GetInt32(), 1, 6))).ToArray();
        OutlineChanged?.Invoke(_task.Id, version, items);
    }
}
