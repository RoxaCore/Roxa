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

namespace Roxa.Localization;

/// <summary>
/// Single source of truth for the supported languages. The settings page and the
/// first-run language picker both show this list; a second copy would drift apart
/// (a language added to one and forgotten in the other).
///
/// The names are the languages' own endonyms ("فارسی", "English"), never translated:
/// a user who cannot read the current UI must still find their own language in the list.
/// </summary>
public static class Languages
{
    /// <summary>Default for fresh installs and for any broken/missing saved value.</summary>
    public const string DefaultCode = "en";

    /// <summary>Endonym matching <see cref="DefaultCode"/>; keeps callers from hard-coding it.</summary>
    public const string DefaultName = "English";

    public static readonly (string Name, string Code)[] All =
    [
        ("فارسی", "fa"),
        ("English", "en"),
        ("العربية", "ar"),
        ("中文", "zh"),
        ("Русский", "ru"),
        ("Türkçe", "tr"),
        ("اردو", "ur"),
        ("Español", "es"),
        ("Français", "fr"),
        ("한국어", "ko"),
        ("Deutsch", "de"),
        ("日本語", "ja"),
        ("Português", "pt-br"),
        ("हिन्दी", "hi"),
    ];
}
