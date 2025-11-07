using System;
using System.Globalization;
using System.Windows.Data;

namespace S3VideoManager.Converters;

public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool boolean ? !boolean : true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool boolean ? !boolean : Binding.DoNothing;
    }
}
