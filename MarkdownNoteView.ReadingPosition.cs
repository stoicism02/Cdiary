using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;

namespace Cdiary;

public partial class MarkdownNoteView
{
    private static string ReadingText(Paragraph paragraph)
    {
        var text = Regex.Replace(new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text, @"\s+", " ").Trim();
        return text[..Math.Min(512, text.Length)];
    }
    public NoteReadingPosition? CaptureReadingPosition()
    {
        if (!PreviewVisible || Preview.Document is null || FindOutlineScroll(Preview) is not { } scroll) return null;
        var paragraphs = OutlineParagraphs(Preview.Document).ToArray();
        if (paragraphs.Length == 0) return null;
        var index = 0;
        for (var i = 0; i < paragraphs.Length; i++)
            if (paragraphs[i].ContentStart.HasValidLayout && paragraphs[i].ContentStart.GetCharacterRect(LogicalDirection.Forward).Top <= 14) index = i;
        var paragraph = paragraphs[index];
        if (!paragraph.ContentStart.HasValidLayout) return null;
        var start = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        var end = paragraph.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
        var text = ReadingText(paragraph);
        return new(text, paragraphs.Take(index).Count(p => ReadingText(p) == text), index,
            Math.Clamp((14 - start.Top) / Math.Max(1, end.Bottom - start.Top), 0, 1),
            scroll.VerticalOffset / Math.Max(1, scroll.ScrollableHeight));
    }
    private void RestoreReadingPosition(NoteReadingPosition? position)
    {
        if (position is null || Preview.Document is null || FindOutlineScroll(Preview) is not { } scroll) return;
        var paragraphs = OutlineParagraphs(Preview.Document).ToArray();
        var target = paragraphs.Where(p => ReadingText(p) == position.Text).Skip(position.Occurrence).FirstOrDefault()
            ?? paragraphs.ElementAtOrDefault(position.Index);
        if (target is not null && target.ContentStart.HasValidLayout)
        {
            var start = target.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            var end = target.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
            scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + start.Top - 14 + Math.Max(1, end.Bottom - start.Top) * position.Fraction));
        }
        else scroll.ScrollToVerticalOffset(position.Ratio * scroll.ScrollableHeight);
    }
    private static IEnumerable<NotePreviewImage> ReadingImages(DependencyObject root)
    {
        if (root is NotePreviewImage image) { yield return image; yield break; }
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in ReadingImages(child)) yield return item;
    }
    private async System.Threading.Tasks.Task SettleReadingImagesAsync()
    {
        UpdateLayout();
        if (Preview.Document is null) return;
        var pending = ReadingImages(Preview.Document).Select(image => image.PendingLoad).OfType<System.Threading.Tasks.Task>().ToArray();
        if (pending.Length > 0)
            await System.Threading.Tasks.Task.WhenAny(System.Threading.Tasks.Task.WhenAll(pending), System.Threading.Tasks.Task.Delay(600));
        UpdateLayout();
    }
}
