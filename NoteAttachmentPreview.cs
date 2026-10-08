using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MdXaml.Plugins;
using Image = System.Windows.Controls.Image;
using Color = System.Windows.Media.Color;

namespace Cdiary;

// Tokens let the Markdown parser retain its own code/fence rules, without opening remote image URLs.
internal sealed class NoteAttachmentPreview : IInlineParser
{
    private readonly NoteAttachmentStore? _store;
    private readonly FlowDocumentScrollViewer _viewer;
    private readonly List<(string Original,string Reference,string Alt)> _images=new();
    private readonly string _prefix="\uE000island"+Guid.NewGuid().ToString("N");
    public Regex FirstMatchPattern {get;}
    public NoteAttachmentPreview(NoteAttachmentStore? store,FlowDocumentScrollViewer viewer)
    { _store=store;_viewer=viewer;FirstMatchPattern=new Regex(Regex.Escape(_prefix)+@"(\d+)\uE001"); }
    public string Prepare(string markdown) => Regex.Replace(markdown,@"!\[(?<alt>(?:\\.|[^\]\\])*)\]\((?<url>[^\s)]+)(?:\s+""[^""]*"")?\)", match=>
    {
        _images.Add((match.Value,match.Groups["url"].Value,match.Groups["alt"].Value));
        return _prefix+(_images.Count-1)+"\uE001";
    });
    public IEnumerable<Inline> Parse(string text,Match match,IMarkdown engine,out int parseTextBegin,out int parseTextEnd)
    {
        parseTextBegin=match.Index;parseTextEnd=match.Index+match.Length;
        var item=_images[int.Parse(match.Groups[1].Value)];
        return new Inline[]{new InlineUIContainer(new NotePreviewImage(_store,item.Reference,item.Alt,_viewer))};
    }
    public void RestoreCodeLiterals(DependencyObject node)
    {
        if(node is Run run) run.Text=FirstMatchPattern.Replace(run.Text,m=>_images[int.Parse(m.Groups[1].Value)].Original);
        foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().ToArray()) RestoreCodeLiterals(child);
    }
}

public sealed class NotePreviewImage : Border
{
    private readonly NoteAttachmentStore? _store;
    private readonly FlowDocumentScrollViewer _viewer;
    private readonly string _reference;
    private bool _loading;
    private Task? _loadTask;
    public Task? PendingLoad => _loading ? _loadTask : null;
    private readonly Image _image=new(){Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly};
    public NotePreviewImage(NoteAttachmentStore? store,string reference,string alt,FlowDocumentScrollViewer viewer)
    {
        _store=store;_reference=reference;_viewer=viewer;
        CornerRadius=new CornerRadius(9);Margin=new Thickness(0,5,0,9);Padding=new Thickness(3);
        Background=new SolidColorBrush(Color.FromRgb(240,232,216));
        Child=new TextBlock{Text=string.IsNullOrEmpty(alt)?"图片":alt,Margin=new Thickness(8),TextWrapping=TextWrapping.Wrap};
        ToolTip="双击查看原图";
        Loaded+=(_,_)=>{Resize();_viewer.SizeChanged+=ViewerSizeChanged;LayoutUpdated+=CheckViewport;CheckViewport(null,EventArgs.Empty);};
        Unloaded+=(_,_)=>{LayoutUpdated-=CheckViewport;_viewer.SizeChanged-=ViewerSizeChanged;};
        SizeChanged+=(_,_)=>Resize();
        MouseLeftButtonDown+=async(_,e)=>{e.Handled=true;if(e.ClickCount==2 && _store is not null)await NoteAttachmentViewer.OpenAsync(_store,_reference,Window.GetWindow(this));};
        MouseLeftButtonUp+=(_,e)=>e.Handled=true;
    }
    private void Resize(){MaxWidth=Math.Max(40,_viewer.ActualWidth-48);_image.MaxWidth=MaxWidth-6;}
    private void ViewerSizeChanged(object sender,SizeChangedEventArgs args)=>Resize();
    private void CheckViewport(object? sender,EventArgs args)
    {
        Resize();if(_loading || _image.Source is not null){LayoutUpdated-=CheckViewport;return;}
        if(!IsVisible || _viewer.ActualHeight<=0)return;
        try {var rect=TransformToAncestor(_viewer).TransformBounds(new Rect(RenderSize));if(rect.Bottom < -200 || rect.Top > _viewer.ActualHeight+200)return;}
        catch(InvalidOperationException){return;}
        _=LoadPreviewAsync();
    }
    public Task LoadPreviewAsync() => _loadTask ??= LoadPreviewCoreAsync();
    private async Task LoadPreviewCoreAsync()
    {
        if(_loading)return;_loading=true;LayoutUpdated-=CheckViewport;
        try
        {
            var bitmap=_store is null ? null : await Task.Run(()=>_store.ReadPreview(_reference));
            if(bitmap is null) Child=new TextBlock{Text="图片不可用，请重新插入本地图片。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(8)};
            else {_image.Source=bitmap;Child=_image;Resize();}
        }
        catch(Exception){Child=new TextBlock{Text="图片读取失败，可重新插入。",Margin=new Thickness(8)};}
        finally{_loading=false;}
    }
}

internal static class NoteAttachmentViewer
{
    public static async Task OpenAsync(NoteAttachmentStore store,string reference,Window? owner)
    {
        if(owner is null)return;
        try
        {
            var path=store.Resolve(reference,true)??throw new IOException("原图不存在，请重新插入图片。");
            var bitmap=await Task.Run(()=>ScreenshotService.Decode(File.ReadAllBytes(path)));
            new ScreenshotViewer("笔记图片",bitmap,owner,path).Show();
        }
        catch(Exception error){System.Windows.MessageBox.Show(owner,error.Message,"无法打开图片");}
    }
}
