using System.Globalization;
using System.Windows.Data;

namespace RsConverterUI;

/// <summary>true ⇄ false (to disable the slope slider while the automatic double click is on).</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
