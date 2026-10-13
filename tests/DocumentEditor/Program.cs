using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Cdiary;
using Microsoft.Web.WebView2.Core;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); _checks++; Console.WriteLine("通过：" + message); }
    private static async Task<T> Eval<T>(DocumentNoteHost host, string script)
        => JsonSerializer.Deserialize<T>(await host.Browser!.ExecuteScriptAsync(script))!;
    private static async Task Until(Func<Task<bool>> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("等待编辑器状态超时"); await Task.Delay(40); }
    }
    private static async Task Insert(DocumentNoteHost host, string text)
        => await host.Browser!.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text }));
    private static async Task ClickOutline(DocumentNoteHost host)
    {
        var point=await Eval<double[]>(host,"(()=>{const r=document.querySelector('button[data-type=note-outline]').getBoundingClientRect();return [r.x+r.width/2,r.y+r.height/2]})()");
        foreach(var type in new[]{"mousePressed","mouseReleased"})
            await host.Browser!.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{type,x=point[0],y=point[1],button="left",clickCount=1}));
    }
    private static async Task Key(DocumentNoteHost host, string key, string code, int windowsVirtualKeyCode, string? text = null, int modifiers = 0)
    {
        await host.Browser!.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type="keyDown",key,code,windowsVirtualKeyCode,text = text ?? "",modifiers }));
        await host.Browser.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type="keyUp",key,code,windowsVirtualKeyCode,modifiers }));
    }
    [STAThread]
    private static void Main(string[] args)
    {
        Exception? failure = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try { await Run(args); } catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }));
        Dispatcher.Run();
        if (failure is not null) throw new Exception("文档编辑器验证失败", failure);
    }
    private static async Task Run(string[] args)
    {
        var repository = new DiaryRepository(Path.Combine(Path.GetTempPath(), "CdiaryDocumentTests", Guid.NewGuid().ToString("N"), "diary.db"));
        var task = repository.AddTask(DateTime.Today, "复习笔记");
        task.Note = "# 今日学习\r\n\r\n**Redis** 缓存复习\r\n\r\n- [ ] 阅读资料\r\n- [x] 整理笔记\r\n\r\n## 常用命令\r\n\r\n```sql\r\nSELECT * FROM tasks;\r\n```\r\n\r\n| 项目 | 进度 |\r\n| --- | --- |\r\n| 索引 | 已复习 |\r\n";
        repository.SaveTask(task);
        var minimal = Environment.GetEnvironmentVariable("CDIARY_TEST_MINIMAL") == "1";
        using var surface = new HwndSource(new HwndSourceParameters("Document editor offscreen verification")
            { Width = 800, Height = 600, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)(minimal ? 0x90000000 : 0x80000000)), ExtendedWindowStyle = 0x08000080 });
        using var host = new DocumentNoteHost(minimal) { Repository = repository };
        if (minimal) host.Ready += () => host.Browser!.PostWebMessageAsJson("{\"kind\":\"minimalSticky\"}");
        host.Notice += Console.WriteLine;
        surface.RootVisual = host;
        Layout(host, 800, 600); host.SetTask(task);
        var startup = Stopwatch.StartNew(); await host.EnsureReadyAsync();
        Console.WriteLine($"编辑器初始化：{startup.ElapsedMilliseconds} ms");
        await Task.Delay(500);
        var original = task.Note;
        if (minimal) Check(await Eval<bool>(host,"document.documentElement.classList.contains('minimal-sticky') && getComputedStyle(document.body).backgroundColor==='rgba(0, 0, 0, 0)' && getComputedStyle(document.querySelector('.vditor-reset')).color==='rgb(48, 58, 61)'"), "透明合成编辑器背景透明且正文不透明");
        Check(await Eval<bool>(host, "!!document.querySelector('.vditor-reset h1') && !!document.querySelector('.vditor-reset table') && document.querySelectorAll('.vditor-reset input[type=checkbox]').length===2"), "标题、表格和待办原生可编辑");
        Check(task.Note == original && repository.ReadNoteBackup(task.Id) is null, "仅打开旧笔记不会格式化原文或建立多余备份");
        if (args.Length > 0)
        {
            var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            using var stream = File.Create(Path.Combine(output, "document-wide.png"));
            await host.Browser!.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        }
        await host.Browser!.ExecuteScriptAsync("(()=>{let p=document.querySelector('.vditor-reset p');let r=document.createRange();r.selectNodeContents(p);r.collapse(false);let s=getSelection();s.removeAllRanges();s.addRange(r);p.closest('[contenteditable]').focus()})()");
        await Insert(host, "，中文输入测试 🌿");
        await Until(() => Task.FromResult(task.Note.Contains("中文输入测试")));
        await host.FlushAsync();
        Check(repository.LoadTasks(DateTime.Today).Single().Note == task.Note, "真实浏览器输入同步模型并写入 SQLite");
        Check(repository.ReadNoteBackup(task.Id) == original, "首次编辑保留原 Markdown 备份");
        await Key(host,"ArrowLeft","ArrowLeft",37);
        await Until(() => Eval<bool>(host, "!!island.editor.vditor.wysiwyg.popover.querySelector('[data-type=remove]')"));
        Check(await Eval<bool>(host, "getComputedStyle(island.editor.vditor.wysiwyg.popover).display==='none'"), "正文输入时不显示遮挡文字的段落操作浮层");
        await host.Browser.ExecuteScriptAsync("(()=>{const p=document.querySelector('.vditor-reset table th');const r=document.createRange();r.selectNodeContents(p);r.collapse(true);getSelection().removeAllRanges();getSelection().addRange(r);p.dispatchEvent(new MouseEvent('click',{bubbles:true}))})()");
        await Key(host,"ArrowRight","ArrowRight",39);
        await Until(() => Eval<bool>(host, "!!island.editor.vditor.wysiwyg.popover.querySelector('input')"));
        Check(await Eval<bool>(host, "(()=>{const p=island.editor.vditor.wysiwyg.popover;return getComputedStyle(p).display!=='none' && [...p.querySelectorAll('[data-type=up],[data-type=down],[data-type=remove]')].every(b=>getComputedStyle(b).display==='none')})()"), "表格编辑控件保留，上移下移删除按钮隐藏");
        await host.Browser.ExecuteScriptAsync("document.querySelector('.vditor-reset input[type=checkbox]').click()");
        await Until(() => Task.FromResult(task.Note.Contains("- [X] 阅读资料")));
        Check(!task.IsDone, "笔记复选框可交互且不改变外层任务状态");
        await host.Browser.ExecuteScriptAsync("(()=>{let p=document.querySelector('.vditor-reset p');let r=document.createRange();r.selectNodeContents(p);let s=getSelection();s.removeAllRanges();s.addRange(r);document.dispatchEvent(new Event('selectionchange'))})()");
        Check(await Eval<bool>(host, "!document.querySelector('#selection-tools').hidden"), "选中文字出现浮动工具条");
        if (args.Length>0)
        {
            using var floating=File.Create(Path.Combine(Path.GetFullPath(args[0]),"document-selection.png"));
            await host.Browser.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,floating);
        }
        await host.Browser.ExecuteScriptAsync("document.querySelector('#selection-tools [data-command=italic]').click()");
        await Until(() => Task.FromResult(task.Note.Contains("*，中文输入测试") || task.Note.Contains("*Redis") || task.Note.Contains("***Redis")));
        Check(await Eval<bool>(host, "!!document.querySelector('.vditor-reset em,.vditor-reset i')"), "浮动格式操作保留选区并生效");
        var next = repository.AddTask(DateTime.Today, "空白笔记");
        host.SetTask(next);
        await Until(() => Eval<bool>(host, $"island.session?.taskId==={next.Id}"));
        await host.Browser.ExecuteScriptAsync("island.editor.focus()");
        await Insert(host, "/");
        await Until(() => Eval<bool>(host, "!document.querySelector('#slash-menu').hidden"));
        Check(await Eval<bool>(host, "document.querySelectorAll('#slash-menu button').length===11"), "空段落斜杠打开插入菜单（含图片）");
        await host.Browser.ExecuteScriptAsync("document.querySelectorAll('#slash-menu button')[3].click()");
        await Until(() => Eval<bool>(host, "!!document.querySelector('.vditor-reset input[type=checkbox]')"));
        Check(next.Note.Contains("[ ]"), "斜杠菜单插入实际待办块");
        await Task.Delay(300);
        await host.Browser.ExecuteScriptAsync("document.querySelector('.vditor-toolbar button[data-type=undo]').click()");
        await Until(()=>Eval<bool>(host,"!document.querySelector('.vditor-reset input[type=checkbox]')"));
        Check(!next.Note.Contains("[ ]"),"文档编辑支持撤销插入");
        var headingTask=repository.AddTask(DateTime.Today,"快捷输入");host.SetTask(headingTask);
        await Until(()=>Eval<bool>(host,$"island.session.taskId==={headingTask.Id}"));
        await host.Browser.ExecuteScriptAsync("island.editor.focus()");await Insert(host,"##");await Key(host," ","Space",32," ");await Insert(host,"直接变成标题");
        await Until(()=>Eval<bool>(host,"!!document.querySelector('.vditor-reset h2')"));
        Check(headingTask.Note.Contains("## 直接变成标题"),"Markdown 快捷输入直接转为标题");
        await Key(host,"Enter","Enter",13,"\r");await Insert(host,"- ");await Insert(host,"清单第一项");await Key(host,"Enter","Enter",13,"\r");await Insert(host,"清单第二项");
        await Until(()=>Eval<bool>(host,"document.querySelectorAll('.vditor-reset li').length===2"));
        Check(headingTask.Note.Contains("清单第二项"),"列表回车自动续写下一项");
        await host.Browser.ExecuteScriptAsync("document.querySelector('[contenteditable=true]').dispatchEvent(new CompositionEvent('compositionstart',{bubbles:true}))");
        var beforeComposition=headingTask.Note;await Insert(host,"输入中");
        Check(headingTask.Note==beforeComposition,"中文组词期间不提交半成品");
        var preventedClose=false;
        try {await host.FlushAsync();}catch(InvalidOperationException){preventedClose=true;}
        Check(preventedClose,"尚在组词时阻止结束编辑");
        await host.Browser.ExecuteScriptAsync("document.querySelector('[contenteditable=true]').dispatchEvent(new CompositionEvent('compositionend',{bubbles:true}))");
        await Until(()=>Task.FromResult(headingTask.Note.Contains("输入中")));
        Check(headingTask.Note.Contains("输入中"),"组词结束后提交完整内容");
        host.SetTask(next);await Until(()=>Eval<bool>(host,$"island.session.taskId==={next.Id}"));
        await host.FlushAsync();
        Check(repository.LoadTasks(DateTime.Today).Single(item=>item.Id==task.Id).Note.Contains("中文输入测试"), "切换任务后原任务内容保留");
        var oldSession = await Eval<JsonElement>(host, "island.session");
        next.Note = "另一窗口的新内容";
        await Until(() => Eval<bool>(host, "island.session.text==='另一窗口的新内容'"));
        var stale = new {kind="change",token=oldSession.GetProperty("token").GetString(),taskId=next.Id,
            revision=oldSession.GetProperty("revision").GetInt64(),baseText=oldSession.GetProperty("text").GetString(),markdown="迟到的旧输入"};
        await host.Browser.ExecuteScriptAsync("chrome.webview.postMessage(" + JsonSerializer.Serialize(stale) + ")");
        await Until(() => Task.FromResult(repository.ReadNoteBackup(next.Id,true) is not null));
        Check(next.Note=="另一窗口的新内容" && repository.ReadNoteBackup(next.Id,true)=="迟到的旧输入", "冲突输入保留找回副本，不覆盖新内容");
        var shortcuts = repository.AddTask(DateTime.Today,"快捷键验证");
        shortcuts.Note="快捷键正文"; repository.SaveTask(shortcuts); host.SetTask(shortcuts);
        await Until(()=>Eval<bool>(host,$"island.session?.taskId==={shortcuts.Id}"));
        await host.Browser.ExecuteScriptAsync("(()=>{let p=document.querySelector('.vditor-reset p');p.closest('[contenteditable]').focus();let r=document.createRange();r.selectNodeContents(p);let s=getSelection();s.removeAllRanges();s.addRange(r)})()");
        await Key(host,"b","KeyB",66,modifiers:2);
        await Until(()=>Task.FromResult(shortcuts.Note.Contains("**快捷键正文**")));
        Check(await Eval<bool>(host,"!!document.querySelector('.vditor-reset strong,.vditor-reset b')"),"Ctrl+B 将选中文字加粗并同步保存");
        await Key(host,"b","KeyB",66,modifiers:2);
        await Until(()=>Task.FromResult(!shortcuts.Note.Contains("**")));
        Check(shortcuts.Note.Contains("快捷键正文"),"再次 Ctrl+B 取消加粗且保留文字");
        for(var level=1;level<=6;level++)
        {
            await Key(host,level.ToString(),"Digit"+level,48+level,modifiers:2);
            await Until(()=>Eval<bool>(host,$"!!document.querySelector('.vditor-reset h{level}')"));
            Check(shortcuts.Note.StartsWith(new string('#',level)+" 快捷键正文"),$"Ctrl+{level} 转为 {level} 级标题且保留文字");
        }
        await Task.Delay(300);
        await Key(host,"0","Digit0",48,modifiers:2);
        await Until(()=>Eval<bool>(host,"!!document.querySelector('.vditor-reset p') && !document.querySelector('.vditor-reset h6')"));
        Check(shortcuts.Note.Trim()=="快捷键正文","Ctrl+0 恢复正文");
        await Task.Delay(300);
        await Key(host,"z","KeyZ",90,modifiers:2);
        await Until(()=>Eval<bool>(host,"!!document.querySelector('.vditor-reset h6')"));
        Check(shortcuts.Note.StartsWith("###### "),"标题快捷键操作可通过 Ctrl+Z 撤销");
        await host.FlushAsync();
        Check(repository.LoadTasks(DateTime.Today).Single(item=>item.Id==shortcuts.Id).Note==shortcuts.Note,"快捷键编辑后的 Markdown 写入数据库");
        host.SetTask(task);
        await Until(() => Eval<bool>(host,$"island.session.taskId==={task.Id}"));
        Layout(host,234,200);
        await Task.Delay(150);
        Check(await Eval<bool>(host,"document.documentElement.scrollWidth<=innerWidth && document.querySelector('.vditor-toolbar').scrollWidth<=innerWidth"), "窄便签工具栏不产生横向溢出");
        if(args.Length>0)
        {
            using var small=File.Create(Path.Combine(Path.GetFullPath(args[0]),"document-small.png"));
            await host.Browser.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,small);
        }
        var processIds=host.Browser.Environment.GetProcessInfos().Select(info=>(int)info.ProcessId).Append(Environment.ProcessId).Distinct().ToArray();
        double SumMemory()=>processIds.Sum(id=>{try{return Process.GetProcessById(id).PrivateMemorySize64/1024d/1024;}catch{return 0;}});
        double SumCpu()=>processIds.Sum(id=>{try{return Process.GetProcessById(id).TotalProcessorTime.TotalMilliseconds;}catch{return 0;}});
        await Task.Delay(500); var cpu=SumCpu(); var wait=Stopwatch.StartNew(); await Task.Delay(1500);
        Console.WriteLine($"宿主＋WebView2：{processIds.Length} 个进程，Private Bytes {SumMemory():F1} MB；空闲 {wait.ElapsedMilliseconds} ms 累计 CPU {SumCpu()-cpu:F1} ms。");
        host.Visibility=Visibility.Collapsed; await Task.Delay(700);
        Check(host.Browser.IsSuspended,"隐藏编辑器后暂停 WebView2");
        cpu=SumCpu(); await Task.Delay(1500);
        Console.WriteLine($"隐藏暂停后累计 CPU：{SumCpu()-cpu:F1} ms / 1500 ms。");
        host.Visibility=Visibility.Visible; await Task.Delay(200);
        Check(!host.Browser.IsSuspended,"重新打开编辑器自动恢复");
        using var secondSurface=new HwndSource(new HwndSourceParameters("Second offscreen note"){Width=340,Height=400,PositionX=-32000,PositionY=-32000,WindowStyle=unchecked((int)0x80000000)});
        using var secondHost=new DocumentNoteHost{Repository=repository}; secondSurface.RootVisual=secondHost; Layout(secondHost,340,400);secondHost.SetTask(task);await secondHost.EnsureReadyAsync();
        await Until(()=>Eval<bool>(secondHost,$"island.session?.taskId==={task.Id}"));
        await secondHost.Browser!.ExecuteScriptAsync("(()=>{let p=document.querySelector('.vditor-reset p');let r=document.createRange();r.selectNodeContents(p);r.collapse(false);let s=getSelection();s.removeAllRanges();s.addRange(r);p.closest('[contenteditable]').focus()})()");
        await Insert(secondHost," 同步到主窗口");
        await Until(()=>Eval<bool>(host,"island.editor.getValue().includes('同步到主窗口')"));
        Check(task.Note.Contains("同步到主窗口"),"两个实际 WebView 编辑器同步同一任务");
        await secondHost.FlushAsync();
        processIds=host.Browser.Environment.GetProcessInfos().Select(info=>(int)info.ProcessId).Append(Environment.ProcessId).Distinct().ToArray();
        Console.WriteLine($"双编辑器：{processIds.Length} 个进程，Private Bytes {SumMemory():F1} MB。");
        using var wrapperSurface=new HwndSource(new HwndSourceParameters("Shared note control verification"){Width=360,Height=400,PositionX=-32000,PositionY=-32000,WindowStyle=unchecked((int)0x80000000)});
        var wrapper=new MarkdownNoteView{Task=next,DocumentRepository=repository};wrapperSurface.RootVisual=wrapper;Layout(wrapper,360,400);wrapper.SetMode(NoteViewMode.Preview);
        Check(!wrapper.IsDocumentReady,"轻量预览不初始化 WebView2");
        await wrapper.OpenDocumentAsync();Check(wrapper.IsDocumentReady,"手动进入文档时才初始化编辑器");
        var wrapperHost=(DocumentNoteHost)((System.Windows.Controls.Border)wrapper.FindName("DocumentContainer")).Child;
        await Until(()=>Eval<bool>(wrapperHost,$"island.session?.taskId==={next.Id}"));
        var renders=wrapper.RenderCount;
        await wrapperHost.Browser!.ExecuteScriptAsync("island.editor.focus()");await Insert(wrapperHost,"结束前最后输入");
        await wrapper.FinishDocumentAsync();Check(!wrapper.IsDocumentReady && wrapper.Mode==NoteViewMode.Preview,"完成后回到原生预览并释放编辑器");
        Check(next.Note.Contains("结束前最后输入") && wrapper.Text==next.Note && repository.LoadTasks(DateTime.Today).Single(item=>item.Id==next.Id).Note==next.Note,"结束编辑前保存最后输入并更新原生预览");
        Check(wrapper.RenderCount<=renders+1,"文档编辑时不重复渲染隐藏的原生预览");
        await wrapper.OpenDocumentAsync();wrapper.Task=task;
        await Until(()=>Task.FromResult(!wrapper.IsDocumentReady));
        Check(wrapper.Task==task && wrapper.Text==task.Note,"切换任务结束旧编辑会话，不显示旧任务正文");
        var imageTask=repository.AddTask(DateTime.Today,"文档插图");
        imageTask.Note="# 学习截图\n\n图片跟随笔记永久保存。\n\n";repository.SaveTask(imageTask);host.SetTask(imageTask);Layout(host,800,600);
        await Until(()=>Eval<bool>(host,$"island.session?.taskId==={imageTask.Id}"));
        var pixels=new byte[600*240*4];for(var i=0;i<pixels.Length;i+=4){pixels[i]=170;pixels[i+1]=200;pixels[i+2]=30;pixels[i+3]=255;}
        var bitmap=System.Windows.Media.Imaging.BitmapSource.Create(600,240,96,96,System.Windows.Media.PixelFormats.Bgra32,null,pixels,600*4);
        var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var pngStream=new MemoryStream();png.Save(pngStream);var image64=Convert.ToBase64String(pngStream.ToArray());
        var pasteScript="(()=>{island.editor.focus();const root=island.editor.vditor.wysiwyg.element;const r=document.createRange();r.selectNodeContents(root);r.collapse(false);getSelection().removeAllRanges();getSelection().addRange(r);const d=new DataTransfer();d.items.add(new File([Uint8Array.from(atob("+JsonSerializer.Serialize(image64)+"),x=>x.charCodeAt(0))],'test.png',{type:'image/png'}));root.dispatchEvent(new ClipboardEvent('paste',{bubbles:true,cancelable:true,clipboardData:d}));})()";
        await host.Browser!.ExecuteScriptAsync(pasteScript);
        await Until(()=>Task.FromResult(imageTask.Note.Contains("attachments/")));
        Check(!imageTask.Note.Contains("data:") && !imageTask.Note.Contains("island-editor.local"),"图片粘贴保存为相对路径，无 Base64 正文");
        await Until(()=>Eval<bool>(host,"(()=>{const i=document.querySelector('.vditor-reset img');return !!i && i.complete && i.naturalWidth>0})()"));
        Check(await Eval<bool>(host,"document.querySelector('.vditor-reset img').naturalWidth===600"),"文档图片通过本地资源响应显示");
        await host.FlushAsync();
        Check(repository.LoadTasks(DateTime.Today).Single(t=>t.Id==imageTask.Id).Note==imageTask.Note,"图片引用自动保存到 SQLite");
        var reference=await Eval<string>(host,"document.querySelector('.vditor-reset img').getAttribute('src')");
        Check(repository.Attachments.Resolve(reference,true) is not null,"文档粘贴已保存独立原图");
        if(args.Length>0){using var picture=File.Create(Path.Combine(Path.GetFullPath(args[0]),"document-image.png"));await host.Browser.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,picture);}
        await Task.Delay(350);await Key(host,"z","KeyZ",90,modifiers:2);
        await Until(()=>Task.FromResult(!imageTask.Note.Contains("attachments/")));
        Check(repository.Attachments.CleanUnused()==0,"撤销图片插入不会清理原图");
        await Key(host,"y","KeyY",89,modifiers:2);
        await Until(()=>Task.FromResult(imageTask.Note.Contains("attachments/")));
        Check(repository.Attachments.Resolve(reference,true) is not null,"重做图片插入仍可读取原图");
        secondHost.SetTask(imageTask);await Until(()=>Eval<bool>(secondHost,$"island.session?.taskId==={imageTask.Id}"));
        await Until(()=>Eval<bool>(secondHost,"!!document.querySelector('.vditor-reset img')"));
        Check(await Eval<bool>(secondHost,"island.editor.getValue().includes('attachments/')"),"另一便签同步图片引用");
        await host.Browser.ExecuteScriptAsync(pasteScript);host.SetTask(next);
        await Until(()=>Eval<bool>(host,$"island.session?.taskId==={next.Id}"));await Task.Delay(300);
        Check(!next.Note.Contains("attachments/"),"图片导入期间切换任务不会插错笔记");
        var outlineTask=repository.AddTask(DateTime.Today,"目录检查");
        var paragraphs=string.Join("\n\n",Enumerable.Repeat("章节正文，检查滚动定位与当前章节。",20));
        outlineTask.Note=$"# 总览\n\n## 重复章节\n\n{paragraphs}\n\n## 重复章节\n\n{paragraphs}\n\n###### 六级标题\n\n{paragraphs}\n\n```md\n# 不是标题\n```\n";
        repository.SaveTask(outlineTask);wrapper.Task=outlineTask;Layout(wrapper,900,600);wrapper.SetOutlineOpen(true);await wrapper.OpenDocumentAsync();
        var outlineHost=(DocumentNoteHost)((System.Windows.Controls.Border)wrapper.FindName("DocumentContainer")).Child;
        await Until(()=>Task.FromResult(wrapper.OutlineItems.Count==4));
        Check(wrapper.IsOutlineDocked && wrapper.OutlineItems.Last().Level==6,"文档目录提取实际六级标题并排除代码块");
        var duplicates=wrapper.OutlineItems.Where(h=>h.Text=="重复章节").ToArray();
        Check(duplicates.Length==2 && duplicates[0].Id!=duplicates[1].Id,"文档同名标题使用独立节点身份");
        var textBeforeJump=outlineTask.Note;
        var selectionBeforeJump=await Eval<string>(outlineHost,"getSelection().toString()");
        await wrapper.NavigateOutlineAsync(duplicates[1].Id);
        await Until(()=>Task.FromResult(wrapper.ActiveOutlineId==duplicates[1].Id));
        Check(await Eval<bool>(outlineHost,"document.querySelectorAll('.vditor-reset h2')[1].getBoundingClientRect().top<100"),"文档目录跳转定位到第二个同名标题");
        Check(outlineTask.Note==textBeforeJump && await Eval<string>(outlineHost,"getSelection().toString()") == selectionBeforeJump,"目录跳转不修改笔记或编辑选区");
        await outlineHost.Browser!.ExecuteScriptAsync("island.editor.vditor.wysiwyg.element.scrollTop=0");
        await Until(()=>Task.FromResult(wrapper.ActiveOutlineId==wrapper.OutlineItems[0].Id));
        Check(wrapper.ActiveOutlineId==wrapper.OutlineItems[0].Id,"手动滚动同步当前章节高亮");
        await outlineHost.Browser.ExecuteScriptAsync("(()=>{let h=document.querySelector('.vditor-reset h1');let r=document.createRange();r.selectNodeContents(h);r.collapse(false);let s=getSelection();s.removeAllRanges();s.addRange(r);h.closest('[contenteditable]').focus()})()");
        await Insert(outlineHost,"已修改");await Until(()=>Task.FromResult(wrapper.OutlineItems[0].Text.Contains("已修改")));
        Check(wrapper.OutlineItems[0].Text.Contains("已修改"),"文档标题输入后自动刷新目录");
        var countBeforeComposition=wrapper.OutlineItems[0].Text;
        await outlineHost.Browser.ExecuteScriptAsync("document.querySelector('[contenteditable=true]').dispatchEvent(new CompositionEvent('compositionstart',{bubbles:true}))");
        await Insert(outlineHost,"组词中");await Task.Delay(380);
        Check(wrapper.OutlineItems[0].Text==countBeforeComposition,"中文组词期间不更新目录半成品");
        await outlineHost.Browser.ExecuteScriptAsync("document.querySelector('[contenteditable=true]').dispatchEvent(new CompositionEvent('compositionend',{bubbles:true}))");
        await Until(()=>Task.FromResult(wrapper.OutlineItems[0].Text.Contains("组词中")));
        Check(wrapper.OutlineItems[0].Text.Contains("组词中"),"组词确认后刷新完整标题");
        Layout(wrapper,340,400);Check(wrapper.IsOutlineOpen && !wrapper.IsOutlineDocked,"文档窄便签切换到浮层布局");
        await wrapper.NavigateOutlineAsync(wrapper.OutlineItems[0].Id);Check(!wrapper.IsOutlineOpen,"文档窄目录跳转后自动收起");
        foreach(var width in new[]{900,340})
        {
            Layout(wrapper,width,400);wrapper.SetOutlineOpen(false);await Task.Delay(100);
            for(var i=0;i<2;i++)
            {
                await ClickOutline(outlineHost);
                var expected=i==0;
                await Until(()=>Task.FromResult(wrapper.IsOutlineOpen==expected));
                await Until(()=>Eval<bool>(outlineHost,$"document.querySelector('button[data-type=note-outline]').getAttribute('aria-expanded')==='{expected.ToString().ToLowerInvariant()}'"));
                Check(wrapper.IsOutlineOpen==expected,$"文档目录 {width} DIP 第 {i+1} 次点击正确切换开关");
            }
            wrapper.SetOutlineOpen(true);await Task.Delay(100);
            await outlineHost.Browser.ExecuteScriptAsync("island.editor.vditor.wysiwyg.element.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true}))");
            await Task.Delay(100);
            Check(wrapper.IsOutlineOpen==(width>=720),$"文档正文点击仅收起窄目录（{width} DIP）");
        }
        wrapper.SetOutlineOpen(true);await Task.Delay(100);await Key(outlineHost,"Escape","Escape",27);
        await Until(()=>Task.FromResult(!wrapper.IsOutlineOpen));Check(!wrapper.IsOutlineOpen,"文档窄目录按 Esc 收起");
        wrapper.SetOutlineOpen(true);wrapper.Task=next;
        Check(wrapper.OutlineItems.Count==0,"切换任务时立即清除文档旧目录");
        await Until(()=>Task.FromResult(!wrapper.IsDocumentReady));wrapper.RefreshOutline();
        Check(wrapper.OutlineItems.All(h=>!h.Text.Contains("重复章节")),"旧会话目录消息不会进入新任务");
        await host.FlushAsync();
        Console.WriteLine($"文档编辑器：{_checks} 项检查通过；窗口始终不可见，未访问正式数据。");
    }
    private static void Layout(FrameworkElement view, double width, double height)
    { view.Measure(new Size(width,height)); view.Arrange(new Rect(0,0,width,height)); view.UpdateLayout(); }
}
