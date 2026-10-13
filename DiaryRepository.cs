using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Cdiary;

public sealed partial class DiaryRepository
{
    public event Action<long, FocusEndReason>? TaskEnding;
    public List<DiaryTask> LoadFocusTasks()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, task_date, title, note, completed, reminder_at, notified_at, recurrence_rule_id, completed_at, deadline, carry_forward, (SELECT group_concat(day) FROM task_days WHERE task_id=tasks.id AND shelved=1) FROM tasks WHERE completed=0 ORDER BY task_date DESC,id DESC;";
        using var reader = command.ExecuteReader(); var tasks = new List<DiaryTask>();
        while(reader.Read()) tasks.Add(ReadTask(reader)); return tasks;
    }
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffff";
    private readonly string _connectionString;

    public DiaryRepository(string? databasePath = null)
    {
        DatabasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cdiary", "cdiary.db");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
        Initialize();
        InitializePlanning();
    }

    public string DatabasePath { get; }
    private NoteAttachmentStore? _attachments;
    private readonly object _attachmentGate = new();
    public NoteAttachmentStore Attachments { get { lock(_attachmentGate) return _attachments ??= new NoteAttachmentStore(this); } }

    public void BackupOriginalNote(long taskId, string content)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO note_originals(task_id,content,created_at) SELECT id,$note,$time FROM tasks WHERE id=$id;";
        command.Parameters.AddWithValue("$id", taskId);
        command.Parameters.AddWithValue("$note", content);
        command.Parameters.AddWithValue("$time", DateTime.Now.ToString(TimestampFormat));
        command.ExecuteNonQuery();
    }

    public void SaveDocumentNote(DiaryTask task)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        // A delayed web message must never recreate a deleted task or overwrite its other fields.
        command.CommandText = "UPDATE tasks SET note=$note,updated_at=$time WHERE id=$id;";
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$note", task.Note);
        command.Parameters.AddWithValue("$time", DateTime.Now.ToString(TimestampFormat));
        command.ExecuteNonQuery();
    }

    public void SaveNoteRecovery(long taskId, string content)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO note_recovery(task_id,content,created_at) SELECT id,$note,$time FROM tasks WHERE id=$id;
            DELETE FROM note_recovery WHERE task_id=$id AND id NOT IN
                (SELECT id FROM note_recovery WHERE task_id=$id ORDER BY id DESC LIMIT 5);
            """;
        command.Parameters.AddWithValue("$id", taskId);
        command.Parameters.AddWithValue("$note", content);
        command.Parameters.AddWithValue("$time", DateTime.Now.ToString(TimestampFormat));
        command.ExecuteNonQuery();
    }

    public string? ReadNoteBackup(long taskId, bool recovery = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = recovery
            ? "SELECT content FROM note_recovery WHERE task_id=$id ORDER BY id DESC LIMIT 1;"
            : "SELECT content FROM note_originals WHERE task_id=$id;";
        command.Parameters.AddWithValue("$id", taskId);
        return command.ExecuteScalar() as string;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS tasks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                task_date TEXT NOT NULL,
                title TEXT NOT NULL,
                note TEXT NOT NULL DEFAULT '',
                completed INTEGER NOT NULL DEFAULT 0,
                completed_at TEXT,
                reminder_at TEXT,
                notified_at TEXT,
                recurrence_rule_id INTEGER,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_tasks_date ON tasks(task_date);
            CREATE INDEX IF NOT EXISTS idx_tasks_pending
                ON tasks(completed, notified_at, reminder_at);
            CREATE INDEX IF NOT EXISTS idx_tasks_overdue ON tasks(completed, task_date);
            CREATE TABLE IF NOT EXISTS day_notes (
                note_date TEXT PRIMARY KEY,
                content TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS note_originals (
                task_id INTEGER PRIMARY KEY,
                content TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS note_attachments (
                id TEXT PRIMARY KEY, extension TEXT NOT NULL, width INTEGER NOT NULL,
                height INTEGER NOT NULL, bytes INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS note_recovery (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                task_id INTEGER NOT NULL,
                content TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS recurrence_rules (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                start_date TEXT NOT NULL,
                end_date TEXT,
                is_weekly INTEGER NOT NULL,
                weekdays_mask INTEGER NOT NULL,
                title TEXT NOT NULL,
                note_template TEXT NOT NULL,
                reminder_time TEXT
            );
            CREATE TABLE IF NOT EXISTS recurrence_skips (
                rule_id INTEGER NOT NULL,
                task_date TEXT NOT NULL,
                PRIMARY KEY(rule_id, task_date)
            );
            """;
        command.ExecuteNonQuery();
        using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(tasks);";
        using (var reader = columns.ExecuteReader())
        {
            var names = new HashSet<string>();
            while (reader.Read()) names.Add(reader.GetString(1));
            if (!names.Contains("recurrence_rule_id"))
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE tasks ADD COLUMN recurrence_rule_id INTEGER;";
                alter.ExecuteNonQuery();
            }
            if (!names.Contains("completed_at"))
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE tasks ADD COLUMN completed_at TEXT; UPDATE tasks SET completed_at=updated_at WHERE completed=1;";
                alter.ExecuteNonQuery();
            }
        }
        using var index = connection.CreateCommand();
        index.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_tasks_recurrence_date ON tasks(recurrence_rule_id, task_date) WHERE recurrence_rule_id IS NOT NULL;";
        index.ExecuteNonQuery();
    }

    public List<DiaryTask> LoadTasks(DateTime date)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, task_date, title, note, completed, reminder_at, notified_at, recurrence_rule_id, completed_at, deadline, carry_forward, (SELECT group_concat(day) FROM task_days WHERE task_id=tasks.id AND shelved=1)
            FROM tasks WHERE task_date = $date
              OR EXISTS(SELECT 1 FROM task_days d WHERE d.task_id=tasks.id AND d.day=$date AND d.planned=1)
              OR (completed=1 AND substr(completed_at,1,10)=$date)
              OR ($date >= $today AND carry_forward=1 AND completed=0 AND task_date <= $date)
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$date", Day(date));
        command.Parameters.AddWithValue("$today", Day(DateTime.Today));
        using var reader = command.ExecuteReader();
        var result = new List<DiaryTask>();
        while (reader.Read()) result.Add(ReadTask(reader));
        reader.Close();
        if (date.Date == DateTime.Today) RecordPlans(result, date);
        return result;
    }

    public List<DiaryTask> LoadOverdueTasks(DateTime today)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, task_date, title, note, completed, reminder_at, notified_at, recurrence_rule_id, completed_at, deadline, carry_forward, (SELECT group_concat(day) FROM task_days WHERE task_id=tasks.id AND shelved=1)
            FROM tasks WHERE completed=0 AND task_date < $today AND carry_forward=0
              AND NOT EXISTS(SELECT 1 FROM task_days d WHERE d.task_id=tasks.id AND d.day=$today AND d.planned=1)
            ORDER BY task_date DESC, id DESC;
            """;
        command.Parameters.AddWithValue("$today", Day(today.Date));
        using var reader = command.ExecuteReader();
        var result = new List<DiaryTask>();
        while (reader.Read()) result.Add(ReadTask(reader));
        return result;
    }

    public List<CalendarEntry> LoadCalendarEntries(DateTime start, DateTime endExclusive)
    {
        start = start.Date;
        endExclusive = endExclusive.Date;
        if (endExclusive <= start) return [];
        using var connection = Open();
        var entries = new List<CalendarEntry>();
        var existing = new HashSet<(long RuleId, DateTime Date)>();
        using (var tasks = connection.CreateCommand())
        {
            tasks.CommandText = "SELECT id, task_date, title, completed, recurrence_rule_id, completed_at FROM tasks WHERE task_date >= $start AND task_date < $end ORDER BY task_date, id;";
            tasks.Parameters.AddWithValue("$start", Day(start));
            tasks.Parameters.AddWithValue("$end", Day(endExclusive));
            using var reader = tasks.ExecuteReader();
            while (reader.Read())
            {
                var date = DateTime.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var ruleId = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4);
                if (ruleId is long id) existing.Add((id, date));
                entries.Add(new CalendarEntry(date, reader.GetString(2), reader.GetInt64(0), ruleId,
                    reader.GetInt64(3) != 0, reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
            }
        }
        using (var skips = connection.CreateCommand())
        {
            skips.CommandText = "SELECT rule_id, task_date FROM recurrence_skips WHERE task_date >= $start AND task_date < $end;";
            skips.Parameters.AddWithValue("$start", Day(start));
            skips.Parameters.AddWithValue("$end", Day(endExclusive));
            using var reader = skips.ExecuteReader();
            while (reader.Read())
                existing.Add((reader.GetInt64(0), DateTime.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }
        using (var rules = connection.CreateCommand())
        {
            rules.CommandText = "SELECT id, start_date, end_date, is_weekly, weekdays_mask, title, reminder_time FROM recurrence_rules WHERE start_date < $end AND (end_date IS NULL OR end_date >= $start);";
            rules.Parameters.AddWithValue("$start", Day(start));
            rules.Parameters.AddWithValue("$end", Day(endExclusive));
            using var reader = rules.ExecuteReader();
            while (reader.Read())
            {
                var rule = ReadRule(reader);
                for (var date = start > DateTime.Today ? start : DateTime.Today; date < endExclusive; date = date.AddDays(1))
                    if (rule.Includes(date) && !existing.Contains((rule.Id, date)))
                        entries.Add(new CalendarEntry(date, rule.Title, null, rule.Id));
            }
        }
        return LoadPlanningCalendar(start, endExclusive).Concat(entries.Where(entry => entry.IsProjected)).OrderBy(entry => entry.Date).ThenBy(entry => entry.IsProjected).ThenBy(entry => entry.Title).ToList();
    }

    public DiaryTask AddTask(DateTime date, string title)
    {
        var now = Stamp(DateTime.Now);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tasks(task_date, title, note, completed, created_at, updated_at)
            VALUES($date, $title, '', 0, $now, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$date", Day(date));
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$now", now);
        var id = (long)(command.ExecuteScalar() ?? throw new InvalidOperationException("无法创建任务。"));
        return new DiaryTask(id, date, title, "", false, null, null);
    }

    public void SaveTask(DiaryTask task)
    {
        if(task.IsDone) TaskEnding?.Invoke(task.Id, FocusEndReason.TaskCompleted);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tasks SET title=$title, note=$note, completed=$completed, completed_at=$completedAt,
                recurrence_rule_id=$rule,
                notified_at=CASE WHEN reminder_at IS $reminder THEN notified_at ELSE NULL END,
                reminder_at=$reminder, updated_at=$now
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$note", task.Note);
        command.Parameters.AddWithValue("$completed", task.IsDone ? 1 : 0);
        command.Parameters.AddWithValue("$completedAt", task.CompletedAt is { } completedAt ? Stamp(completedAt) : DBNull.Value);
        command.Parameters.AddWithValue("$rule", (object?)task.RecurrenceRuleId ?? DBNull.Value);
        command.Parameters.AddWithValue("$reminder", (object?)task.ReminderAt is DateTime time ? Stamp(time) : DBNull.Value);
        command.Parameters.AddWithValue("$now", Stamp(DateTime.Now));
        command.ExecuteNonQuery();
    }

    public void DeleteTask(long id)
    {
        TaskEnding?.Invoke(id, FocusEndReason.TaskDeleted);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var skip = connection.CreateCommand())
        {
            skip.Transaction = transaction;
            skip.CommandText = "INSERT OR IGNORE INTO recurrence_skips(rule_id, task_date) SELECT recurrence_rule_id, task_date FROM tasks WHERE id=$id AND recurrence_rule_id IS NOT NULL;";
            skip.Parameters.AddWithValue("$id", id);
            skip.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM tasks WHERE id=$id; DELETE FROM note_originals WHERE task_id=$id; DELETE FROM note_recovery WHERE task_id=$id; DELETE FROM task_days WHERE task_id=$id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public List<DiaryTask> LoadCompletedTasks()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, task_date, title, note, completed, reminder_at, notified_at, recurrence_rule_id, completed_at, deadline, carry_forward, (SELECT group_concat(day) FROM task_days WHERE task_id=tasks.id AND shelved=1) FROM tasks WHERE completed=1 ORDER BY completed_at DESC, id DESC;";
        using var reader = command.ExecuteReader();
        var result = new List<DiaryTask>();
        while (reader.Read()) result.Add(ReadTask(reader));
        return result;
    }

    public RecurrenceRule? LoadRule(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, start_date, end_date, is_weekly, weekdays_mask, title, reminder_time FROM recurrence_rules WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRule(reader) : null;
    }

    public long SaveRule(DiaryTask source, bool weekly, int weekdaysMask,
        string title, TimeSpan? reminderTime, DateTime? endDate = null)
    {
        if (source.CarryForward) throw new InvalidOperationException("请先在日期与推进中关闭持续推进，再设置重复。");
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("任务名称不能为空。", nameof(title));
        if (weekly && (weekdaysMask & 127) == 0) throw new ArgumentException("请至少选择一天。", nameof(weekdaysMask));
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        long id;
        if (source.RecurrenceRuleId is long existing)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE recurrence_rules SET is_weekly=$weekly, weekdays_mask=$mask, title=$title, note_template=$note, reminder_time=$reminder, end_date=$end WHERE id=$id;";
            FillRuleParameters(update, weekly, weekdaysMask, title, reminderTime, endDate);
            update.Parameters.AddWithValue("$id", existing);
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("重复规则不存在。 ");
            id = existing;
        }
        else
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO recurrence_rules(start_date, end_date, is_weekly, weekdays_mask, title, note_template, reminder_time) VALUES($start, $end, $weekly, $mask, $title, $note, $reminder); SELECT last_insert_rowid();";
            FillRuleParameters(insert, weekly, weekdaysMask, title, reminderTime, endDate);
            insert.Parameters.AddWithValue("$start", Day(source.Date));
            id = (long)(insert.ExecuteScalar() ?? throw new InvalidOperationException("无法创建重复规则。"));
            using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = "UPDATE tasks SET recurrence_rule_id=$rule WHERE id=$task;";
            link.Parameters.AddWithValue("$rule", id);
            link.Parameters.AddWithValue("$task", source.Id);
            link.ExecuteNonQuery();
        }
        transaction.Commit();
        source.RecurrenceRuleId = id;
        return id;
    }

    public void StopRule(long id, DateTime lastDate)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recurrence_rules SET end_date=$end WHERE id=$id;";
        command.Parameters.AddWithValue("$end", Day(lastDate));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public int EnsureOccurrences(DateTime date)
    {
        date = date.Date;
        if (date < DateTime.Today) return 0;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var rules = connection.CreateCommand();
        rules.Transaction = transaction;
        rules.CommandText = "SELECT id, start_date, end_date, is_weekly, weekdays_mask, title, reminder_time FROM recurrence_rules WHERE start_date <= $date AND (end_date IS NULL OR end_date >= $date);";
        rules.Parameters.AddWithValue("$date", Day(date));
        var matching = new List<RecurrenceRule>();
        using (var reader = rules.ExecuteReader())
            while (reader.Read())
            {
                var rule = ReadRule(reader);
                if (rule.Includes(date)) matching.Add(rule);
            }
        var count = 0;
        foreach (var rule in matching)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO tasks(task_date,title,note,completed,reminder_at,notified_at,recurrence_rule_id,created_at,updated_at) SELECT $date,$title,$note,0,$reminder,NULL,$rule,$now,$now WHERE NOT EXISTS (SELECT 1 FROM recurrence_skips WHERE rule_id=$rule AND task_date=$date);";
            insert.Parameters.AddWithValue("$date", Day(date));
            insert.Parameters.AddWithValue("$title", rule.Title);
            insert.Parameters.AddWithValue("$note", "");
            insert.Parameters.AddWithValue("$reminder", rule.ReminderTime is { } time ? Stamp(date.Add(time)) : DBNull.Value);
            insert.Parameters.AddWithValue("$rule", rule.Id);
            insert.Parameters.AddWithValue("$now", Stamp(DateTime.Now));
            count += insert.ExecuteNonQuery();
        }
        transaction.Commit();
        return count;
    }

    private static void FillRuleParameters(SqliteCommand command, bool weekly, int mask,
        string title, TimeSpan? reminder, DateTime? end)
    {
        command.Parameters.AddWithValue("$weekly", weekly ? 1 : 0);
        command.Parameters.AddWithValue("$mask", mask & 127);
        command.Parameters.AddWithValue("$title", title.Trim());
        command.Parameters.AddWithValue("$note", "");
        command.Parameters.AddWithValue("$reminder", reminder is { } time ? time.ToString(@"hh\:mm", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$end", end is { } date ? Day(date) : DBNull.Value);
    }

    private static RecurrenceRule ReadRule(SqliteDataReader reader)
        => new(reader.GetInt64(0), DateTime.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            reader.IsDBNull(2) ? null : DateTime.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            reader.GetInt64(3) != 0, reader.GetInt32(4), reader.GetString(5),
            reader.IsDBNull(6) ? null : TimeSpan.ParseExact(reader.GetString(6), @"hh\:mm", CultureInfo.InvariantCulture));

    public string LoadDayMemo(DateTime date)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content FROM day_notes WHERE note_date=$date;";
        command.Parameters.AddWithValue("$date", Day(date));
        return (string?)command.ExecuteScalar() ?? "";
    }

    public List<SearchResult> Search(string query, int limit = 50)
    {
        query = query.Trim();
        if (query.Length == 0) return new List<SearchResult>();

        var pattern = "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT kind, id, entry_date, title, body FROM (
                SELECT 0 AS kind, id, task_date AS entry_date, title, note AS body
                FROM tasks WHERE title LIKE $pattern ESCAPE '\' OR note LIKE $pattern ESCAPE '\'
                UNION ALL
                SELECT 1 AS kind, NULL AS id, note_date AS entry_date,
                       '每日备忘' AS title, content AS body
                FROM day_notes WHERE content LIKE $pattern ESCAPE '\'
            )
            ORDER BY entry_date DESC, kind, id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$pattern", pattern);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        using var reader = command.ExecuteReader();
        var results = new List<SearchResult>();
        while (reader.Read())
        {
            var taskId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
            var date = DateTime.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var title = reader.GetString(3);
            var body = reader.GetString(4);
            results.Add(new SearchResult(date, taskId, title, SearchSnippet(body, query)));
        }
        return results;
    }

    private static string SearchSnippet(string body, string query)
    {
        body = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (body.Length == 0) return "点击查看任务";
        var match = body.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, (match < 0 ? 0 : match) - 24);
        var length = Math.Min(80, body.Length - start);
        return (start > 0 ? "…" : "") + body.Substring(start, length)
            + (start + length < body.Length ? "…" : "");
    }

    public void SaveDayMemo(DateTime date, string content)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO day_notes(note_date, content, updated_at)
            VALUES($date, $content, $now)
            ON CONFLICT(note_date) DO UPDATE SET content=excluded.content, updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$date", Day(date));
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$now", Stamp(DateTime.Now));
        command.ExecuteNonQuery();
    }

    public List<DiaryTask> DueReminders(DateTime now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, task_date, title, note, completed, reminder_at, notified_at, recurrence_rule_id, completed_at, deadline, carry_forward, (SELECT group_concat(day) FROM task_days WHERE task_id=tasks.id AND shelved=1)
            FROM tasks
            WHERE completed=0 AND notified_at IS NULL AND reminder_at IS NOT NULL
              AND reminder_at <= $now
            ORDER BY reminder_at;
            """;
        command.Parameters.AddWithValue("$now", Stamp(now));
        using var reader = command.ExecuteReader();
        var result = new List<DiaryTask>();
        while (reader.Read()) result.Add(ReadTask(reader));
        return result;
    }

    public DateTime? NextReminder(DateTime now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(reminder_at) FROM tasks
            WHERE completed=0 AND notified_at IS NULL AND reminder_at > $now;
            """;
        command.Parameters.AddWithValue("$now", Stamp(now));
        var value = command.ExecuteScalar();
        return value is string timestamp ? Parse(timestamp) : null;
    }

    public void MarkNotified(IEnumerable<long> ids, DateTime now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE tasks SET notified_at=$now WHERE id=$id AND notified_at IS NULL;";
        command.Parameters.AddWithValue("$now", Stamp(now));
        var idParameter = command.Parameters.Add("$id", SqliteType.Integer);
        foreach (var id in ids)
        {
            idParameter.Value = id;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static DiaryTask ReadTask(SqliteDataReader reader)
    {
        var date = DateTime.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var reminder = reader.IsDBNull(5) ? (TimeSpan?)null : Parse(reader.GetString(5)).TimeOfDay;
        var notified = reader.IsDBNull(6) ? (DateTime?)null : Parse(reader.GetString(6));
        var task = new DiaryTask(reader.GetInt64(0), date, reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4) != 0, reminder, notified,
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : Parse(reader.GetString(8)));
        task.ApplyPlanning(date, reader.IsDBNull(9) ? null : DateTime.ParseExact(reader.GetString(9), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            reader.GetInt64(10) != 0, reader.IsDBNull(11) ? [] : reader.GetString(11).Split(',').Select(s => DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        return task;
    }

    private static string Day(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Stamp(DateTime date) => date.ToString(TimestampFormat, CultureInfo.InvariantCulture);
    private static DateTime Parse(string value) => DateTime.ParseExact(value, TimestampFormat, CultureInfo.InvariantCulture);
}
