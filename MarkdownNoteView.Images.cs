using System.Windows;
using System.Windows.Input;
using DataFormats=System.Windows.DataFormats;
using DragEventArgs=System.Windows.DragEventArgs;
using DataObject=System.Windows.DataObject;

namespace Cdiary;

public partial class MarkdownNoteView
{
    private bool _imageImporting;
    private long _imageEpoch;
    private void InitializeImages()
    {
        DataObject.AddPastingHandler(Editor,async(_,args)=>
        {
            if(!NoteImageInput.HasImage(args.DataObject))return;
            args.CancelCommand();
            var data=args.DataObject;
            await InsertImageAsync(()=>NoteImageInput.ReadAsync(data));
        });
        Editor.AllowDrop=true;
        Editor.PreviewDragOver+=(_,args)=>{if(args.Data.GetDataPresent(DataFormats.FileDrop)){args.Effects=System.Windows.DragDropEffects.Copy;args.Handled=true;}};
        Editor.PreviewDrop+=async(_,args)=>
        {
            if(!args.Data.GetDataPresent(DataFormats.FileDrop))return;
            args.Handled=true;
            if(Editor.GetPositionFromPoint(args.GetPosition(Editor)) is { } position) Editor.CaretOffset=Editor.Document.GetOffset(position.Line,position.Column);
            var data=args.Data;await InsertImageAsync(()=>NoteImageInput.ReadAsync(data));
        };
        Unloaded+=(_,_)=>_imageEpoch++;
        IsVisibleChanged+=(_,_)=>{if(!IsVisible)_imageEpoch++;};
    }
    private async void Image_Click(object sender,RoutedEventArgs args)
    {
        var path=NoteImageInput.ChooseFile(Window.GetWindow(this));
        if(path is not null)await InsertImageAsync(async()=>
        {
            if(new System.IO.FileInfo(path).Length>NoteAttachmentStore.MaxBytes)throw new System.IO.IOException("单张图片不能超过 20 MB。");
            return await System.IO.File.ReadAllBytesAsync(path);
        });
    }
    public Task InsertImageBytesAsync(byte[] bytes)=>InsertImageAsync(()=>System.Threading.Tasks.Task.FromResult(bytes));
    public Task InsertImageDataAsync(System.Windows.IDataObject data)=>InsertImageAsync(()=>NoteImageInput.ReadAsync(data));
    private bool TryPasteImage()
    {
        try
        {
            var data=System.Windows.Clipboard.GetDataObject();
            if(data is null || !NoteImageInput.HasImage(data))return false;
            _=InsertImageDataAsync(data);return true;
        }
        catch(Exception error){Status.Text="读取剪贴板失败："+error.Message;Status.Visibility=Visibility.Visible;return true;}
    }
    private async Task InsertImageAsync(Func<Task<byte[]>> read)
    {
        if(_imageImporting || Task is not { } task || DocumentRepository is not { } repository)return;
        _imageImporting=true;
        var revision=task.NoteRevision;var epoch=_imageEpoch;var document=Editor.Document;
        var start=Editor.SelectionStart;var length=Editor.SelectionLength;
        Status.Text="正在保存图片…";Status.Visibility=Visibility.Visible;
        try
        {
            var attachment=await repository.Attachments.ImportAsync(await read());
            if(Task!=task || task.NoteRevision!=revision || epoch!=_imageEpoch || Editor.Document!=document)
            {Status.Text="编辑位置已变化，已取消插入；请在需要的位置重试。";return;}
            var markdown=$"\n![图片]({attachment.Reference})\n";
            using(document.RunUpdate())document.Replace(start,length,markdown);
            Editor.CaretOffset=start+markdown.Length;
            repository.SaveDocumentNote(task);
            Status.Text="图片已保存到笔记附件。";
        }
        catch(Exception error){Status.Text="插入图片失败："+error.Message;}
        finally{_imageImporting=false;}
    }
}
