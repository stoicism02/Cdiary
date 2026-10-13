using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Cdiary;

public sealed record NoteAppearance(double Opacity = .96, double ImageStrength = .3, bool HasImage = false, int PinStyle = 0)
{
    public NoteAppearance Normalize() => this with
    {
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, .1, 1) : .96,
        ImageStrength = double.IsFinite(ImageStrength) ? Math.Clamp(ImageStrength, 0, 1) : .3,
        PinStyle = Math.Clamp(PinStyle, 0, 2)
    };
}

// Backgrounds are separate from note attachments and screenshot retention.
public sealed class NoteAppearanceStore
{
    private readonly string _folder;
    public string SettingsKey => _folder;
    public static event Action<string, NoteAppearance>? Changed;
    public NoteAppearanceStore(string databasePath, long taskId)
    {
        var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "NoteAppearance");
        _folder = Path.Combine(root, "global");
        if (!File.Exists(Path.Combine(_folder, "appearance.json"))) Migrate(root);
    }
    private void Migrate(string root)
    {
        if (!Directory.Exists(root)) return;
        var candidates = new List<(NoteAppearance Value, string Folder, DateTime Date)>();
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (!long.TryParse(Path.GetFileName(folder), out _)) continue;
            try
            {
                var path = Path.Combine(folder, "appearance.json");
                var value = JsonSerializer.Deserialize<NoteAppearance>(File.ReadAllText(path))?.Normalize();
                if (value is null) continue;
                var image = Path.Combine(folder, "background.png");
                value = value with { HasImage = value.HasImage && File.Exists(image) };
                candidates.Add((value, folder, File.GetLastWriteTimeUtc(value.HasImage ? image : path)));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        if (candidates.Count == 0) return;
        // Prefer the most recently imported existing background; keep all old task files intact.
        var chosen = candidates.OrderByDescending(x => x.Value.HasImage).ThenByDescending(x => x.Date).First();
        Directory.CreateDirectory(_folder);
        if (chosen.Value.HasImage) File.Copy(Path.Combine(chosen.Folder, "background.png"), ImagePath, true);
        Save(chosen.Value);
    }
    public string ImagePath => Path.Combine(_folder, "background.png");
    public NoteAppearance Load()
    {
        try { return (JsonSerializer.Deserialize<NoteAppearance>(File.ReadAllText(Path.Combine(_folder, "appearance.json"))) ?? new()).Normalize(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(NoteAppearance appearance)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "appearance.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(appearance.Normalize()));
        File.Move(path + ".tmp", path, true);
        Changed?.Invoke(SettingsKey, appearance.Normalize());
    }
    public BitmapImage? ReadImage()
    {
        if (!File.Exists(ImagePath)) return null;
        return Decode(ImagePath);
    }
    private static BitmapImage Decode(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        var width = frame.PixelWidth; var height = frame.PixelHeight;
        if ((long)width * height > 80_000_000) throw new InvalidOperationException("图片尺寸过大，请先缩小图片。");
        var targetWidth = Math.Max(1, (int)(width * Math.Min(1, 1600d / Math.Max(width, height))));
        stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = targetWidth; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }
    public BitmapImage Import(string source)
    {
        if (new FileInfo(source).Length > 20 * 1024 * 1024) throw new InvalidOperationException("请选择小于 20 MB 的图片。");
        var image = Decode(source);
        Directory.CreateDirectory(_folder);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(ImagePath + ".tmp")) encoder.Save(stream);
        File.Move(ImagePath + ".tmp", ImagePath, true);
        return image;
    }
    public void RemoveImage() { if (File.Exists(ImagePath)) File.Delete(ImagePath); }
}
