using System;
using System.Globalization;
using System.Windows.Data;

namespace S3VideoManager.Converters;

public class NullToBooleanConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isNull = value is null;
        var result = Invert ? isNull : !isNull;
        return result;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
