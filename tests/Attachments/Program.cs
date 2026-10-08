using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cdiary;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition,string name){if(!condition)throw new Exception(name);Console.WriteLine("通过："+name);_checks++;}
    [STAThread] private static void Main(string[] args)
    {
        Exception? failure=null;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async()=>{try{await Run(args);}catch(Exception error){failure=error;}finally{Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}}));
        Dispatcher.Run();if(failure is not null)throw failure;
    }
    private static byte[] Picture(int width,int height,bool jpeg=false)
    {
        var pixels=new byte[width*height*4];
        for(var i=0;i<pixels.Length;i+=4){pixels[i]=180;pixels[i+1]=200;pixels[i+2]=30;pixels[i+3]=255;}
        var source=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);
        BitmapEncoder encoder=jpeg?new JpegBitmapEncoder():new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream=new MemoryStream();encoder.Save(stream);return stream.ToArray();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {if(root is T typed)yield return typed;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var nested in Descendants<T>(child))yield return nested;}
    private static async Task Run(string[] args)
    {
        var folder=Path.Combine(Path.GetTempPath(),"CdiaryAttachmentTests",Guid.NewGuid().ToString("N"));
        var repository=new DiaryRepository(Path.Combine(folder,"diary.db"));var store=repository.Attachments;
        var bytes=Picture(1800,1000);var attachment=await store.ImportAsync(bytes);
        Check(File.ReadAllBytes(store.Resolve(attachment.Reference,true)!).SequenceEqual(bytes),"原图独立保存且内容不变");
        Check(store.ReadPreview(attachment.Reference)!.PixelWidth==1024,"正文使用缩小的预览图");
        Check((await store.ImportAsync(bytes)).Id==attachment.Id && store.Usage().Count==1,"重复图片去重");
        Check(store.Resolve("attachments/../diary.db",true) is null && store.Resolve(attachment.Reference+"/../x",true) is null,"路径只能解析有效附件 ID");
        var jpeg=await store.ImportAsync(Picture(80,50,true));Check(jpeg.Extension=="jpg" && store.ReadPreview(jpeg.Reference) is not null,"支持 JPEG 原图与 PNG 预览");
        foreach(var invalid in new[]{new byte[]{1,2,3},new byte[NoteAttachmentStore.MaxBytes+1]})
        {var rejected=false;try{await store.ImportAsync(invalid);}catch{rejected=true;}Check(rejected,"拒绝损坏或超限图片");}
        Check(!Directory.EnumerateFiles(store.DirectoryPath,"*.tmp",SearchOption.AllDirectories).Any(),"原子保存不留下临时文件");
        var sourcePath=Path.Combine(folder,"external.png");File.WriteAllBytes(sourcePath,bytes);await store.ImportFileAsync(sourcePath);File.Delete(sourcePath);
        Check(File.Exists(store.Resolve(attachment.Reference,true)),"删除外部原图不影响笔记附件");
        var task=repository.AddTask(DateTime.Today,"图片笔记");task.Note=$"![图片]({attachment.Reference})";repository.SaveTask(task);
        var fresh=new DiaryRepository(repository.DatabasePath).Attachments;
        Check(fresh.Usage().UnusedCount==1,"重新打开后通过笔记引用识别有效附件");
        repository.BackupOriginalNote(task.Id,task.Note);task.Note="正文已删除图片";repository.SaveTask(task);
        Check(fresh.Usage().UnusedCount==1,"原文备份继续保护图片");
        repository.SaveNoteRecovery(task.Id,$"![找回]({jpeg.Reference})");Check(fresh.Usage().UnusedCount==0,"冲突恢复副本继续保护图片");
        repository.DeleteTask(task.Id);Check(store.CleanUnused()==0,"本次运行的图片保留供撤销使用");
        var clean=new DiaryRepository(repository.DatabasePath).Attachments;
        Check(clean.CleanUnused()==2 && clean.Usage().Count==0,"重启后可清理真正未使用的图片");
        attachment=await store.ImportAsync(bytes);
        task=repository.AddTask(DateTime.Today,"原生模式");task.Note="插入位置";repository.SaveTask(task);
        var view=new MarkdownNoteView{DocumentRepository=repository,Task=task};
        view.Measure(new Size(360,480));view.Arrange(new Rect(0,0,360,480));view.UpdateLayout();
        view.CaretIndex=view.Text.Length;await view.InsertImageBytesAsync(bytes);
        Check(task.Note.Contains(attachment.Reference) && repository.LoadTasks(DateTime.Today).Single(t=>t.Id==task.Id).Note==task.Note,"源码插图同步模型和数据库");
        var editor=(ICSharpCode.AvalonEdit.TextEditor)view.FindName("Editor");editor.Undo();Check(!task.Note.Contains("attachments/"),"源码图片插入可一次撤销");
        editor.Redo();Check(task.Note.Contains(attachment.Reference),"源码重做仍可找到图片");
        view.SetMode(NoteViewMode.Preview);
        var images=Descendants<NotePreviewImage>(view.PreviewDocument!).ToArray();
        Check(images.Length==1 && !view.IsDocumentReady,"轻量预览原生显示图片，不加载浏览器");
        await images[0].LoadPreviewAsync();Check(images[0].Child is Image{Source:not null},"原生图片读取预览文件");
        view.Text=$"```md\n![图片]({attachment.Reference})\n```";view.RenderPreview();
        Check(!Descendants<NotePreviewImage>(view.PreviewDocument!).Any() && new System.Windows.Documents.TextRange(view.PreviewDocument!.ContentStart,view.PreviewDocument.ContentEnd).Text.Contains(attachment.Reference),"代码块中的图片语法保留原文");
        view.SetMode(NoteViewMode.Edit);var next=repository.AddTask(DateTime.Today,"另一任务");
        var importing=view.InsertImageBytesAsync(bytes);view.Task=next;await importing;
        Check(next.Note=="","图片导入中切换任务不会插入另一任务");
        var data=new DataObject();data.SetImage(ScreenshotService.Decode(bytes));
        await view.InsertImageDataAsync(data);
        Check(next.Note.Contains("attachments/"),"截图使用的 WPF Bitmap 数据可插入源码，未触碰系统剪贴板");
        var screenshotBox=new ScreenshotStore(Path.Combine(folder,"Screenshots"));
        screenshotBox.Save(bytes,ScreenshotService.Thumbnail(ScreenshotService.Decode(bytes)),1800,1000,DateTime.Today.AddDays(-1));
        screenshotBox.Cleanup(DateTime.Now);
        Check(screenshotBox.Load().Count==0 && store.Resolve(attachment.Reference,true) is not null,"截图箱到期清理不影响笔记图片");
        view.Text=$"# 图片笔记\n\n截图已永久保存。\n\n![图片]({attachment.Reference})";view.SetMode(NoteViewMode.Preview);
        foreach(var image in Descendants<NotePreviewImage>(view.PreviewDocument!).ToArray())await image.LoadPreviewAsync();
        view.Measure(new Size(360,480));view.Arrange(new Rect(0,0,360,480));view.UpdateLayout();
        if(args.Length>0){Directory.CreateDirectory(args[0]);var rendered=new RenderTargetBitmap(360,480,96,96,PixelFormats.Pbgra32);rendered.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(rendered));using var output=File.Create(Path.Combine(args[0],"native-image.png"));encoder.Save(output);}
        using var scheduler=new ReminderScheduler(repository,Dispatcher.CurrentDispatcher,_=>{});
        var main=new MainWindow(repository,scheduler);
        ((System.Windows.Controls.RadioButton)main.FindName("SettingsTab")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Task.Delay(150);
        var panel=(FrameworkElement)main.Content;panel.Measure(new Size(760,580));panel.Arrange(new Rect(0,0,760,580));panel.UpdateLayout();
        Check(((TextBlock)main.FindName("AttachmentUsageText")).Text.Contains("MB"),"设置页异步显示附件占用");
        var cleanButton=(Button)main.FindName("CleanAttachmentsButton");
        Check(!cleanButton.IsEnabled,"有撤销保护的图片不会出现可清理入口");
        var settingsScroll=Descendants<ScrollViewer>((DependencyObject)main.FindName("SettingsPage")).First();settingsScroll.ScrollToBottom();panel.UpdateLayout();
        if(args.Length>0){var rendered=new RenderTargetBitmap(760,580,96,96,PixelFormats.Pbgra32);rendered.Render(panel);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(rendered));using var output=File.Create(Path.Combine(args[0],"attachment-settings.png"));encoder.Save(output);}
        var viewer=new ScreenshotViewer("笔记图片",ScreenshotService.Decode(bytes),main,store.Resolve(attachment.Reference,true)!);
        var viewerContent=(FrameworkElement)viewer.Content;viewerContent.Measure(new Size(920,650));viewerContent.Arrange(new Rect(0,0,920,650));viewerContent.UpdateLayout();
        Check(viewer.Title=="笔记图片" && viewer.WindowStyle==WindowStyle.None,"原图查看器复用无系统边框的缩放与复制界面");
        Console.WriteLine($"附件：{_checks} 项检查通过，使用临时数据，未读写系统剪贴板、未显示窗口。");
    }
}
