using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;

namespace Cdiary;
public partial class FocusPage : System.Windows.Controls.UserControl
{
    private FocusStore? _store;
    private FocusService? _service;
    private DiaryRepository? _repository;
    private IReadOnlyList<FocusSession> _records = [];
    private int _limit = 30;
    private long _lastSummary;
    private DateTime _day = DateTime.Today;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    public FocusPage()
    {
        InitializeComponent();
        foreach(var picker in new[]{MinutesPicker,WorkSetting,ShortSetting,LongSetting}) picker.ItemsSource=Enumerable.Range(1,36).Select(x=>x*5).ToArray();
        RoundsSetting.ItemsSource=Enumerable.Range(1,12).ToArray(); MinutesPicker.SelectedItem=25;
        PomodoroTab.GroupName=StopwatchTab.GroupName=Guid.NewGuid().ToString("N");
        PomodoroTab.Checked+=(_,_)=>ModePicker.SelectedIndex=0;StopwatchTab.Checked+=(_,_)=>ModePicker.SelectedIndex=1;
        ModePicker.SelectionChanged+=(_,_)=>Refresh();MinutesPicker.SelectionChanged+=(_,_)=>Refresh();
        TaskPicker.SelectionChanged+=(_,_)=>Refresh();
        _timer.Tick+=(_,_)=>{if(_day!=DateTime.Today){_day=DateTime.Today;Reload();}else Refresh();};
        IsVisibleChanged+=(_,_)=>{if(IsVisible){Reload();_timer.Start();}else _timer.Stop();};
        Unloaded+=(_,_)=>{_timer.Stop();if(_service!=null)_service.Changed-=Reload;};
        Loaded+=(_,_)=>{if(_service!=null){_service.Changed-=Reload;_service.Changed+=Reload;}if(IsVisible)_timer.Start();};
    }
    public void Attach(DiaryRepository repository,FocusStore store,FocusService service)
    {
        _repository=repository;_store=store;_service=service;service.Changed+=Reload;
        var s=store.LoadSettings(); WorkSetting.SelectedItem=s.WorkMinutes;ShortSetting.SelectedItem=s.ShortBreakMinutes;LongSetting.SelectedItem=s.LongBreakMinutes;RoundsSetting.SelectedItem=s.RoundsBeforeLongBreak;SoundSetting.IsChecked=s.SoundEnabled;MinutesPicker.SelectedItem=s.WorkMinutes;ModePicker.SelectedIndex=s.UseStopwatch?1:0;
        Reload();
    }
    public void SelectTask(DiaryTask task){Reload();TaskPicker.SelectedItem=TaskPicker.Items.Cast<DiaryTask>().FirstOrDefault(x=>x.Id==task.Id);}
    private void Reload()
    {
        if(_store==null||!IsVisible)return;
        _records=_store.Read();var selected=(TaskPicker.SelectedItem as DiaryTask)?.Id;
        TaskPicker.ItemsSource=_repository!.LoadFocusTasks();TaskPicker.SelectedItem=TaskPicker.Items.Cast<DiaryTask>().FirstOrDefault(x=>x.Id==selected)??TaskPicker.Items.Cast<DiaryTask>().FirstOrDefault();
        _lastSummary=0;Refresh();RenderHistory();
    }
    public static string Duration(long ms){var t=TimeSpan.FromMilliseconds(ms);return t.TotalHours>=1?(t.Minutes==0?$"{(int)t.TotalHours} 小时":$"{(int)t.TotalHours} 小时 {t.Minutes} 分"):t.TotalMinutes<1?$"{t.Seconds} 秒":t.Seconds==0?$"{(int)t.TotalMinutes} 分":$"{(int)t.TotalMinutes} 分 {t.Seconds} 秒";}
    public static string Clock(long ms){var t=TimeSpan.FromMilliseconds(Math.Max(0,ms));return t.TotalHours>=1?$"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}":$"{t.Minutes:00}:{t.Seconds:00}";}
    private void Refresh()
    {
        if(_service==null)return;var current=_service.Snapshot();var active=current is {State:not FocusState.Finished};
        DateCaption.Text=DateTime.Today.ToString("M 月 d 日  dddd",System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
        PomodoroTab.IsChecked=ModePicker.SelectedIndex==0;StopwatchTab.IsChecked=ModePicker.SelectedIndex==1;
        CurrentTitle.Text=active?current!.TaskTitle:(TaskPicker.SelectedItem as DiaryTask)?.Title??"请选择任务";
        ClockText.Text=Clock(active?(current!.Mode==FocusMode.Stopwatch?current.ElapsedMilliseconds:current.PlannedMinutes*60000L-current.ElapsedMilliseconds):(ModePicker.SelectedIndex==1?0:(int)(MinutesPicker.SelectedItem??25)*60000L));
        StateText.Text=_service.HasUnsavedChanges?"暂停尚未保存，正在重试":active?(current!.State==FocusState.Paused?$"已暂停 · {current.PauseReason}":current.IsBreak?"休息中，不计入任务投入":"正在专注"):"选择任务，开始一段专注时间";
        StartButton.Visibility=active?Visibility.Collapsed:Visibility.Visible;PauseButton.Visibility=EndButton.Visibility=active?Visibility.Visible:Visibility.Collapsed;
        PauseButton.Content=current?.State==FocusState.Paused?"继续":"暂停";
        BreakButton.Visibility=current is {State:FocusState.Finished,IsBreak:false}?Visibility.Visible:Visibility.Collapsed;
        TaskPicker.IsEnabled=ModePicker.IsEnabled=MinutesPicker.IsEnabled=!active;MinutesPicker.Visibility=ModePicker.SelectedIndex==0?Visibility.Visible:Visibility.Collapsed;
        SetupRow.Visibility=TaskPicker.Visibility=active?Visibility.Collapsed:Visibility.Visible;
        SessionHeader.Visibility=active?Visibility.Visible:Visibility.Collapsed;
        SessionMode.Text=current?.IsBreak==true?"休息":current?.Mode==FocusMode.Stopwatch?"正计时":"番茄钟";
        var cycle=(int)(RoundsSetting.SelectedItem??4);
        SessionRound.Text=current?.Mode==FocusMode.Pomodoro?$"第 {_records.Count(x=>x.CompletedPomodoro)%cycle+1} 轮 / {cycle} 轮":"";
        if(active&&current!.State==FocusState.Running&&!_service.HasUnsavedChanges)
            StateText.Text=current.IsBreak?"休息中，不计入任务投入":"本轮已专注 "+Duration(current.ElapsedMilliseconds);
        RoundProgress.Visibility=active&&current!.Mode==FocusMode.Pomodoro?Visibility.Visible:Visibility.Collapsed;
        RoundProgress.Value=active&&current!.Mode==FocusMode.Pomodoro?Math.Clamp(100.0*current.ElapsedMilliseconds/(current.PlannedMinutes*60000L),0,100):0;
        _timer.Interval=TimeSpan.FromSeconds(active?1:30);
        if(_lastSummary!=0&&Environment.TickCount64-_lastSummary<10000)return;
        _lastSummary=Environment.TickCount64;
        var records=_records.Where(x=>x.Id!=current?.Id).Concat(current==null?[]:new[]{current}).Where(x=>!x.IsBreak).ToArray();
        var day=DateOnly.FromDateTime(DateTime.Today);
        var groups=records.GroupBy(x=>x.TaskId).Select(g=>new{Title=g.First().TaskTitle,Ms=(long)FocusStatistics.ByDay(g,TimeZoneInfo.Local).GetValueOrDefault(day).TotalMilliseconds}).Where(x=>x.Ms>0).OrderByDescending(x=>x.Ms).ToArray();
        var total=groups.Sum(x=>x.Ms);TodayTotal.Text=Duration(total);RoundText.Text=$"完成 {records.Count(x=>x.CompletedPomodoro&&x.EndedUtc?.LocalDateTime.Date==DateTime.Today)} 轮番茄";
        var multiple=groups.Length>1;
        string[] palette=["#70C5AE","#B3CDA0","#D6B883","#A5BBD1","#D3A6B2","#B4AED0","#87BDB9","#C6BB93"];
        var rows=groups.Select((x,i)=>new{x.Title,Time=Duration(x.Ms),Percent=100.0*x.Ms/total,Color=palette[i%palette.Length],ShareVisibility=multiple?Visibility.Visible:Visibility.Collapsed,
            ShareText=100.0*x.Ms/total<0.1?"不足 0.1%":$"{100.0*x.Ms/total:0.#}%"}).ToArray();
        Totals.ItemsSource=rows;
        DistributionBar.Children.Clear();DistributionBar.ColumnDefinitions.Clear();
        DistributionBar.Visibility=multiple?Visibility.Visible:Visibility.Collapsed;
        if(multiple)for(var i=0;i<rows.Length;i++)
        {
            var row=rows[i];DistributionBar.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(row.Percent,GridUnitType.Star)});
            var segment=new Border{Background=(System.Windows.Media.Brush)new BrushConverter().ConvertFromString(row.Color)!,ToolTip=$"{row.Title} · {row.Time} · {row.ShareText}"};
            Grid.SetColumn(segment,i);DistributionBar.Children.Add(segment);
        }
        ProgressHint.Text=multiple?"今日时间分配 · 包含正在进行的专注":groups.Length==1?"今日累计时间 · 包含正在进行的专注":"还没有投入记录，开始一段专注吧。";
    }
    private void RenderHistory()
    {
        var cutoff=HistoryFilter.SelectedIndex switch{0=>DateTime.Today,1=>DateTime.Today.AddDays(-6),_=>DateTime.MinValue};
        var rows=_records.Where(x=>x.State==FocusState.Finished&&(x.EndedUtc?.LocalDateTime??x.StartedUtc.LocalDateTime)>=cutoff).ToArray();
        History.ItemsSource=rows.Take(_limit).Select(x=>new{Title=x.TaskTitle,Time=Duration(x.ElapsedMilliseconds),Period=$"{x.StartedUtc.LocalDateTime:HH:mm} — {x.EndedUtc?.LocalDateTime:HH:mm}",Detail=$"{x.StartedUtc.LocalDateTime:yyyy-MM-dd HH:mm} — {x.EndedUtc?.LocalDateTime:yyyy-MM-dd HH:mm}",Result=x.IsBreak?"休息":x.CompletedPomodoro?"完成一轮":x.EndReason==FocusEndReason.TaskDeleted?"任务已删除":x.EndReason==FocusEndReason.TaskCompleted?"任务已完成":x.Mode==FocusMode.Pomodoro?"提前结束":"结束计时",Color=x.CompletedPomodoro?"#177A70":"#99886E"}).ToArray();
        EmptyText.Visibility=rows.Length==0?Visibility.Visible:Visibility.Collapsed;MoreButton.Visibility=rows.Length>_limit?Visibility.Visible:Visibility.Collapsed;
    }
    private void Run(Action action){try{action();ErrorText.Text="";Reload();}catch(Exception e){ErrorText.Text="尚未完成操作："+e.Message;}}
    private void StartClick(object sender,RoutedEventArgs e)=>Run(()=>{if(TaskPicker.SelectedItem is not DiaryTask task)throw new InvalidOperationException("请先在待办页添加或选择一个任务。");_store!.SaveSettings(_store.LoadSettings() with {UseStopwatch=ModePicker.SelectedIndex==1,WorkMinutes=(int)MinutesPicker.SelectedItem});_service!.Start(task,ModePicker.SelectedIndex==0?FocusMode.Pomodoro:FocusMode.Stopwatch,(int)MinutesPicker.SelectedItem);});
    private void PauseClick(object sender,RoutedEventArgs e)=>Run(()=>{if(_service!.Snapshot()?.State==FocusState.Paused)_service.Resume();else _service.Pause();});
    private void EndClick(object sender,RoutedEventArgs e)=>Run(()=>_service!.Finish());
    private void BreakClick(object sender,RoutedEventArgs e)=>Run(()=>{var s=_store!.LoadSettings();var mode=_service!.SuggestedBreak(s);var id=_service.Current!.TaskId;var task=_repository!.LoadFocusTasks().Concat(_repository.LoadCompletedTasks()).FirstOrDefault(x=>x.Id==id);if(task==null)throw new InvalidOperationException("任务已删除，请选择另一项待办后开始新的专注。");_service.Start(task,mode,mode==FocusMode.LongBreak?s.LongBreakMinutes:s.ShortBreakMinutes);});
    private void SaveSettingsClick(object sender,RoutedEventArgs e)=>Run(()=>{_store!.SaveSettings(new((int)WorkSetting.SelectedItem,(int)ShortSetting.SelectedItem,(int)LongSetting.SelectedItem,(int)RoundsSetting.SelectedItem,SoundSetting.IsChecked==true,ModePicker.SelectedIndex==1));MinutesPicker.SelectedItem=WorkSetting.SelectedItem;});
    private void FilterChanged(object sender,SelectionChangedEventArgs e){if(History==null)return;_limit=30;RenderHistory();}
    private void MoreClick(object sender,RoutedEventArgs e){_limit+=30;RenderHistory();}
    private void Resize(object sender,SizeChangedEventArgs e){if(Summary==null)return;var narrow=e.NewSize.Width<690;Grid.SetColumn(Summary,narrow?0:1);Grid.SetRow(Summary,narrow?1:0);Grid.SetColumnSpan(TimerCard,narrow?2:1);Grid.SetColumnSpan(Summary,narrow?2:1);Summary.Margin=narrow?new Thickness(0,22,0,0):new Thickness(24,8,0,0);}
}
