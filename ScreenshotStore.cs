using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Cdiary;

public sealed record ScreenshotOptions(int RetentionDays = 1, int CapacityMb = 500,
    uint Modifiers = 3, uint Key = 0x53, bool Enabled = true)
{
    public string HotkeyLabel => string.Join(" + ", new[]
    {
        (Modifiers & 2) != 0 ? "Ctrl" : null, (Modifiers & 1) != 0 ? "Alt" : null,
        (Modifiers & 4) != 0 ? "Shift" : null,
        new System.Windows.Input.KeyConverter().ConvertToString(System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)Key))
    }.Where(part => part is not null));

    public void Validate()
    {
        if (RetentionDays is < 1 or > 365 || CapacityMb is < 10 or > 10240)
            throw new ArgumentException("保留天数需为 1–365 天，容量需为 10–10240 MB。");
        if ((Modifiers & ~7u) != 0 || (Modifiers & 3) == 0 ||
            !(Key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x7A))
            throw new ArgumentException("快捷键需包含 Ctrl 或 Alt，搭配字母、数字或 F1–F11。");
    }
}

public sealed record ScreenshotEntry(string Id, DateTime CapturedAt, DateTime ExpiresAt,
    int Width, int Height, long Bytes, string OriginalPath, string ThumbnailPath)
{
    public string DateLabel => CapturedAt.Date == DateTime.Today ? "今天" : CapturedAt.ToString("M月d日 dddd");
    public string TimeLabel => CapturedAt.ToString("HH:mm:ss");
    public string Details => $"{Width} × {Height}  ·  {Bytes / 1024d:N0} KB";
    public string ExpiryLabel => $"{ExpiresAt:M月d日 HH:mm} 清理";
}

// Files stay outside the diary database. All paths are derived from validated IDs.
public sealed class ScreenshotStore
{
    private readonly object _gate = new();
    private readonly string _connectionString;
    public string DirectoryPath { get; }
    public ScreenshotStore(string? directory = null)
    {
        DirectoryPath = Path.GetFullPath(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cdiary", "Screenshots"));
        Directory.CreateDirectory(DirectoryPath);
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(DirectoryPath, "screenshots.db"), Pooling = false }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS captures (
                id TEXT PRIMARY KEY, captured TEXT NOT NULL, expires TEXT NOT NULL,
                width INTEGER NOT NULL, height INTEGER NOT NULL, bytes INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_captures_expires ON captures(expires);
            CREATE TABLE IF NOT EXISTS options (
                id INTEGER PRIMARY KEY CHECK(id=1), days INTEGER NOT NULL, capacity INTEGER NOT NULL,
                modifiers INTEGER NOT NULL, keycode INTEGER NOT NULL, enabled INTEGER NOT NULL);
            INSERT OR IGNORE INTO options VALUES(1,1,500,3,83,1);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open() { var connection = new SqliteConnection(_connectionString); connection.Open(); return connection; }
    private static string Stamp(DateTime time) => time.ToString("O", CultureInfo.InvariantCulture);
    public static DateTime Expiry(DateTime captured, int days) => captured.Date.AddDays(days);
    private string FilePath(string id, bool thumbnail = false)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("无效截图标识。");
        return Path.Combine(DirectoryPath, id + (thumbnail ? ".thumb.png" : ".png"));
    }

    public ScreenshotOptions LoadOptions()
    {
        lock (_gate)
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT days,capacity,modifiers,keycode,enabled FROM options WHERE id=1";
            using var reader = command.ExecuteReader(); reader.Read();
            var options = new ScreenshotOptions(reader.GetInt32(0), reader.GetInt32(1),
                (uint)reader.GetInt32(2), (uint)reader.GetInt32(3), reader.GetBoolean(4));
            options.Validate(); return options;
        }
    }

    public IReadOnlyList<ScreenshotEntry> Load()
    {
        lock (_gate)
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,captured,expires,width,height,bytes FROM captures ORDER BY captured DESC,id DESC";
            using var reader = command.ExecuteReader(); var entries = new List<ScreenshotEntry>();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                entries.Add(new(id, DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                    DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture), reader.GetInt32(3),
                    reader.GetInt32(4), reader.GetInt64(5), FilePath(id), FilePath(id, true)));
            }
            return entries;
        }
    }

    public void SaveOptions(ScreenshotOptions options)
    {
        options.Validate();
        lock (_gate)
        {
            var entries = Load();
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE options SET days=$days,capacity=$cap,modifiers=$mod,keycode=$key,enabled=$enabled WHERE id=1";
            command.Parameters.AddWithValue("$days", options.RetentionDays); command.Parameters.AddWithValue("$cap", options.CapacityMb);
            command.Parameters.AddWithValue("$mod", options.Modifiers); command.Parameters.AddWithValue("$key", options.Key);
            command.Parameters.AddWithValue("$enabled", options.Enabled); command.ExecuteNonQuery();
            foreach (var entry in entries)
            {
                command.Parameters.Clear(); command.CommandText = "UPDATE captures SET expires=$expires WHERE id=$id";
                command.Parameters.AddWithValue("$expires", Stamp(Expiry(entry.CapturedAt, options.RetentionDays)));
                command.Parameters.AddWithValue("$id", entry.Id); command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public ScreenshotEntry Save(byte[] original, byte[] thumbnail, int width, int height, DateTime captured)
    {
        lock (_gate)
        {
            var options = LoadOptions();
            var bytes = original.LongLength + thumbnail.LongLength;
            var actualBytes = Directory.EnumerateFiles(DirectoryPath, "*.png").Sum(path => new FileInfo(path).Length);
            if (actualBytes + bytes > options.CapacityMb * 1024L * 1024L)
                throw new IOException("截图箱已满，本次未暂存。可另存已有截图后删除，或调高容量上限。");
            var id = Guid.NewGuid().ToString("N");
            var entry = new ScreenshotEntry(id, captured, Expiry(captured, options.RetentionDays), width, height,
                bytes, FilePath(id), FilePath(id, true));
            try
            {
                File.WriteAllBytes(entry.OriginalPath, original);
                File.WriteAllBytes(entry.ThumbnailPath, thumbnail);
                using var connection = Open(); using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO captures VALUES($id,$captured,$expires,$w,$h,$bytes)";
                command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$captured", Stamp(captured));
                command.Parameters.AddWithValue("$expires", Stamp(entry.ExpiresAt)); command.Parameters.AddWithValue("$w", width);
                command.Parameters.AddWithValue("$h", height); command.Parameters.AddWithValue("$bytes", bytes); command.ExecuteNonQuery();
                return entry;
            }
            catch { TryDeleteFile(entry.OriginalPath); TryDeleteFile(entry.ThumbnailPath); throw; }
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            // Retain the index on failure so the next cleanup retries locked files.
            File.Delete(FilePath(id)); File.Delete(FilePath(id, true));
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM captures WHERE id=$id";
            command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
        }
    }

    public int Cleanup(DateTime now)
    {
        lock (_gate)
        {
            var failed = 0;
            foreach (var entry in Load().Where(entry => entry.ExpiresAt <= now || !File.Exists(entry.OriginalPath)))
            {
                try { Delete(entry.Id); }
                catch (IOException) { failed++; }
                catch (UnauthorizedAccessException) { failed++; }
            }
            // Recover files left behind if the process stopped between writing a file and inserting its index.
            var known = Load().Select(entry => entry.Id).ToHashSet();
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.png"))
            {
                var id = Path.GetFileName(path).Split('.')[0];
                if (Guid.TryParseExact(id, "N", out _) && !known.Contains(id)) TryDeleteFile(path);
            }
            return failed;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
