using System.Windows;

namespace Cdiary;

public partial class MainWindow
{
    private bool _cleaningAttachments;
    private async void RefreshAttachments_Click(object sender,RoutedEventArgs args)=>await RefreshAttachmentUsageAsync();
    private async Task RefreshAttachmentUsageAsync()
    {
        try
        {
            var usage=await Task.Run(()=>_repository.Attachments.Usage());
            AttachmentUsageText.Text=$"共 {usage.Count} 张，占用 {usage.Bytes/1024d/1024:F1} MB；可清理 {usage.UnusedCount} 张，释放 {usage.UnusedBytes/1024d/1024:F1} MB。";
            CleanAttachmentsButton.IsEnabled=usage.UnusedCount>0 && !_cleaningAttachments;
        }
        catch(Exception error){AttachmentUsageText.Text="读取附件占用失败："+error.Message;}
    }
    private async void CleanAttachments_Click(object sender,RoutedEventArgs args)
    {
        if(_cleaningAttachments)return;
        _cleaningAttachments=true;CleanAttachmentsButton.IsEnabled=false;
        try
        {
            FlushEdits();foreach(var window in _noteWindows.Values)window.FlushEdits();
            var usage=await Task.Run(()=>_repository.Attachments.Usage());
            if(usage.UnusedCount==0)return;
            if(System.Windows.MessageBox.Show(this,$"永久删除 {usage.UnusedCount} 张未使用图片，预计释放 {usage.UnusedBytes/1024d/1024:F1} MB？\n仍被笔记、备份或本次编辑记录使用的图片会保留。","清理笔记图片",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
            var deleted=await Task.Run(()=>_repository.Attachments.CleanUnused());
            SettingsStatus.Text=$"已清理 {deleted} 张未使用的笔记图片。";
        }
        catch(Exception error){SettingsStatus.Text="清理未完成："+error.Message;}
        finally{_cleaningAttachments=false;await RefreshAttachmentUsageAsync();}
    }
}
