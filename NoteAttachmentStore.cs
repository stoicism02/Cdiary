using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;

namespace Cdiary;

public sealed record NoteAttachment(string Id, string Extension, int Width, int Height, long Bytes)
{
    public string Reference => $"attachments/{Id}.{Extension}";
}
public sealed record AttachmentUsage(int Count, long Bytes, int UnusedCount, long UnusedBytes);

public sealed class NoteAttachmentStore
{
    public const int MaxBytes = 20 * 1024 * 1024;
    public const long MaxPixels = 40_000_000;
    private static readonly Regex ReferencePattern = new(@"attachments/([a-f0-9]{64})\.(png|jpg)", RegexOptions.Compiled);
    private readonly DiaryRepository _repository;
    private readonly object _gate = new();
    // Conservative lifetime lease: loaded notes can remain in either editor's undo history.
    private readonly HashSet<string> _sessionImages = new();
    public string DirectoryPath { get; }
    public NoteAttachmentStore(DiaryRepository repository)
    {
        _repository = repository;
        DirectoryPath = Path.Combine(Path.GetDirectoryName(repository.DatabasePath)!, "Attachments");
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _repository.DatabasePath }.ToString());
        connection.Open(); return connection;
    }
    public void Protect(string markdown)
    { lock (_gate) foreach (Match match in ReferencePattern.Matches(markdown)) _sessionImages.Add(match.Groups[1].Value); }
    public static string? IdFromReference(string reference)
    {
        var match = ReferencePattern.Match(reference);
        return match.Success && match.Index == 0 && match.Length == reference.Length ? match.Groups[1].Value : null;
    }
    public Task<NoteAttachment> ImportAsync(byte[] bytes) => Task.Run(() => Import(bytes));
    public async Task<NoteAttachment> ImportFileAsync(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxBytes) throw new IOException("图片不存在或超过 20 MB。");
        return await ImportAsync(await File.ReadAllBytesAsync(path));
    }
    private NoteAttachment Import(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes) throw new IOException("单张图片不能超过 20 MB。");
        using var input = new MemoryStream(bytes, false);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        var extension = decoder is PngBitmapDecoder ? "png" : decoder is JpegBitmapDecoder ? "jpg" : throw new IOException("目前支持 PNG、JPEG 静态图片。");
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || (long)frame.PixelWidth * frame.PixelHeight > MaxPixels)
            throw new IOException("图片超过 4000 万像素，请先缩小后插入。");
        var id = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var attachment = new NoteAttachment(id, extension, frame.PixelWidth, frame.PixelHeight, bytes.Length);
        using var previewInput = new MemoryStream(bytes, false);
        var preview = new BitmapImage(); preview.BeginInit(); preview.CacheOption = BitmapCacheOption.OnLoad;
        preview.DecodePixelWidth = Math.Max(1, (int)(frame.PixelWidth * Math.Min(1, 1024d / Math.Max(frame.PixelWidth, frame.PixelHeight))));
        preview.StreamSource = previewInput; preview.EndInit(); preview.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(preview));
        using var output = new MemoryStream(); encoder.Save(output);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.Combine(DirectoryPath, "originals"));
            Directory.CreateDirectory(Path.Combine(DirectoryPath, "previews"));
            var originalPath = Path.Combine(DirectoryPath, "originals", id + "." + extension);
            var previewPath = Path.Combine(DirectoryPath, "previews", id + ".png");
            var originalExisted=File.Exists(originalPath);var previewExisted=File.Exists(previewPath);
            try
            {
                AtomicWrite(originalPath, bytes); AtomicWrite(previewPath, output.ToArray());
                using var connection = Open(); using var command = connection.CreateCommand();
                command.CommandText = "INSERT OR IGNORE INTO note_attachments(id,extension,width,height,bytes) VALUES($id,$ext,$w,$h,$bytes)";
                command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$ext", extension);
                command.Parameters.AddWithValue("$w", frame.PixelWidth); command.Parameters.AddWithValue("$h", frame.PixelHeight);
                command.Parameters.AddWithValue("$bytes", bytes.Length + output.Length); command.ExecuteNonQuery();
            }
            catch
            {
                if(!originalExisted)TryDelete(originalPath);
                if(!previewExisted)TryDelete(previewPath);
                throw;
            }
            _sessionImages.Add(id);
        }
        return attachment;
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        if (File.Exists(path)) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void TryDelete(string path)
    {try{File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}}
    public string? Resolve(string reference, bool original = false)
    {
        var id = IdFromReference(reference); if (id is null) return null;
        var path = Path.Combine(DirectoryPath, original ? "originals" : "previews", original ? reference[12..] : id + ".png");
        return File.Exists(path) ? path : null;
    }
    public BitmapSource? ReadPreview(string reference)
    {
        var path = Resolve(reference); return path is null ? null : ScreenshotService.Decode(File.ReadAllBytes(path));
    }
    private (List<NoteAttachment> All, HashSet<string> Used) Inventory()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,extension,width,height,bytes FROM note_attachments";
        var all = new List<NoteAttachment>();
        using (var reader = command.ExecuteReader()) while(reader.Read()) all.Add(new(reader.GetString(0),reader.GetString(1),reader.GetInt32(2),reader.GetInt32(3),reader.GetInt64(4)));
        var used = new HashSet<string>(_sessionImages);
        command.CommandText = "SELECT note FROM tasks UNION ALL SELECT content FROM note_originals UNION ALL SELECT content FROM note_recovery UNION ALL SELECT content FROM day_notes UNION ALL SELECT note_template FROM recurrence_rules";
        using (var reader = command.ExecuteReader()) while(reader.Read()) foreach(Match match in ReferencePattern.Matches(reader.GetString(0))) used.Add(match.Groups[1].Value);
        return (all, used);
    }
    public AttachmentUsage Usage()
    {
        lock (_gate) { var (all, used) = Inventory(); var unused = all.Where(a => !used.Contains(a.Id)).ToArray(); return new(all.Count,all.Sum(a=>a.Bytes),unused.Length,unused.Sum(a=>a.Bytes)); }
    }
    public int CleanUnused()
    {
        lock (_gate)
        {
            var (all, used) = Inventory(); var count = 0;
            foreach(var attachment in all.Where(a => !used.Contains(a.Id)))
            {
                File.Delete(Path.Combine(DirectoryPath,"originals",attachment.Id+"."+attachment.Extension));
                File.Delete(Path.Combine(DirectoryPath,"previews",attachment.Id+".png"));
                using var connection = Open(); using var command = connection.CreateCommand();
                command.CommandText="DELETE FROM note_attachments WHERE id=$id"; command.Parameters.AddWithValue("$id",attachment.Id);command.ExecuteNonQuery();count++;
            }
            return count;
        }
    }
}
