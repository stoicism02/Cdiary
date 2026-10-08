using Cdiary;
using System.IO;
using Microsoft.Data.Sqlite;

var path = Path.Combine(Path.GetTempPath(), "CdiaryFocus", Guid.NewGuid().ToString("N"), "test.db");
var repo = new DiaryRepository(path);
var task = repo.AddTask(DateTime.Today, "学习数据库");
task.Note = "保留原有笔记"; repo.SaveTask(task);
var store = new FocusStore(repo);
var clock = new TestClock();
var service = new FocusService(store, clock);
int checks = 0, notices = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("通过：" + name); checks++; }
void Fails(Action action, string name) { try { action(); } catch { Check(true,name); return; } throw new Exception(name); }
service.Finished += _ => notices++;
Check(repo.LoadTasks(DateTime.Today).Single().Note == task.Note, "建表不改变旧任务和笔记");
Check(store.LoadSettings() == new FocusSettings(), "默认参数为 25/5/15，四轮长休息");
Fails(() => store.SaveSettings(new FocusSettings(23)), "拒绝非五分钟间隔");
store.SaveSettings(new FocusSettings(30,10,20,3,false));
Check(new FocusStore(repo).LoadSettings().WorkMinutes == 30, "设置持久化");
service.Start(task, FocusMode.Pomodoro);
Fails(() => service.Start(task, FocusMode.Stopwatch), "禁止重叠计时");
clock.Advance(10); service.Checkpoint(); service.Checkpoint();
Check(store.Read().Single().ElapsedMilliseconds == 600000, "检查点幂等，不重复累计");
service.Pause(); clock.Advance(60);
Check(service.Snapshot()!.ElapsedMilliseconds == 600000, "暂停一小时不计入专注");
service.Resume(); clock.Advance(7); service.Finish(); service.Finish();
Check(store.Read().Single().ElapsedMilliseconds == 1020000, "中途结束保留十七分钟");
Check(service.CompletedRounds == 0 && notices == 1, "提前结束不算整轮，重复结束不重复通知");
service.Start(task, FocusMode.Pomodoro, 5); clock.Advance(20); service.Checkpoint();
Check(service.Current!.ElapsedMilliseconds == 300000 && service.Current.CompletedPomodoro, "回调晚到也只计算计划时长");
Check(service.Current.EndedUtc == service.Current.StartedUtc.AddMinutes(5), "结束时间为实际截止时间");
Check(service.Current.State == FocusState.Finished && notices == 2, "到时自动结算，不自动开始下一轮");
service.Start(task, FocusMode.ShortBreak,5); clock.Advance(5); service.Checkpoint();
Check(FocusStatistics.ByDay(store.Read(),TimeZoneInfo.Utc).Values.Aggregate(TimeSpan.Zero,(a,b)=>a+b)==TimeSpan.FromMinutes(22), "休息不计入累计时长");
service.Start(task, FocusMode.Stopwatch); clock.Advance(12); clock.JumpWall(-120); service.Checkpoint();
Check(service.Current!.ElapsedMilliseconds == 720000, "修改系统时间不影响经过时长");
clock.Advance(9); var recovered = new FocusService(store,clock);
Check(recovered.Current!.State == FocusState.Paused && recovered.Current.ElapsedMilliseconds == 720000, "崩溃仅恢复检查点并暂停");
Fails(() => service.Checkpoint(), "过期会话无法覆盖恢复记录");
clock.Advance(120); recovered.Resume(); clock.Advance(3); recovered.Finish();
Check(recovered.Current!.ElapsedMilliseconds == 900000, "重启离线时间不计入时长");
clock.SetWall(new DateTimeOffset(2026,10,8,15,55,0,TimeSpan.Zero));
recovered.Start(task,FocusMode.Stopwatch); clock.Advance(15); recovered.Finish();
var zone=TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
var days=FocusStatistics.ByDay(new[]{recovered.Current!},zone);
Check(days[new DateOnly(2026,10,8)]==TimeSpan.FromMinutes(5) && days[new DateOnly(2026,10,9)]==TimeSpan.FromMinutes(10), "跨零点正确拆分每日统计");
var before=store.Read().Count;
task.IsDone=true;repo.SaveTask(task);
Fails(()=>recovered.Start(task,FocusMode.Pomodoro),"已完成任务不能开始专注");
Check(store.Read().Count==before,"任务完成保留历史");
task.IsDone=false;repo.SaveTask(task);
recovered.Start(task,FocusMode.Stopwatch);clock.Advance(1);
using(var connection=new SqliteConnection($"Data Source={path}")) {
 connection.Open();using var command=connection.CreateCommand();
 command.CommandText="CREATE TRIGGER reject_focus BEFORE UPDATE ON focus_sessions BEGIN SELECT RAISE(FAIL,'simulated disk error'); END;";command.ExecuteNonQuery();
}
Fails(()=>recovered.Finish(),"数据库保存失败明确上报");
Check(recovered.Current!.State==FocusState.Running && recovered.Snapshot()!.ElapsedMilliseconds==60000,"保存失败保留计时内容");
Fails(()=>recovered.Pause("电脑睡眠"),"系统暂停遇到写入失败会报告错误");
clock.Advance(600);
Check(recovered.HasUnsavedChanges && recovered.Snapshot()!.State==FocusState.Paused && recovered.Snapshot()!.ElapsedMilliseconds==60000,"即使保存失败也冻结计时，不累计睡眠时间");
using(var connection=new SqliteConnection($"Data Source={path}")) {connection.Open();using var command=connection.CreateCommand();command.CommandText="DROP TRIGGER reject_focus;";command.ExecuteNonQuery();}
recovered.Checkpoint();
Check(!recovered.HasUnsavedChanges && recovered.Current!.State==FocusState.Paused,"故障解除后补写待保存的暂停状态");
recovered.Finish();Check(recovered.Current!.ElapsedMilliseconds==60000,"解除故障后可重试结算");
for(int i=0;i<3;i++){recovered.Start(task,FocusMode.Pomodoro,5);clock.Advance(5);recovered.Checkpoint();}
Check(recovered.CompletedRounds==4 && recovered.SuggestedBreak(new FocusSettings())==FocusMode.LongBreak,"四个完整番茄建议长休息");
recovered.Start(task,FocusMode.Stopwatch);clock.Advance(2);recovered.Pause("睡眠");clock.Advance(600);
Check(recovered.Snapshot()!.ElapsedMilliseconds==120000,"系统暂停后不累计睡眠时间");
recovered.Finish(FocusEndReason.TaskDeleted);
repo.DeleteTask(task.Id);
Check(store.Read(task.Id).Count>0 && store.Read(task.Id).All(x=>x.TaskTitle=="学习数据库"),"任务删除后历史与标题快照保留");
Console.WriteLine($"专注基础：{checks} 项检查通过，未显示窗口，未使用用户数据库。");

sealed class TestClock : TimeProvider
{
 private DateTimeOffset _utc=new(2026,10,6,0,0,0,TimeSpan.Zero);
 private long _ticks;
 public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
 public override long GetTimestamp()=>_ticks;
 public override DateTimeOffset GetUtcNow()=>_utc;
 public void Advance(double minutes){var span=TimeSpan.FromMinutes(minutes);_utc+=span;_ticks+=span.Ticks;}
 public void JumpWall(double minutes)=>_utc=_utc.AddMinutes(minutes);
 public void SetWall(DateTimeOffset value)=>_utc=value;
}
