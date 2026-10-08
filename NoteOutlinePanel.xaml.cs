using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using System.Globalization;

namespace Cdiary;

public partial class NoteOutlinePanel : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ActiveHeadingIdProperty = DependencyProperty.Register(
        nameof(ActiveHeadingId), typeof(string), typeof(NoteOutlinePanel));
    public string? ActiveHeadingId { get => (string?)GetValue(ActiveHeadingIdProperty); private set => SetValue(ActiveHeadingIdProperty, value); }
    public event Action<string>? NavigateRequested;
    public event Action? CloseRequested;
    public NoteOutlinePanel() { InitializeComponent(); }
    public void SetEntries(IReadOnlyList<NoteHeading> entries)
    {
        HeadingsList.ItemsSource = entries;
        EmptyHint.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HeadingsList.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    public void SetActive(string? id)
    { ActiveHeadingId = id; }
    private void Close_Click(object sender, RoutedEventArgs args) => CloseRequested?.Invoke();
    private void List_MouseUp(object sender, MouseButtonEventArgs args)
    {
        if (args.OriginalSource is not DependencyObject target) return;
        var item = ItemsControl.ContainerFromElement(HeadingsList, target) as ListBoxItem;
        if (item?.Content is NoteHeading heading) { NavigateRequested?.Invoke(heading.Id); args.Handled = true; }
    }
    private void List_KeyDown(object sender, System.Windows.Input.KeyEventArgs args)
    {
        if (args.Key == Key.Escape) { CloseRequested?.Invoke(); args.Handled = true; }
        if (args.Key == Key.Enter && HeadingsList.SelectedItem is NoteHeading heading) { NavigateRequested?.Invoke(heading.Id); args.Handled = true; }
    }
}

public sealed class OutlineActiveConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2 && values[0] is string id && values[1] is string active && id == active;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
