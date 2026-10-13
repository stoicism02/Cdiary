using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Cdiary;

public sealed partial class DiaryRepository
{
    public event Action? PlanningChanged;
    internal void NotifyPlanningChanged() => PlanningChanged?.Invoke();
    private void InitializePlanning()
    {
        using var connection = Open();
        using var columns = connection.CreateCommand(); columns.CommandText = "PRAGMA table_info(tasks)";
        var names = new HashSet<string>();
        using (var reader = columns.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(1));
        if (names.Contains("carry_forward")) return;
        var backup = DatabasePath + ".before-multiday.bak";
        if (!File.Exists(backup))
        {
            using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString());
            target.Open(); connection.BackupDatabase(target);
        }
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE tasks ADD COLUMN deadline TEXT;
            ALTER TABLE tasks ADD COLUMN carry_forward INTEGER NOT NULL DEFAULT 0;
            CREATE TABLE task_days (
                task_id INTEGER NOT NULL, day TEXT NOT NULL, planned INTEGER NOT NULL DEFAULT 1,
                shelved INTEGER NOT NULL DEFAULT 0, progressed INTEGER NOT NULL DEFAULT 0,
                deadline TEXT, updated_at TEXT NOT NULL, PRIMARY KEY(task_id,day));
            CREATE INDEX idx_task_days_date ON task_days(day,task_id);
            CREATE INDEX idx_tasks_carry ON tasks(carry_forward,completed,task_date);
            INSERT INTO task_days(task_id,day,updated_at) SELECT id,task_date,updated_at FROM tasks;
            """;
        command.ExecuteNonQuery(); transaction.Commit();
    }
    public DiaryTask? FindTask(long id)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, task_date, title, note, completed, reminder_at, notified_at, recurrence_rule_id, completed_at,
                deadline,carry_forward,(SELECT group_concat(day) FROM task_days WHERE task_id=tasks.id AND shelved=1)
            FROM tasks WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id",id);
        using var reader = command.ExecuteReader(); return reader.Read() ? ReadTask(reader) : null;
    }
    private void RecordPlans(IEnumerable<DiaryTask> tasks, DateTime date)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        foreach (var task in tasks)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO task_days(task_id,day,deadline,updated_at) VALUES($id,$day,$deadline,$now)
                ON CONFLICT(task_id,day) DO UPDATE SET planned=1,deadline=$deadline,updated_at=$now
                WHERE task_days.planned=0 OR task_days.deadline IS NOT $deadline;
                """;
            command.Parameters.AddWithValue("$id",task.Id); command.Parameters.AddWithValue("$day",Day(date));
            command.Parameters.AddWithValue("$deadline", task.Deadline is { } due ? Day(due) : DBNull.Value);
            command.Parameters.AddWithValue("$now",Stamp(DateTime.Now)); command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public void UpdatePlan(DiaryTask task, DateTime date, DateTime? deadline, bool carryForward)
    {
        if (deadline?.Date < date.Date) throw new InvalidOperationException("截止日期不能早于安排日期。");
        if (carryForward && task.RecurrenceRuleId is not null) throw new InvalidOperationException("重复任务暂不支持持续推进，请先停止重复并新建普通任务。");
        if (task.RecurrenceRuleId is not null && date.Date != task.Date) throw new InvalidOperationException("重复任务的安排日期由重复规则决定。");
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        if (task.Date <= DateTime.Today)
        {
            using var history = connection.CreateCommand(); history.Transaction = transaction;
            history.CommandText = "INSERT OR IGNORE INTO task_days(task_id,day,deadline,updated_at) VALUES($id,$day,$due,$now)";
            history.Parameters.AddWithValue("$id",task.Id); history.Parameters.AddWithValue("$day",Day(task.Date));
            history.Parameters.AddWithValue("$due", task.Deadline is { } prior ? Day(prior) : DBNull.Value);
            history.Parameters.AddWithValue("$now",Stamp(DateTime.Now)); history.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            UPDATE tasks SET task_date=$date,deadline=$deadline,carry_forward=$carry,
                reminder_at=$reminder,notified_at=CASE WHEN task_date=$date THEN notified_at ELSE NULL END,updated_at=$now WHERE id=$id;
            UPDATE task_days SET planned=0 WHERE task_id=$id AND day >= $today
                AND (day < $date OR (progressed=0 AND shelved=0));
            """;
        command.Parameters.AddWithValue("$date",Day(date)); command.Parameters.AddWithValue("$deadline",deadline is { } due ? Day(due) : DBNull.Value);
        command.Parameters.AddWithValue("$carry",carryForward?1:0); command.Parameters.AddWithValue("$id",task.Id);
        command.Parameters.AddWithValue("$reminder",task.ReminderTime is { } time ? Stamp(date.Date.Add(time)) : DBNull.Value);
        command.Parameters.AddWithValue("$now",Stamp(DateTime.Now)); command.Parameters.AddWithValue("$today",Day(DateTime.Today));
        command.ExecuteNonQuery(); transaction.Commit();
        if (task.Date != date.Date) task.NotifiedAt = null;
        task.ApplyPlanning(date,deadline,carryForward); PlanningChanged?.Invoke();
    }
    public void SetDayShelved(DiaryTask task, DateTime day, bool shelved)
    {
        if (day.Date != DateTime.Today || task.IsDone || task.Date > day.Date)
            throw new InvalidOperationException("只能调整今天已安排、尚未完成的任务。");
        // Settle the timer before hiding the task. Failure leaves the task visible.
        if (shelved) TaskEnding?.Invoke(task.Id, FocusEndReason.Manual);
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO task_days(task_id,day,planned,shelved,progressed,deadline,updated_at)
            VALUES($id,$day,1,$shelved,$shelved,$deadline,$now)
            ON CONFLICT(task_id,day) DO UPDATE SET planned=1,shelved=$shelved,
                progressed=MAX(task_days.progressed,$shelved),updated_at=$now;
            """;
        command.Parameters.AddWithValue("$id",task.Id); command.Parameters.AddWithValue("$day",Day(day));
        command.Parameters.AddWithValue("$shelved",shelved?1:0); command.Parameters.AddWithValue("$deadline",task.Deadline is { } due?Day(due):DBNull.Value);
        command.Parameters.AddWithValue("$now",Stamp(DateTime.Now)); command.ExecuteNonQuery();
        task.ApplyPlanning(task.Date,task.Deadline,task.CarryForward, ReadShelvedDays(task.Id));
        PlanningChanged?.Invoke();
    }
    public IReadOnlyList<DateTime> ReadShelvedDays(long id)
    {
        using var connection = Open(); using var command=connection.CreateCommand();
        command.CommandText="SELECT day FROM task_days WHERE task_id=$id AND shelved=1"; command.Parameters.AddWithValue("$id",id);
        using var reader=command.ExecuteReader(); var days=new List<DateTime>();
        while(reader.Read())days.Add(DateTime.ParseExact(reader.GetString(0),"yyyy-MM-dd",CultureInfo.InvariantCulture)); return days;
    }
}
