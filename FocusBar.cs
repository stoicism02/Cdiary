using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;

namespace Cdiary;
public sealed class FocusBar : Border
{
    private FocusService? _service;
    private FocusStore? _store;
    public DiaryTask? Task { get; set; }
    public event Action<DiaryTask>? OpenRequested;
    private readonly TextBlock _text=new(){VerticalAlignment=VerticalAlignment.Center,FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis};
    private readonly System.Windows.Controls.Button _action=new(){Padding=new Thickness(8,4,8,4),FontSize=11};
    private readonly System.Windows.Controls.Button _end=new(){Content="结束",Padding=new Thickness(8,4,8,4),FontSize=11,Margin=new Thickness(5,0,0,0)};
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(1)};
    private long _total;
    public FocusBar()
    {
        Background=new SolidColorBrush(System.Windows.Media.Color.FromRgb(230,245,233));CornerRadius=new CornerRadius(13);Padding=new Thickness(9,6,9,6);Margin=new Thickness(0,0,0,8);
        var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});grid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        grid.Children.Add(_text);grid.Children.Add(_action);grid.Children.Add(_end);Grid.SetColumn(_action,1);Grid.SetColumn(_end,2);Child=grid;
        _action.SetResourceReference(FrameworkElement.StyleProperty,"Pill");_end.SetResourceReference(FrameworkElement.StyleProperty,"Pill");
        _action.Click+=(_,_)=>Act(false);_end.Click+=(_,_)=>Act(true);
        _timer.Tick+=(_,_)=>Refresh();
        Loaded+=(_,_)=>{if(_service!=null){_service.Changed-=Reload;_service.Changed+=Reload;}Reload();if(IsVisible)_timer.Start();};
        Unloaded+=(_,_)=>{_timer.Stop();if(_service!=null)_service.Changed-=Reload;};
        IsVisibleChanged+=(_,_)=>{if(IsVisible){Reload();_timer.Start();}else _timer.Stop();};
    }
    public void Attach(FocusStore store,FocusService service){_store=store;_service=service;Reload();}
    public void Reload(){if(_store!=null&&Task!=null)_total=_store.Read(Task.Id).Where(x=>!x.IsBreak).Sum(x=>x.ElapsedMilliseconds);Refresh();}
    private void Refresh()
    {
        var s=_service?.Snapshot();var active=Task!=null&&s?.TaskId==Task.Id&&s.State!=FocusState.Finished;
        _text.Text=active?(s!.IsBreak?"休息 ":"")+FocusPage.Clock(s.Mode==FocusMode.Stopwatch?s.ElapsedMilliseconds:s.PlannedMinutes*60000L-s.ElapsedMilliseconds):"累计 "+FocusPage.Duration(_total);
        _action.Content=active?(s!.State==FocusState.Paused?"继续":"暂停"):"开始专注";
        _action.IsEnabled=Task!=null&&!Task.IsDone;_end.Visibility=active?Visibility.Visible:Visibility.Collapsed;
        _timer.Interval=TimeSpan.FromSeconds(active?1:30);
    }
    private void Act(bool finish)
    {
        try
        {
            if(_service==null||Task==null)return;
            var s=_service.Snapshot();
            if(s?.TaskId==Task.Id&&s.State!=FocusState.Finished){if(finish)_service.Finish();else if(s.State==FocusState.Paused)_service.Resume();else _service.Pause();}
            else
            {
                if(s is{State:not FocusState.Finished})
                {
                    if(System.Windows.MessageBox.Show(Window.GetWindow(this),$"结束“{s.TaskTitle}”的计时并切换到当前任务？","切换专注",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                    _service.Finish(FocusEndReason.Switched);
                }
                OpenRequested?.Invoke(Task);
            }
            Reload();
        }
        catch(Exception error){_text.Text="未保存";ToolTip=error.Message;}
    }
}
