using System.Globalization;

namespace Cdiary;

public sealed record SearchResult(DateTime Date, long? TaskId, string Title, string Snippet)
{
    public string DateLabel => Date.ToString("yyyy年M月d日", CultureInfo.GetCultureInfo("zh-CN"));
    public string KindLabel => TaskId is null ? "备忘" : "任务";
}
