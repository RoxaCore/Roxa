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
//
// Additional terms under section 7 of that license: the "Roxa" name and
// visual identity are not licensed. See "The name and logo are not
// covered" in README.md.

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Roxa.Converters;

public class FlagToImageConverter : IValueConverter
{
    public static readonly FlagToImageConverter Instance = new();

    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static Bitmap? _fallback;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string code = value?.ToString()?.Trim().ToLowerInvariant() ?? "";
        if (code.Length != 2) return GetFallback();

        if (Cache.TryGetValue(code, out var cached)) return cached;

        try
        {
            var uri = new Uri($"avares://Roxa/Assets/Flags/{code}.png");
            var stream = AssetLoader.Open(uri);
            var bmp = new Bitmap(stream);
            Cache[code] = bmp;
            return bmp;
        }
        catch
        {
            return GetFallback();
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Bitmap GetFallback()
    {
        if (_fallback != null) return _fallback;
        try
        {
            var uri = new Uri("avares://Roxa/Assets/Flags/_unknown.png");
            _fallback = new Bitmap(AssetLoader.Open(uri));
        }
        catch
        {
            _fallback = null;
        }
        return _fallback!;
    }
}
