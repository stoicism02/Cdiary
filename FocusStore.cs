using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Cdiary;

public sealed class FocusStore
{
    private readonly string _connectionString;
    public FocusStore(DiaryRepository repository)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = repository.DatabasePath }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS focus_sessions (
                id TEXT PRIMARY KEY, task_id INTEGER NOT NULL, state INTEGER NOT NULL,
                revision INTEGER NOT NULL, started_utc TEXT NOT NULL, payload TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS focus_one_active ON focus_sessions((1)) WHERE state IN (0,1);
            CREATE INDEX IF NOT EXISTS focus_task ON focus_sessions(task_id,started_utc);
            CREATE TABLE IF NOT EXISTS focus_settings (id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var connection = new SqliteConnection(_connectionString); connection.Open(); return connection; }

    public void Save(FocusSession session, int? expectedRevision)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = expectedRevision is null
            ? "INSERT INTO focus_sessions(id,task_id,state,revision,started_utc,payload) VALUES($id,$task,$state,$revision,$started,$payload);"
            : "UPDATE focus_sessions SET state=$state,revision=$revision,payload=$payload WHERE id=$id AND revision=$expected;";
        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$task", session.TaskId);
        command.Parameters.AddWithValue("$state", (int)session.State);
        command.Parameters.AddWithValue("$revision", session.Revision);
        command.Parameters.AddWithValue("$started", session.StartedUtc.ToString("O"));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(session));
        command.Parameters.AddWithValue("$expected", expectedRevision ?? -1);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("专注记录已变化，请重新加载后重试。");
    }
    public IReadOnlyList<FocusSession> Read(long? taskId = null, bool activeOnly = false)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM focus_sessions WHERE ($task IS NULL OR task_id=$task) AND ($active=0 OR state IN (0,1)) ORDER BY started_utc DESC;";
        command.Parameters.AddWithValue("$task", (object?)taskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$active", activeOnly ? 1 : 0);
        using var reader = command.ExecuteReader(); var result = new List<FocusSession>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<FocusSession>(reader.GetString(0))!);
        return result;
    }
    public bool CanStartTask(long id, bool allowCompleted = false)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM tasks WHERE id=$id AND (completed=0 OR $allow=1);";
        command.Parameters.AddWithValue("$allow",allowCompleted?1:0);
        command.Parameters.AddWithValue("$id", id); return (long)command.ExecuteScalar()! == 1;
    }
    public FocusSettings LoadSettings()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM focus_settings WHERE id=1;";
        var settings = command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<FocusSettings>(json)! : new FocusSettings();
        settings.Validate(); return settings;
    }
    public void SaveSettings(FocusSettings settings)
    {
        settings.Validate(); using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO focus_settings(id,payload) VALUES(1,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload;";
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(settings)); command.ExecuteNonQuery();
    }
}
