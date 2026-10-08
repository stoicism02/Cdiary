using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cdiary;
using Microsoft.Web.WebView2.Core;

class Program
{
 static int checks;
 static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("通过："+message);checks++;}
 static async Task Until(Func<Task<bool>> condition){var watch=Stopwatch.StartNew();while(!await condition()){if(watch.ElapsedMilliseconds>10000)throw new TimeoutException();await Task.Delay(40);}}
 static async Task<T> Eval<T>(DocumentNoteHost host,string script)=>JsonSerializer.Deserialize<T>(await host.Browser!.ExecuteScriptAsync(script))!;
 static async Task Key(DocumentNoteHost host,string key,int code,int modifiers=2){foreach(var type in new[]{"keyDown","keyUp"})await host.Browser!.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",JsonSerializer.Serialize(new{type,key,windowsVirtualKeyCode=code,modifiers}));}
 static void Layout(FrameworkElement view){view.Measure(new Size(800,500));view.Arrange(new Rect(0,0,800,500));view.UpdateLayout();}
 static DocumentNoteHost Host(MarkdownNoteView view)=>(DocumentNoteHost)((Border)view.FindName("DocumentContainer")).Child;
 static ScrollViewer? Scroll(DependencyObject node){if(node is ScrollViewer s)return s;for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)if(Scroll(VisualTreeHelper.GetChild(node,i)) is {} found)return found;return null;}
 static NotePreviewImage? Picture(DependencyObject node){if(node is NotePreviewImage image)return image;foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())if(Picture(child) is {} found)return found;return null;}
 [STAThread]static void Main(string[] args){Exception? failure=null;Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async()=>{try{await Run(args);}catch(Exception e){failure=e;}finally{Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}}));Dispatcher.Run();if(failure!=null)throw failure;Console.WriteLine($"切换与只读：{checks} 项通过，未显示窗口。");}
 static async Task Run(string[] args)
 {
  var repo=new DiaryRepository(Path.Combine(Path.GetTempPath(),"CdiaryTransitions",Guid.NewGuid().ToString("N"),"test.db"));
  var task=repo.AddTask(DateTime.Today,"书写交接");
  task.Note="# 今日学习\n\n- [ ] 完成复习\n\n"+string.Join("\n\n",Enumerable.Range(1,60).Select(i=>$"第 {i} 段学习笔记，正文用于检验编辑与阅读之间的位置交接。"));repo.SaveTask(task);
  using var surface=new HwndSource(new HwndSourceParameters("Invisible transition checks"){Width=800,Height=500,PositionX=-32000,PositionY=-32000,WindowStyle=unchecked((int)0x80000000)});
  var view=new MarkdownNoteView{DocumentRepository=repo,Task=task};surface.RootVisual=view;Layout(view);view.SetMode(NoteViewMode.Preview);Layout(view);
  var preview=(FlowDocumentScrollViewer)view.FindName("Preview");Scroll(preview)!.ScrollToVerticalOffset(730);await Task.Delay(150);Layout(view);
  var before=view.CaptureReadingPosition();Check(before is not null && before.Index>5,"主窗口记录滚动到中段的段落位置");
  var opening=view.OpenDocumentAsync();
  Check(((Grid)view.FindName("NativePanel")).Visibility==Visibility.Visible,"编辑器初始化期间保留原阅读页面");
  await opening;var host=Host(view);await Task.Delay(100);
  var entered=await host.ReadPositionAsync();
  Check(entered?.Text==before?.Text,"进入编辑后仍显示同一段正文");
  Check(await Eval<bool>(host,"!document.getElementById('finish').hidden"),"主窗口保留完成按钮");
  await host.Browser!.ExecuteScriptAsync("island.editor.vditor.wysiwyg.element.scrollTop+=360");await Task.Delay(100);var leaving=await host.ReadPositionAsync();
  await view.FinishDocumentAsync();Layout(view);await Task.Delay(100);
  Check(!view.IsDocumentReady && view.Mode==NoteViewMode.Preview,"主窗口结束编辑后释放浏览器");
  Check(view.CaptureReadingPosition()?.Text==leaving?.Text,"返回原生阅读仍显示编辑前所在段落");
  view.RetainDocumentForReading=true;var original=task.Note;await view.OpenDocumentAsync();host=Host(view);
  Check(await Eval<bool>(host,"document.getElementById('finish').hidden"),"独立便签不显示完成按钮");
  var browser=host.Browser;
  await browser!.ExecuteScriptAsync("(()=>{const root=island.editor.vditor.wysiwyg.element;root.focus();const p=root.querySelector('p');const r=document.createRange();r.selectNodeContents(p);r.collapse(false);getSelection().removeAllRanges();getSelection().addRange(r);root.scrollTop=450})()");
  await browser.CallDevToolsProtocolMethodAsync("Input.insertText",JsonSerializer.Serialize(new{text=" 本次新增文字"}));await Task.Delay(250);
  var edited=task.Note;var token=await Eval<string>(host,"island.session.token");var offset=await Eval<double>(host,"island.editor.vditor.wysiwyg.element.scrollTop");
  await Key(host,"s",83);await Until(()=>Eval<bool>(host,"island.readOnly"));
  Check(host.IsReadOnly && view.IsDocumentReady && ReferenceEquals(browser,host.Browser),"Ctrl+S 保存后原地只读，保留同一浏览器");
  Check(repo.LoadTasks(DateTime.Today).Single(t=>t.Id==task.Id).Note==edited,"切为只读之前已写入数据库");
  Check(await Eval<string>(host,"island.session.token")==token && Math.Abs(await Eval<double>(host,"island.editor.vditor.wysiwyg.element.scrollTop")-offset)<2,"只读切换保留会话和滚动位置");
  await browser.ExecuteScriptAsync("document.querySelector('.vditor-reset input[type=checkbox]').click()");await Key(host,"z",90);await Task.Delay(100);
  Check(task.Note==edited && await Eval<bool>(host,"island.readOnly"),"只读模式不会通过清单或撤销快捷键修改内容");
  if(args.Length>0){Directory.CreateDirectory(args[0]);using var file=File.Create(Path.Combine(args[0],"sticky-readonly.png"));await browser.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);}
  await browser.ExecuteScriptAsync("document.querySelector('.vditor-reset p').dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,button:0}))");await Until(()=>Task.FromResult(!host.IsReadOnly));
  Check(await Eval<bool>(host,"!island.readOnly && island.editor.vditor.wysiwyg.element.contentEditable==='true'") && ReferenceEquals(browser,host.Browser),"点击正文立即复用原编辑器继续输入");
  await Key(host,"z",90);await Until(()=>Task.FromResult(!task.Note.Contains("本次新增文字")));
  Check(task.Note.Contains("学习笔记"),"阅读和编辑切换后撤销历史仍有效");
  await browser.ExecuteScriptAsync("island.editor.vditor.wysiwyg.element.dispatchEvent(new CompositionEvent('compositionstart',{bubbles:true}))");await Key(host,"s",83);
  Check(!host.IsReadOnly && await Eval<bool>(host,"!island.readOnly"),"中文组词未确认时 Ctrl+S 不结束编辑");
  await browser.ExecuteScriptAsync("island.editor.vditor.wysiwyg.element.dispatchEvent(new CompositionEvent('compositionend',{bubbles:true}))");
  host.Repository=null;
  await browser.ExecuteScriptAsync("island.editor.focus()");await browser.CallDevToolsProtocolMethodAsync("Input.insertText",JsonSerializer.Serialize(new{text=" 模拟保存失败"}));await Key(host,"s",83);
  await Until(()=>Eval<bool>(host,"document.getElementById('state').textContent.includes('未能结束编辑')"));
  Check(!host.IsReadOnly && await Eval<bool>(host,"island.editor.vditor.wysiwyg.element.contentEditable==='true'"),"保存失败保留编辑状态和输入");
  host.Repository=repo;await Key(host,"s",83);await Until(()=>Eval<bool>(host,"island.readOnly"));
  task.Note=task.Note.Replace("第 60 段","外部更新的第 60 段");repo.SaveTask(task);
  await Until(()=>Eval<bool>(host,"island.editor.getValue().includes('外部更新')"));
  Check(await Eval<bool>(host,"island.readOnly && island.editor.vditor.wysiwyg.element.contentEditable==='false'"),"另一窗口更新正文后仍保持只读");
  view.Visibility=Visibility.Collapsed;await Until(()=>Task.FromResult(!view.IsDocumentReady));
  Check(!view.IsDocumentReady,"隐藏便签后保存并释放浏览器");
  view.Visibility=Visibility.Visible;Layout(view);await view.OpenDocumentAsync();var next=repo.AddTask(DateTime.Today,"切换目标");next.Note="新任务正文";view.Task=next;
  await Until(()=>Task.FromResult(!view.IsDocumentReady));Check(view.Text==next.Note,"切任务释放旧会话且不覆盖新任务");
  var pending=view.OpenDocumentAsync();view.Task=task;await pending;await Until(()=>Task.FromResult(!view.IsDocumentReady));
  Check(view.Task==task && view.Text==task.Note,"初始化期间切换任务不会显示过期编辑器");
  var bitmap=BitmapSource.Create(160,100,96,96,PixelFormats.Bgra32,null,Enumerable.Repeat((byte)180,160*100*4).ToArray(),160*4);
  var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var bytes=new MemoryStream();encoder.Save(bytes);
  var attachment=await repo.Attachments.ImportAsync(bytes.ToArray());var illustrated=repo.AddTask(DateTime.Today,"图文笔记");
  illustrated.Note=$"# 图文笔记\n\n![插图]({attachment.Reference})\n\n正文继续书写。";repo.SaveTask(illustrated);
  view.RetainDocumentForReading=false;view.Task=illustrated;view.SetMode(NoteViewMode.Preview);Layout(view);
  await view.OpenDocumentAsync();host=Host(view);
  Check(await Eval<bool>(host,"(()=>{const i=document.querySelector('.vditor-reset img');return i.complete && i.naturalWidth===160})()"),"主窗口交接前已准备可见图片");
  if(args.Length>0){using var file=File.Create(Path.Combine(args[0],"main-editing.png"));await host.Browser!.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);}
  await view.FinishDocumentAsync();Layout(view);await Task.Delay(100);
  var imageView=Picture(view.PreviewDocument!)!;
  Check(imageView.TransformToAncestor(preview).Transform(new Point()).Y>=view.PreviewDocument!.Blocks.FirstBlock.ContentEnd.GetCharacterRect(System.Windows.Documents.LogicalDirection.Backward).Bottom,"原生图片占据完整行高，不遮挡前一段标题");
  Check(view.PreviewDocument is not null && view.Text==illustrated.Note,"含图片的笔记安全返回原生阅读");
  if(args.Length>0){var render=new RenderTargetBitmap(800,500,96,96,PixelFormats.Pbgra32);render.Render(view);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(render));using var file=File.Create(Path.Combine(args[0],"main-reading.png"));png.Save(file);}
  view.DisplayOnly=true;view.SetMode(NoteViewMode.Edit);await view.OpenDocumentAsync();
  Check(view.Mode==NoteViewMode.Preview && !view.IsDocumentReady && ((StackPanel)view.FindName("ModeButtons")).Visibility==Visibility.Collapsed,"主页仅展示，不能切换编辑或启动浏览器");
  if(args.Length>0){Layout(view);var render=new RenderTargetBitmap(800,500,96,96,PixelFormats.Pbgra32);render.Render(view);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(render));using var file=File.Create(Path.Combine(args[0],"display-only.png"));png.Save(file);}
  view.DisplayOnly=false;view.WritingOnly=true;view.Task=task;Layout(view);await view.OpenDocumentAsync();host=Host(view);browser=host.Browser!;
  Check(await Eval<bool>(host,"document.getElementById('finish').hidden && document.getElementById('source').hidden && !island.readOnly"),"专用书写便签没有源码和完成按钮，保持可编辑");
  if(args.Length>0){view.Width=360;Layout(view);await Task.Delay(150);using var file=File.Create(Path.Combine(args[0],"writing-only.png"));await browser.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);}
  await browser.ExecuteScriptAsync("island.editor.focus()");await browser.CallDevToolsProtocolMethodAsync("Input.insertText",JsonSerializer.Serialize(new{text=" 失焦补存标记"}));
  await browser.ExecuteScriptAsync("window.dispatchEvent(new Event('blur'))");
  await Until(()=>Task.FromResult(repo.LoadTasks(DateTime.Today).Single(t=>t.Id==task.Id).Note.Contains("失焦补存标记")));
  Check(await Eval<bool>(host,"!island.readOnly && island.editor.vditor.wysiwyg.element.contentEditable==='true'"),"失焦后保存到数据库且不切为只读");
  await Key(host,"s",83);await Task.Delay(100);
  Check(await Eval<bool>(host,"!island.readOnly"),"Ctrl+S 仍可补存但不再结束编辑");
  await browser.ExecuteScriptAsync("island.editor.vditor.wysiwyg.element.dispatchEvent(new CompositionEvent('compositionstart',{bubbles:true}))");
  await browser.CallDevToolsProtocolMethodAsync("Input.insertText",JsonSerializer.Serialize(new{text=" 中文组词补存"}));await browser.ExecuteScriptAsync("window.dispatchEvent(new Event('blur'))");
  Check(!repo.LoadTasks(DateTime.Today).Single(t=>t.Id==task.Id).Note.Contains("中文组词补存"),"失焦时不提交输入法半成品");
  await browser.ExecuteScriptAsync("island.editor.vditor.wysiwyg.element.dispatchEvent(new CompositionEvent('compositionend',{bubbles:true}))");
  await Until(()=>Task.FromResult(repo.LoadTasks(DateTime.Today).Single(t=>t.Id==task.Id).Note.Contains("中文组词补存")));
  Check(await Eval<bool>(host,"!island.readOnly"),"组词确认后完成待执行的失焦保存");
  host.Repository=null;await browser.CallDevToolsProtocolMethodAsync("Input.insertText",JsonSerializer.Serialize(new{text=" 失焦保存失败"}));await browser.ExecuteScriptAsync("window.dispatchEvent(new Event('blur'))");
  await Until(()=>Eval<bool>(host,"document.getElementById('state').textContent.includes('保存失败')"));
  Check(await Eval<bool>(host,"!island.readOnly && island.editor.getValue().includes('失焦保存失败')"),"失焦保存失败时保留输入且仍可编辑");
  host.Repository=repo;await browser.ExecuteScriptAsync("window.dispatchEvent(new Event('blur'))");await host.FlushAsync();
  await view.FinishDocumentAsync();
  var directTask=repo.AddTask(DateTime.Today,"直接书写");
  var direct=new MarkdownNoteView{WritingOnly=true,DocumentRepository=repo,Task=directTask};
  surface.RootVisual=direct;Layout(direct);
  await Until(()=>Task.FromResult(direct.IsDocumentReady && ((Grid)direct.FindName("NativePanel")).Visibility==Visibility.Collapsed));
  var directHost=Host(direct);
  await Until(()=>Eval<bool>(directHost,"document.activeElement===island.editor.vditor.wysiwyg.element && getSelection().rangeCount>0"));
  Check(await Eval<bool>(directHost,"!island.readOnly"),"便签加载后自动进入编辑并建立正文光标，无需点击");
  await directHost.Browser!.CallDevToolsProtocolMethodAsync("Input.insertText",JsonSerializer.Serialize(new{text="打开后直接输入"}));
  await direct.FlushDocumentAsync();
  Check(repo.LoadTasks(DateTime.Today).Single(t=>t.Id==directTask.Id).Note.Contains("打开后直接输入"),"未点击或手动聚焦即可输入并保存");
  await direct.FinishDocumentAsync();
 }
}
