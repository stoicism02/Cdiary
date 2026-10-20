using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Cdiary;

public sealed class CalendarGridHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Math.Max(280, value is double height && double.IsFinite(height) ? height - 2 : 280);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}
