using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Cdiary;

public sealed partial class DocumentNoteHost
{
    private bool _importingImage;
    private async Task ServeLocalResourceAsync(CoreWebView2 core,CoreWebView2WebResourceRequestedEventArgs args,Dictionary<string,string> resources)
    {
        var deferral=args.GetDeferral();
        try
        {
            var uri=args.Request.Uri.Split('?')[0];
            if(!resources.TryGetValue(uri,out var path))
            {args.Response=core.Environment.CreateWebResourceResponse(Stream.Null,404,"Not found","");return;}
            var bytes=await File.ReadAllBytesAsync(path);
            var type=Path.GetExtension(path) switch {".html"=>"text/html; charset=utf-8",".js"=>"application/javascript; charset=utf-8",".css"=>"text/css; charset=utf-8",_=>"application/octet-stream"};
            args.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(bytes,false),200,"OK","Content-Type: "+type+"\r\nCache-Control: no-cache");
        }
        catch {if(!_disposed)args.Response=core.Environment.CreateWebResourceResponse(Stream.Null,404,"Not found","");}
        finally{try{deferral.Complete();}catch(System.Runtime.InteropServices.COMException) when(_disposed){}}
    }
    private async Task ServeImageAsync(CoreWebView2 core,CoreWebView2WebResourceRequestedEventArgs args)
    {
        var deferral=args.GetDeferral();
        try
        {
            var reference=args.Request.Uri[(Origin.Length+1)..];
            var path=Repository?.Attachments.Resolve(reference);
            var bytes=path is null?null:await File.ReadAllBytesAsync(path);
            args.Response=core.Environment.CreateWebResourceResponse(bytes is null ? Stream.Null : new MemoryStream(bytes,false),bytes is null?404:200,bytes is null?"Not found":"OK","Content-Type: image/png\r\nCache-Control: no-cache");
        }
        catch { if(!_disposed)args.Response=core.Environment.CreateWebResourceResponse(Stream.Null,404,"Not found",""); }
        finally { try{deferral.Complete();}catch(System.Runtime.InteropServices.COMException) when(_disposed){} }
    }
    private async void BeginImageImport(JsonElement message)
    {
        var request=message.GetProperty("request").GetString();
        var token=message.GetProperty("token").GetString();
        if(_importingImage || _disposed || Repository is null || token!=_token || _task is null)
        {Send(new{kind="imageResult",request,token,error="编辑位置已变化，请重试。"});return;}
        _importingImage=true;
        var task=_task;var revision=task.NoteRevision;
        try
        {
            NoteAttachment? attachment;
            if(message.TryGetProperty("choose",out var choose) && choose.GetBoolean())
            {
                var path=NoteImageInput.ChooseFile(Window.GetWindow(this));
                if(path is null){Send(new{kind="imageResult",request,token,canceled=true});return;}
                attachment=await Repository.Attachments.ImportFileAsync(path);
            }
            else
            {
                var encoded=message.GetProperty("base64").GetString()??"";
                if(encoded.Length>NoteAttachmentStore.MaxBytes*4L/3+4)throw new IOException("单张图片不能超过 20 MB。");
                attachment=await Repository.Attachments.ImportAsync(Convert.FromBase64String(encoded));
            }
            if(_disposed || _task!=task || _token!=token || task.NoteRevision!=revision)
            {Send(new{kind="imageResult",request,token,error="编辑位置已变化，已取消图片插入，请重试。"});return;}
            Send(new{kind="imageResult",request,token,reference=attachment.Reference});
        }
        catch(Exception error){Send(new{kind="imageResult",request,token,error="插入图片失败："+error.Message});}
        finally{_importingImage=false;}
    }
}
