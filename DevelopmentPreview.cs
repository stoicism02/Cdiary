using System.IO;

namespace Cdiary;

public static class DevelopmentPreview
{
    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cdiary", "DevelopmentPreview");

    public static DiaryRepository OpenRepository(string directory)
    {
        var database = Path.Combine(directory, "preview.db");
        var isNew = !File.Exists(database);
        var repository = new DiaryRepository(database);
        if (isNew)
        {
            var today = DateTime.Today;
            var task = repository.AddTask(today, "整理明天的计划");
            task.Note = "这是独立的示例笔记。修改共享样式时，输入内容和窗口会保留。";
            repository.SaveTask(task);
            repository.AddTask(today, "给小花浇水");
            repository.AddTask(today, "读一会儿书");
            repository.AddTask(today.AddDays(-1), "整理上周的照片");
            repository.SaveDayMemo(today, "开发预览使用独立数据，不影响日常记录。");
        }
        return repository;
    }
}
