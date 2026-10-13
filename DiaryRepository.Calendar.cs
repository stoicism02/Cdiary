using System.Globalization;
using System.Text.Json;

namespace Cdiary;

public sealed partial class DiaryRepository
{
    private List<CalendarEntry> LoadPlanningCalendar(DateTime start, DateTime end)
    {
        var records = new Dictionary<(long, DateTime), CalendarEntry>();
        using var connection = Open();
        var tasks = new Dictionary<long, (DateTime Date,string Title,bool Done,DateTime? Completed,DateTime? Deadline,bool Carry,long? Rule)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText="SELECT id,task_date,title,completed,completed_at,deadline,carry_forward,recurrence_rule_id FROM tasks WHERE task_date < $end OR id IN (SELECT task_id FROM task_days WHERE day < $end) OR completed_at < $end";
            command.Parameters.AddWithValue("$end",Day(end));
            using var r=command.ExecuteReader();
            while(r.Read())tasks[r.GetInt64(0)]=(DateTime.ParseExact(r.GetString(1),"yyyy-MM-dd",CultureInfo.InvariantCulture),r.GetString(2),r.GetInt64(3)!=0,
                r.IsDBNull(4)?null:Parse(r.GetString(4)),r.IsDBNull(5)?null:DateTime.ParseExact(r.GetString(5),"yyyy-MM-dd",CultureInfo.InvariantCulture),r.GetInt64(6)!=0,r.IsDBNull(7)?null:r.GetInt64(7));
        }
        void Add(long id, DateTime date, bool progress=false, long worked=0, DateTime? deadline=null, bool snapshot=false)
        {
            date=date.Date;
            if(date<start || date>=end || !tasks.TryGetValue(id,out var task))return;
            var due=snapshot?deadline:task.Deadline;
            var done=task.Done && task.Completed?.Date==date;
            var overdue=due is { } d && d<date && date<=DateTime.Today && (!task.Done || task.Completed?.Date>date);
            records.TryGetValue((id,date),out var old);
            records[(id,date)]=new CalendarEntry(date,task.Title,id,task.Rule,done,done?task.Completed:null,
                progress || old?.Progressed==true,overdue,worked>0?worked:old?.WorkedMilliseconds??0);
        }
        foreach(var (id,t) in tasks)
        {
            Add(id,t.Date);
            if(t.Done && t.Completed is { } at)Add(id,at.Date);
            if(t.Carry && !t.Done)
                for(var date=new[]{start,DateTime.Today,t.Date}.Max();date<end;date=date.AddDays(1))Add(id,date);
        }
        using(var command=connection.CreateCommand())
        {
            command.CommandText="SELECT task_id,day,progressed,deadline FROM task_days WHERE day >= $start AND day < $end AND (planned=1 OR progressed=1)";
            command.Parameters.AddWithValue("$start",Day(start));command.Parameters.AddWithValue("$end",Day(end));
            using var r=command.ExecuteReader();
            while(r.Read())Add(r.GetInt64(0),DateTime.ParseExact(r.GetString(1),"yyyy-MM-dd",CultureInfo.InvariantCulture),r.GetInt64(2)!=0,deadline:r.IsDBNull(3)?null:DateTime.ParseExact(r.GetString(3),"yyyy-MM-dd",CultureInfo.InvariantCulture),snapshot:true);
        }
        using(var command=connection.CreateCommand())
        {
            command.CommandText="SELECT count(*) FROM sqlite_master WHERE type='table' AND name='focus_sessions'";
            if((long)command.ExecuteScalar()!>0)
            {
                command.CommandText="SELECT payload FROM focus_sessions WHERE started_utc < $end";
                command.Parameters.AddWithValue("$end",new DateTimeOffset(end.AddDays(1)).ToUniversalTime().ToString("O"));
                using var r=command.ExecuteReader();var sessions=new List<FocusSession>();
                while(r.Read()){var session=JsonSerializer.Deserialize<FocusSession>(r.GetString(0));if(session is not null && tasks.ContainsKey(session.TaskId))sessions.Add(session);}
                foreach(var group in sessions.GroupBy(s=>s.TaskId))
                    foreach(var pair in FocusStatistics.ByDay(group,TimeZoneInfo.Local))
                    {
                        var date=pair.Key.ToDateTime(TimeOnly.MinValue);
                        var key=(group.Key,date);
                        var old=records.GetValueOrDefault(key);
                        Add(group.Key,date,true,(long)pair.Value.TotalMilliseconds);
                        if(old is not null)records[key]=records[key] with {WasOverdue=old.WasOverdue};
                    }
            }
        }
        return records.Values.ToList();
    }
}
