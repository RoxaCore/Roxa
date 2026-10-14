// Roxa - a VPN client for Windows built on Xray-core
// Copyright (C) 2026 Roxa contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Roxa.Converters;

public class BoolToBrushConverter : IValueConverter
{
    public static readonly BoolToBrushConverter Instance = new();

    private static readonly Dictionary<string, (SolidColorBrush True, SolidColorBrush False)> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && parameter is string colors)
        {
            if (!Cache.TryGetValue(colors, out var cached))
            {
                var parts = colors.Split(',');
                if (parts.Length == 2)
                {
                    cached = (new SolidColorBrush(Color.Parse(parts[0].Trim())), new SolidColorBrush(Color.Parse(parts[1].Trim())));
                    Cache[colors] = cached;
                }
            }
            if (Cache.TryGetValue(colors, out cached))
                return b ? cached.True : cached.False;
        }
        return new SolidColorBrush(Colors.Transparent);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
