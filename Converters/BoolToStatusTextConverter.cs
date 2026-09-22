using System.Globalization;
using Avalonia.Data.Converters;

namespace XrayClient.Converters;

public class BoolToStatusTextConverter : IValueConverter
{
    public static readonly BoolToStatusTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && parameter is string formats)
        {
            var parts = formats.Split(',');
            if (parts.Length == 2)
                return b ? parts[0].Trim() : parts[1].Trim();
        }
        return "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
