using System.IO;
using System.Windows.Media.Imaging;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using IDataObject = System.Windows.IDataObject;

namespace Cdiary;

internal static class NoteImageInput
{
    public static bool HasImage(IDataObject data) => data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.FileDrop);
    public static async Task<byte[]> ReadAsync(IDataObject data)
    {
        if(data.GetData(DataFormats.FileDrop) is string[] files)
        {
            if(files.Length!=1) throw new IOException("请一次插入一张 PNG 或 JPEG 图片。");
            if(new FileInfo(files[0]).Length>NoteAttachmentStore.MaxBytes) throw new IOException("单张图片不能超过 20 MB。");
            return await File.ReadAllBytesAsync(files[0]);
        }
        if(data.GetData("PNG") is MemoryStream png)
        {
            if(png.Length>NoteAttachmentStore.MaxBytes) throw new IOException("单张图片不能超过 20 MB。");
            return png.ToArray();
        }
        if(data.GetData(DataFormats.Bitmap) is not BitmapSource bitmap) throw new IOException("剪贴板中没有可用图片。");
        if((long)bitmap.PixelWidth*bitmap.PixelHeight>NoteAttachmentStore.MaxPixels) throw new IOException("图片超过 4000 万像素，请先缩小后插入。");
        bitmap=bitmap.Clone(); bitmap.Freeze();
        return await Task.Run(()=> {var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=new MemoryStream();encoder.Save(stream);return stream.ToArray();});
    }
    public static string? ChooseFile(System.Windows.Window? owner)
    {
        var dialog=new Microsoft.Win32.OpenFileDialog{Filter="图片|*.png;*.jpg;*.jpeg",Multiselect=false,Title="插入笔记图片"};
        return (owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner))==true ? dialog.FileName : null;
    }
}
