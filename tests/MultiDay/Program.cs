using Cdiary;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Reflection;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static int checks;
    [STAThread] private static int Main()
    {
        try { Run(); Console.WriteLine($"跨日任务：{checks} 项通过，未显示窗口。"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); checks++; Console.WriteLine("通过：" + name); }
    private static void Reject(Action action, string name)
    { try { action(); } catch (InvalidOperationException) { Check(true,name); return; } throw new Exception(name); }
    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false)); Dispatcher.PushFrame(frame); }
    private static void Sql(string path, string sql)
    { using var c = new SqliteConnection($"Data Source={path}"); c.Open(); using var q=c.CreateCommand(); q.CommandText=sql; q.ExecuteNonQuery(); }
    private static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),"CdiaryMultiDay",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var legacyPath=Path.Combine(root,"legacy.db");
        Sql(legacyPath,"""
            CREATE TABLE tasks(id INTEGER PRIMARY KEY AUTOINCREMENT,task_date TEXT NOT NULL,title TEXT NOT NULL,note TEXT NOT NULL DEFAULT '',
              completed INTEGER NOT NULL DEFAULT 0,reminder_at TEXT,notified_at TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
            INSERT INTO tasks(task_date,title,note,created_at,updated_at) VALUES('2026-01-01','旧任务','# 原笔记','2026-01-01','2026-01-01');
            """);
        var legacy=new DiaryRepository(legacyPath);
        Check(legacy.FindTask(1) is {Title:"旧任务",Note:"# 原笔记",CarryForward:false,Deadline:null},"迁移保留旧任务且持续推进默认关闭");
        Check(File.Exists(legacyPath+".before-multiday.bak"),"迁移前生成 SQLite 一致性备份");
        var backup=File.ReadAllBytes(legacyPath+".before-multiday.bak"); _=new DiaryRepository(legacyPath);
        Check(backup.SequenceEqual(File.ReadAllBytes(legacyPath+".before-multiday.bak")),"重复启动不覆盖迁移备份");
        Check(legacy.LoadCalendarEntries(new(2026,1,1),new(2026,1,2)).Single().State!="Overdue","旧任务无截止日期不误报逾期");
        var path=Path.Combine(root,"diary.db"); var repo=new DiaryRepository(path); var today=DateTime.Today;
        var task=repo.AddTask(today.AddDays(-3),"跨日开发"); task.Note="# 进展\n保留附件 ![图](attachment:test.png)"; repo.SaveTask(task);
        var oldNote=task.Note; var originalId=task.Id;
        repo.UpdatePlan(task,task.Date,today.AddDays(2),true);
        Check(repo.LoadTasks(today).Single().Id==originalId && repo.LoadTasks(today.AddDays(1)).Single().Id==originalId,"今日与明日沿用同一任务 ID");
        Check(repo.LoadOverdueTasks(today).Count==0,"持续推进不重复出现在之前未完成区");
        Check(repo.LoadTasks(today.AddDays(-1)).Count==0,"未运行的过去日期不补造日计划");
        Check(repo.LoadCalendarEntries(today.AddDays(-2),today).Count==0,"月历不补造离线日期的推进记录");
        Reject(()=>repo.UpdatePlan(task,today,today.AddDays(-1),true),"截止日期不能早于安排日期");
        Reject(()=>repo.SaveRule(task,false,0,task.Title,null),"持续推进不能混用重复规则");
        var repeat=repo.AddTask(today,"重复任务"); repo.SaveRule(repeat,false,0,repeat.Title,null);
        Reject(()=>repo.UpdatePlan(repeat,today,null,true),"重复任务不能开启持续推进");
        Reject(()=>repo.UpdatePlan(repeat,today.AddDays(1),null,false),"重复实例不可脱离规则改期");
        var store=new FocusStore(repo); var clock=new TestClock(today.AddHours(12)); var focus=new FocusService(store,clock);
        repo.TaskEnding+=(id,reason)=>{if(focus.Current?.TaskId==id)focus.Finish(reason);};
        focus.Start(task,FocusMode.Stopwatch); clock.Advance(120);
        repo.SetDayShelved(task,today,true);
        Check(focus.Current is {State:FocusState.Finished,ElapsedMilliseconds:120000},"今天先到这里先结算正在运行的专注");
        Check(!task.IsDone && task.IsShelvedOn(today) && repo.FindTask(task.Id)!.IsShelvedOn(today),"今日收起持久化但不完成任务");
        Check(!repo.FindTask(task.Id)!.IsShelvedOn(today.AddDays(1)),"收起只作用于当天，次日恢复待办");
        Check(repo.LoadCalendarEntries(today,today.AddDays(1)).Single(x=>x.TaskId==task.Id) is {Progressed:true,WorkedMilliseconds:120000,IsDone:false},"月历记录真实投入时长与推进状态");
        repo.SetDayShelved(task,today,false);
        Check(!task.IsShelvedOn(today) && repo.LoadCalendarEntries(today,today.AddDays(1)).Single(x=>x.TaskId==task.Id).Progressed,"继续处理不丢失当日已投入记录");
        repo.SetDayShelved(task,today,true); focus.Start(task,FocusMode.Stopwatch);
        Check(!repo.FindTask(task.Id)!.IsShelvedOn(today),"重新开始专注自动恢复今天的任务");
        clock.Advance(30); focus.Finish();
        Check(store.Read(task.Id).Sum(s=>s.ElapsedMilliseconds)==150000,"多轮计时累计到同一任务");
        var invalid=repo.AddTask(today.AddDays(1),"未开始");
        Reject(()=>repo.SetDayShelved(invalid,today,true),"未开始的未来任务不能标记今日推进");
        Reject(()=>repo.SetDayShelved(task,today.AddDays(-1),true),"不能从旧日期便签修改今天的进展");
        // Failed focus settlement must leave the task active.
        Action<long,FocusEndReason> fail=(_,_)=>throw new InvalidOperationException("磁盘失败"); repo.TaskEnding+=fail;
        Reject(()=>repo.SetDayShelved(task,today,true),"专注结算失败向上报告"); repo.TaskEnding-=fail;
        Check(!repo.FindTask(task.Id)!.IsShelvedOn(today),"结算失败不会收起任务");
        repo.UpdatePlan(task,task.Date,today.AddDays(-1),true); repo.LoadTasks(today);
        Check(repo.LoadCalendarEntries(today,today.AddDays(1)).Single(x=>x.TaskId==task.Id).State=="Overdue","只有真实截止日期已过才标红");
        var prior=today.AddDays(-1).ToString("yyyy-MM-dd");
        Sql(path,$"INSERT INTO task_days(task_id,day,progressed,deadline,updated_at) VALUES({task.Id},'{prior}',1,'{prior}','{prior}');");
        task.IsDone=true; repo.SaveTask(task);
        var entries=repo.LoadCalendarEntries(today.AddDays(-3),today.AddDays(4)).Where(x=>x.TaskId==task.Id).ToList();
        Check(entries.Count(e=>e.IsDone)==1 && entries.Single(e=>e.IsDone).Date==today,"完成只把真实完成日标绿");
        Check(entries.Single(e=>e.Date==today.AddDays(-1)).State=="Progress","后来完成不覆盖先前推进状态");
        Check(repo.LoadTasks(today.AddDays(1)).All(t=>t.Id!=task.Id),"完成后停止出现在未来待办");
        Check(repo.LoadCompletedTasks().Single().Id==originalId && repo.FindTask(originalId)!.Note==oldNote,"完成归档且笔记和附件引用不丢失");
        task.IsDone=false; repo.SaveTask(task);
        Check(repo.LoadTasks(today.AddDays(1)).Any(t=>t.Id==task.Id),"恢复完成任务后继续跨日");
        repo.UpdatePlan(task,today.AddDays(2),null,true);
        Check(repo.FindTask(task.Id)!.Note==oldNote && store.Read(task.Id).Count==2,"改期保持笔记与专注记录关联");
        Check(repo.LoadCalendarEntries(today,today.AddDays(1)).Single(x=>x.TaskId==task.Id).Progressed,"改到未来后仍保留今天的实际进展");
        Check(repo.LoadTasks(today).All(t=>t.Id!=task.Id),"改到未来退出今日安排，同时保留历史投入");
        var move=repo.AddTask(today,"纯安排"); repo.LoadTasks(today); repo.UpdatePlan(move,today.AddDays(2),null,false);
        Check(repo.LoadTasks(today).All(t=>t.Id!=move.Id),"无实际进展的任务改期后退出今日清单");
        repo.UpdatePlan(move,today,null,false);
        Check(repo.LoadTasks(today).Any(t=>t.Id==move.Id),"再次改回今天可正常恢复计划");
        repo.UpdatePlan(task,today.AddDays(-3),null,true);
        // Native views share task identity but keep their own date filters.
        using var scheduler=new ReminderScheduler(repo,Dispatcher.CurrentDispatcher,_=>{});
        var main=new MainWindow(repo,scheduler,focusStore:store,focus:focus);
        var live=main.Tasks.Single(t=>t.Id==task.Id);
        var daily=new DailyTodoWindow(today,main.Tasks,_=>{},t=>repo.SaveTask(t),repo);
        var tomorrow=new DailyTodoWindow(today.AddDays(1),new ObservableCollection<DiaryTask>{live},_=>{},_=>{},repo);
        var windows=(Dictionary<DateTime,DailyTodoWindow>)typeof(MainWindow).GetField("_dayWindows",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)!;
        windows.Add(today,daily); windows.Add(today.AddDays(1),tomorrow);
        repo.SetDayShelved(live,today,true); Pump();
        Check(!main.ActiveTasks.Cast<DiaryTask>().Any(t=>t.Id==live.Id) && main.ShelvedTasks.Any(t=>t.Id==live.Id),"主界面收起到今日已推进");
        Check(!daily.ActiveTasks.Cast<DiaryTask>().Any(t=>t.Id==live.Id) && daily.ShelvedTasks.Any(t=>t.Id==live.Id),"当日便签同步收起");
        Check(tomorrow.ActiveTasks.Cast<DiaryTask>().Any(t=>t.Id==live.Id),"跨日期便签不会共享错误的过滤条件");
        repo.SetDayShelved(live,today,false); Pump();
        Check(main.ActiveTasks.Cast<DiaryTask>().Any(t=>t.Id==live.Id) && daily.HasTasks,"继续处理恢复主界面与便签");
        Check(ReferenceEquals(main.Tasks,daily.Tasks) && ReferenceEquals(main.Tasks.Single(t=>t.Id==live.Id),tomorrow.Tasks.Single(t=>t.Id==live.Id)),"刷新后多个日期窗口仍共享任务对象");
        var moving=main.Tasks.Single(t=>t.Id==move.Id);
        repo.UpdatePlan(moving,today.AddDays(1),null,false); Pump();
        Check(main.Tasks.All(t=>t.Id!=move.Id) && daily.Tasks.All(t=>t.Id!=move.Id) && tomorrow.Tasks.Any(t=>t.Id==move.Id),"改期在多个已打开清单中同步移除与加入");
        ((TextBox)main.FindName("NewTaskBox")).Text="刷新后新增";
        typeof(MainWindow).GetMethod("AddTask",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(main,null);
        Check(daily.Tasks.Any(t=>t.Title=="刷新后新增"),"刷新后主窗口新增仍同步到已打开便签");
        var plan=new TaskPlanWindow(repo,live);
        Check(((DatePicker)plan.FindName("StartDate")).SelectedDate==live.Date && ((CheckBox)plan.FindName("CarryCheck")).IsChecked==true,"日期面板正确回填安排与持续推进");
        var repeatPlan=new TaskPlanWindow(repo,repeat);
        Check(!((CheckBox)repeatPlan.FindName("CarryCheck")).IsEnabled && !((DatePicker)repeatPlan.FindName("StartDate")).IsEnabled,"重复任务面板限制不兼容操作");
        var tomorrowTask=repo.AddTask(today.AddDays(1),"午夜待办");
        main.ShowTask(today.AddDays(-1),live.Id);
        typeof(MainWindow).GetField("_knownToday",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(main,today.AddDays(-1));
        main.RefreshTodayOverview(); Pump();
        Check(main.Tasks.Any(t=>t.Id==live.Id) && main.Tasks.All(t=>t.Id!=tomorrowTask.Id),"午夜或恢复时重新加载今日，不提前加载明日任务");
        SaveVisual(plan,380,440,Path.Combine(root,"planning.png"));
        var picker=(DatePicker)plan.FindName("StartDate");
        Check(picker.Template.FindName("PART_Button",picker) is Button
            && picker.Template.FindName("PART_Popup",picker) is System.Windows.Controls.Primitives.Popup {Child:Calendar},"圆角日期选择器保留按钮及原生日历选择功能");
        SaveVisual(main,1100,720,Path.Combine(root,"main.png"));
        SaveVisual(daily,285,480,Path.Combine(root,"daily.png"));
        Console.WriteLine("布局截图："+root);
        plan.Close(); repeatPlan.Close(); main.AllowExit=true; main.Close();
        repo.DeleteTask(task.Id);
        Check(repo.LoadCalendarEntries(today.AddDays(-5),today.AddDays(5)).All(e=>e.TaskId!=task.Id),"删除任务后清理每日记录与日历关联");
    }
    private static void SaveVisual(Window window,int width,int height,string path)
    {
        var root=(FrameworkElement)window.Content; root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
}
internal sealed class TestClock(DateTime start):TimeProvider
{
    private long ticks;
    public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    public override long GetTimestamp()=>ticks;
    public override DateTimeOffset GetUtcNow()=>new DateTimeOffset(start).ToUniversalTime().AddTicks(ticks);
    public void Advance(double seconds)=>ticks+=TimeSpan.FromSeconds(seconds).Ticks;
}
