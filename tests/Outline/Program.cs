using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cdiary;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("通过："+message);_checks++;}
    [STAThread] private static void Main(string[] args)
    {
        Exception? failure=null;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async()=>{try{await Run(args);}catch(Exception error){failure=error;}finally{Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}}));
        Dispatcher.Run();if(failure is not null)throw failure;
        Console.WriteLine($"目录：{_checks} 项检查通过，未显示窗口。");
    }
    private static void Layout(FrameworkElement view,double width,double height){view.Measure(new Size(width,height));view.Arrange(new Rect(0,0,width,height));view.UpdateLayout();}
    private static async Task Run(string[] args)
    {
        var parsed=NoteOutline.Parse("# **复习** [链接](https://example.com)\r\n\r\n## 重复\r\n正文\r\n\r\n## 重复\r\n\r\n```md\r\n# 代码里的标题\r\n```\r\n    # 缩进代码\r\n\\# 转义\r\n\r\n下划线标题\r\n===\r\n\r\n###### 六级\r\n");
        Check(parsed.Count==5,"提取六级范围标题，排除围栏代码、缩进代码和转义井号");
        Check(parsed[0].Text=="复习 链接" && parsed[4].Level==6,"标题去除粗体和链接语法，保留等级");
        Check(parsed[1].Text==parsed[2].Text && parsed[1].Offset!=parsed[2].Offset,"同名标题保留不同位置");
        Check(parsed[1].Line==3 && parsed[1].Offset=="# **复习** [链接](https://example.com)\r\n\r\n".Length,"源码行号和 CRLF 偏移准确");
        Check(parsed[3].Text=="下划线标题" && parsed[3].Level==1,"支持下划线形式标题");
        Check(NoteOutline.Parse("- ```md\n  # 列表中的代码\n  ```\n\n# 正文").Count==1,"列表围栏中的井号不是标题");
        Check(NoteOutline.Parse("第一行\n第二行\n---").Single().Text=="第一行 第二行","多行下划线标题定位到段落开头");
        var repository=new DiaryRepository(Path.Combine(Path.GetTempPath(),"CdiaryOutlineTests",Guid.NewGuid().ToString("N"),"diary.db"));
        var task=repository.AddTask(DateTime.Today,"复习笔记");
        var filler=string.Join("\n\n",Enumerable.Repeat("复习正文，用于检查目录跳转与阅读位置同步。",16));
        task.Note=$"# 今日复习\n\n## Redis\n\n{filler}\n\n### 重复标题\n\n{filler}\n\n## MySQL\n\n### 重复标题\n\n{filler}\n\n#### 索引\n\n##### 执行计划\n\n###### 补充\n\n{filler}";repository.SaveTask(task);
        using var surface=new HwndSource(new HwndSourceParameters("Outline offscreen"){Width=900,Height=600,PositionX=-32000,PositionY=-32000,WindowStyle=unchecked((int)0x80000000)});
        var view=new MarkdownNoteView{DocumentRepository=repository,Task=task};surface.RootVisual=view;Layout(view,900,600);
        var original=task.Note;var renders=view.RenderCount;
        Check(!view.IsOutlineOpen,"目录默认收起");view.SetOutlineOpen(true);view.RefreshOutline();
        Check(view.IsOutlineDocked && view.OutlineItems.Count==8,"宽笔记右侧展开目录");
        Check(view.RenderCount==renders && !view.IsDocumentReady,"源码目录不生成预览或启动浏览器");
        var duplicate=view.OutlineItems.Where(h=>h.Text=="重复标题").Last();
        await view.NavigateOutlineAsync(duplicate.Id);
        Check(view.CaretIndex==duplicate.Offset && task.Note==original,"源码跳转到第二个同名标题且不修改笔记");
        view.SetMode(NoteViewMode.Preview);Layout(view,900,600);await Task.Delay(150);view.RefreshOutline();
        Check(view.OutlineItems.Count==8 && view.OutlineItems.Last().Level==6,"原生预览从实际排版提取全部标题");
        duplicate=view.OutlineItems.Where(h=>h.Text=="重复标题").Last();await view.NavigateOutlineAsync(duplicate.Id);await Task.Delay(150);
        Check(view.ActiveOutlineId==duplicate.Id,"预览跳转后高亮对应章节");
        var preview=(FrameworkElement)view.FindName("Preview");
        var scroll=FindScroll(preview)!;
        Check(scroll.VerticalOffset>0,"原生目录跳转实际滚动正文");
        scroll.ScrollToTop();await Task.Delay(150);
        Check(view.ActiveOutlineId==view.OutlineItems[0].Id,"原生预览手动滚回顶部同步章节高亮");
        var outlinePanel=(NoteOutlinePanel)((Border)view.FindName("OutlineSidebar")).Child;
        var list=(ListBox)outlinePanel.FindName("HeadingsList");
        list.SelectedIndex=2;outlinePanel.SetActive(view.OutlineItems[0].Id);
        Check(list.SelectedIndex==2 && outlinePanel.ActiveHeadingId==view.OutlineItems[0].Id,"阅读高亮更新不改变目录键盘选择");
        Check(task.Note==original && !view.IsDocumentReady,"预览跳转保留原文且不加载文档编辑器");
        Layout(view,340,400);Check(view.IsOutlineOpen && !view.IsOutlineDocked && ((ColumnDefinition)view.FindName("OutlineColumn")).Width.Value==0,"窄便签目录不占正文宽度");
        await view.NavigateOutlineAsync(view.OutlineItems[0].Id);Check(!view.IsOutlineOpen,"窄便签选择章节后收起目录");
        view.SetMode(NoteViewMode.Edit);view.SetOutlineOpen(true);
        var stale=view.OutlineItems[0].Id;view.Text="# 新标题\n\n正文";await Task.Delay(420);
        Check(view.OutlineItems.Count==1 && view.OutlineItems[0].Text=="新标题","停止输入后更新目录并移除旧标题");
        var before=view.CaretIndex;await view.NavigateOutlineAsync(stale);Check(view.CaretIndex==before,"过期目录项不会跳到错误位置");
        var next=repository.AddTask(DateTime.Today,"无标题笔记");next.Note="普通正文";view.Task=next;view.RefreshOutline();
        Check(view.OutlineItems.Count==0,"切换任务立即清空上一任务目录");
        using var secondSurface=new HwndSource(new HwndSourceParameters("Second outline offscreen"){Width=900,Height=600,PositionX=-32000,PositionY=-32000,WindowStyle=unchecked((int)0x80000000)});
        var other=new MarkdownNoteView{DocumentRepository=repository,Task=next};secondSurface.RootVisual=other;Layout(other,900,600);other.SetOutlineOpen(true);
        next.Note="# 两个窗口同步";await Task.Delay(420);
        Check(view.OutlineItems.Single().Text=="两个窗口同步" && other.OutlineItems.Single().Text=="两个窗口同步","两个笔记控件同步标题变化");
        other.SetOutlineOpen(false);Check(view.IsOutlineOpen,"每个窗口独立保留目录展开状态");
        foreach(var width in new[]{900,340})
        {
            Layout(view,width,400);view.SetOutlineOpen(false);
            var button=(Button)view.FindName("OutlineButton");
            for(var i=0;i<2;i++)
            {
                button.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=UIElement.PreviewMouseDownEvent});
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(view.IsOutlineOpen==(i==0),$"原生目录 {width} DIP 第 {i+1} 次点击正确切换开关");
            }
            view.SetOutlineOpen(true);
            view.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=UIElement.PreviewMouseDownEvent});
            Check(view.IsOutlineOpen==(width>=720),$"点击正文空白仅收起窄目录（{width} DIP）");
        }
        view.Task=task;view.Text=original;view.SetMode(NoteViewMode.Preview);Layout(view,900,600);view.SetOutlineOpen(true);Layout(view,900,600);
        if(args.Length>0){Directory.CreateDirectory(args[0]);Render(view,Path.Combine(args[0],"outline-wide.png"),900,600);var panel=new NoteOutlinePanel();panel.SetEntries(view.OutlineItems);Render(panel,Path.Combine(args[0],"outline-popup.png"),260,340);}
    }
    private static void Render(FrameworkElement view,string path,int width,int height)
    {Layout(view,width,height);var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(path);encoder.Save(stream);}
    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if(root is ScrollViewer scroll)return scroll;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            if(FindScroll(VisualTreeHelper.GetChild(root,i)) is {} child)return child;
        return null;
    }
}
