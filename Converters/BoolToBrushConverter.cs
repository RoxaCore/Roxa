using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace XrayClient.Converters;

public class BoolToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && parameter is string colors)
        {
            var parts = colors.Split(',');
            if (parts.Length == 2)
            {
                var brush = new SolidColorBrush(Color.Parse(parts[0].Trim()));
                var other = new SolidColorBrush(Color.Parse(parts[1].Trim()));
                return b ? brush : other;
            }
        }
        return new SolidColorBrush(Colors.Transparent);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
