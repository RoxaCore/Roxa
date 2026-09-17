using Avalonia.Data.Converters;
using System.Globalization;

namespace XrayClient.Localization;

public class TranslateConverter : IValueConverter
{
    public static readonly TranslateConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = parameter as string ?? value as string ?? "";
        return TranslationManager.Instance[key];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
