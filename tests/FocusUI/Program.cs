using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cdiary;

class Program
{
 static int checks;
 static void Check(bool result,string text){if(!result)throw new Exception(text);Console.WriteLine("通过："+text);checks++;}
 static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
 static IEnumerable<T> Visuals<T>(DependencyObject node) where T:DependencyObject
 {for(var i=0;i<VisualTreeHelper.GetChildrenCount(node);i++){var child=VisualTreeHelper.GetChild(node,i);if(child is T match)yield return match;foreach(var nested in Visuals<T>(child))yield return nested;}}
 [STAThread]static void Main()
 {
  var repo=new DiaryRepository(Path.Combine(AppContext.BaseDirectory,"CdiaryFocusUI",Guid.NewGuid().ToString("N"),"test.db"));
  var task=repo.AddTask(DateTime.Today,"学习数据库索引");var other=repo.AddTask(DateTime.Today,"整理项目笔记");var store=new FocusStore(repo);
  foreach(var entry in new[]{(task,30.0),(other,20.0)})
  {
   var start=new DateTimeOffset(DateTime.Today.AddHours(8)).ToUniversalTime();
   store.Save(new FocusSession(Guid.NewGuid().ToString("N"),entry.Item1.Id,entry.Item1.Title,FocusMode.Stopwatch,0,FocusState.Finished,start,start.AddMinutes(entry.Item2),FocusEndReason.Manual,"",0,[new FocusSegment(start,(long)(entry.Item2*60000L))]),null);
  }
  var clock=new TestClock();var service=new FocusService(store,clock);
  repo.TaskEnding+=(id,reason)=>{if(service.Current?.TaskId==id)service.Finish(reason);};
  using var surface=new HwndSource(new HwndSourceParameters("Invisible focus layout"){Width=950,Height=800,PositionX=-32000,PositionY=-32000,WindowStyle=unchecked((int)0x80000000)});
  var page=new FocusPage();page.Attach(repo,store,service);surface.RootVisual=page;
  void Layout(int width){page.Measure(new Size(width,780));page.Arrange(new Rect(0,0,width,780));page.UpdateLayout();Pump();}
  T Find<T>(string name)=>(T)page.FindName(name);
  void Click(string name)=>Find<Button>(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
  Layout(920);page.SelectTask(task);
  Check(Find<ComboBox>("TaskPicker").SelectedItem is DiaryTask,"页面加载待办任务");
  var picker=Find<ComboBox>("TaskPicker");Layout(920);
  Check(Visuals<TextBlock>(picker).Any(x=>x.Text==task.Title)&&!Visuals<TextBlock>(picker).Any(x=>x.Text.Contains("Cdiary.DiaryTask")),"收起的下拉框显示任务标题，不显示对象类型名");
  picker.SelectedItem=picker.Items.Cast<DiaryTask>().Single(x=>x.Id==other.Id);Layout(920);
  Check(Visuals<TextBlock>(picker).Any(x=>x.Text==other.Title)&&Find<TextBlock>("CurrentTitle").Text==other.Title,"切换任务后下拉框与计时标题同步更新");
  var option=new ContentPresenter{Content=other,ContentTemplate=picker.ItemTemplate};
  surface.RootVisual=option;option.Measure(new Size(300,50));option.Arrange(new Rect(0,0,300,50));option.UpdateLayout();Pump();
  Check(Visuals<TextBlock>(option).Any(x=>x.Text==other.Title),"下拉选项使用相同的任务名称模板");
  surface.RootVisual=page;
  page.SelectTask(task);Layout(920);
  Click("StartButton");Check(service.Current?.State==FocusState.Running,"界面可开始番茄钟");
  Check(Find<TextBlock>("CurrentTitle").Text==task.Title,"正在专注的任务同步到页面");
  Click("PauseButton");Check(service.Current?.State==FocusState.Paused,"页面可暂停");
  Click("PauseButton");Check(service.Current?.State==FocusState.Running,"页面可继续");
  Layout(920);Check(Grid.GetColumn(Find<StackPanel>("Summary"))==1,"宽页面统计在右侧");
  var totals=Find<ItemsControl>("Totals");
  Check(totals.Items.Count==2 && totals.ActualWidth<=240,"时间分配区宽度限制为 240 DIP");
  double Percent(string title){var item=totals.Items.Cast<object>().Single(x=>(string)x.GetType().GetProperty("Title")!.GetValue(x)! == title);return (double)item.GetType().GetProperty("Percent")!.GetValue(item)!;}
  Check(Math.Abs(Percent(task.Title)-60)<0.0001&&Math.Abs(Percent(other.Title)-40)<0.0001,"30 分钟与 20 分钟显示 60% / 40% 时间分配");
  var distribution=Find<Grid>("DistributionBar");
  Check(distribution.Visibility==Visibility.Visible&&distribution.Children.Count==2&&Math.Abs(distribution.ColumnDefinitions.Sum(x=>x.Width.Value)-100)<0.0001,"多个任务共用一条分色条，各段合计 100%");
  Check(Find<ProgressBar>("RoundProgress").Value==0,"番茄钟新一轮从零开始，不受今日累计 30 分钟影响");
  clock.Advance(5);Click("PauseButton");
  Check(Math.Abs(Find<ProgressBar>("RoundProgress").Value-20)<0.0001,"本轮 5 分钟 / 25 分钟为 20%");Click("PauseButton");
  Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"preview"));
  void Save(string name,int width){Layout(width);Pump();page.UpdateLayout();var image=new RenderTargetBitmap(width,780,96,96,PixelFormats.Pbgra32);image.Render(page);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var f=File.Create(Path.Combine(AppContext.BaseDirectory,"preview",name));png.Save(f);}
  Save("focus-wide.png",920);
  Layout(570);Check(Grid.GetRow(Find<StackPanel>("Summary"))==1,"窄页面统计移到下方");
  Check(Find<ItemsControl>("Totals").ActualWidth<=240,"窄布局统计条不横向拉满页面");
  Save("focus-narrow.png",570);
  task.IsDone=true;repo.SaveTask(task);Check(service.Current?.EndReason==FocusEndReason.TaskCompleted,"保存任务完成状态会结算专注");
  Check(Find<ItemsControl>("History").Items.Count==3,"结算后历史立即更新");
  task.IsDone=false;repo.SaveTask(task);page.SelectTask(other);Find<ComboBox>("ModePicker").SelectedIndex=1;Click("StartButton");Check(service.Current?.Mode==FocusMode.Stopwatch&&service.Current.TaskId==other.Id,"切换任务后开始正计时，记录关联新任务");
  Check(Find<ProgressBar>("RoundProgress").Visibility==Visibility.Collapsed,"正计时不显示完成进度条");
  repo.DeleteTask(other.Id);Check(service.Current?.EndReason==FocusEndReason.TaskDeleted,"删除任务会结算并保留记录");
  Find<ComboBox>("WorkSetting").SelectedItem=35;var button=Find<ComboBox>("WorkSetting");
  using var reminders=new ReminderScheduler(repo,Dispatcher.CurrentDispatcher,_=>{});
  var window=new MainWindow(repo,reminders,null,store,service);window.ShowFocusPage();
  Check(((FocusPage)window.FindName("FocusView")).Visibility==Visibility.Visible,"主窗口能打开专注页");
  ((RadioButton)window.FindName("HomeTab")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
  Check(((FocusPage)window.FindName("FocusView")).Visibility==Visibility.Collapsed,"切回待办隐藏专注页");
  var soloRepo=new DiaryRepository(Path.Combine(AppContext.BaseDirectory,"CdiaryFocusUI",Guid.NewGuid().ToString("N"),"solo.db"));
  var soloTask=soloRepo.AddTask(DateTime.Today,"申论");var soloStore=new FocusStore(soloRepo);var soloService=new FocusService(soloStore,clock);
  page=new FocusPage();page.Attach(soloRepo,soloStore,soloService);surface.RootVisual=page;Layout(920);
  Check(Find<Grid>("DistributionBar").Visibility==Visibility.Collapsed&&Find<ItemsControl>("Totals").Items.Count==0,"无记录不显示空占比或虚构进度");
  soloService.Start(soloTask,FocusMode.Stopwatch);clock.Advance(0.15);soloService.Pause();Layout(920);
  var soloRow=Find<ItemsControl>("Totals").Items.Cast<object>().Single();
  Check(Find<Grid>("DistributionBar").Visibility==Visibility.Collapsed&&(Visibility)soloRow.GetType().GetProperty("ShareVisibility")!.GetValue(soloRow)! == Visibility.Collapsed,"单任务只显示任务和时长，隐藏分配条与百分比");
  Check((string)soloRow.GetType().GetProperty("Time")!.GetValue(soloRow)! == "9 秒","单任务 9 秒记录正常保留");
  Save("focus-single.png",920);
  surface.RootVisual=null;Console.WriteLine($"专注界面：{checks} 项通过，未显示窗口。");
 }
}
sealed class TestClock : TimeProvider
{
 private long ticks;
 private readonly DateTimeOffset start=new DateTimeOffset(DateTime.Today.AddHours(12)).ToUniversalTime();
 public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
 public override long GetTimestamp()=>ticks;
 public override DateTimeOffset GetUtcNow()=>start.AddTicks(ticks);
 public void Advance(double minutes)=>ticks+=TimeSpan.FromMinutes(minutes).Ticks;
}
