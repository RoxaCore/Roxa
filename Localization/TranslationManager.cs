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

using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace Roxa.Localization;

public sealed class TranslationManager : INotifyPropertyChanged
{
    public static TranslationManager Instance { get; } = new();

    private static readonly ResourceManager _rm = new(
        "Roxa.Localization.Strings",
        typeof(TranslationManager).Assembly);

    private CultureInfo _culture = CultureInfo.GetCultureInfo("fa");

    // This project's neutral resource (Strings.resx) is Persian and there is no
    // separate Strings.fa.resx; "fa" IS the neutral resource.
    //
    // Knowing this is vital: with tryParents: false the ResourceManager only
    // looks at that language's own file and returns null when missing. Had we
    // looked up "fa" too, nothing would be found and all strings would fall
    // back to English — the default Persian would silently turn English. So
    // Persian is read straight from the neutral resource; the chain is for the rest.
    private const string NeutralLang = "fa";

    // Second language in the chain: untranslated keys show English instead of
    // Persian. Without it, missed keys in the 12 other languages came out Persian.
    private static readonly CultureInfo Fallback = CultureInfo.GetCultureInfo("en");

    // This project's neutral resource is exactly this language. We deliberately
    // do not use InvariantCulture: GetString on "fa" walks the same old path
    // (fa ← neutral) and returns what it always returned; the result is the
    // same, just without depending on how the neutral culture maps.
    private static readonly CultureInfo Neutral = CultureInfo.GetCultureInfo(NeutralLang);

    public CultureInfo Culture
    {
        get => _culture;
        set
        {
            if (_culture == value) return;
            _culture = value;
            CultureInfo.DefaultThreadCurrentCulture = value;
            CultureInfo.DefaultThreadCurrentUICulture = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    public string this[string key]
    {
        get
        {
            // 1) Neutral resource (Persian): both the default and the last
            //    refuge for any key missing from any language.
            string? persian = NeutralGet(key);

            // The active language is Persian itself; nothing left to look up.
            if (IsNeutralSelected) return persian ?? $"[{key}]";

            // 2) Active language — but only if really present in its own file.
            string? own = ExactGet(key, _culture, persian);
            if (own != null) return own;

            // 3) English for keys that are not translated.
            string? english = ExactGet(key, Fallback, persian);
            if (english != null) return english;

            // 4) A key that is not in the project at all: the old behavior.
            return persian ?? $"[{key}]";
        }
    }

    private bool IsNeutralSelected =>
        _culture.Name.Length == 0 ||
        _culture.Name.Equals(NeutralLang, StringComparison.OrdinalIgnoreCase);

    /// <summary>Persian value, read straight from the assembly's neutral resource — never depending on the fallback.</summary>
    private static string? NeutralGet(string key)
    {
        try
        {
            return _rm.GetString(key, Neutral) is { Length: > 0 } s ? s : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns it only when the key really exists in that language's own file.
    ///
    /// Two points:
    /// <list type="bullet">
    /// <item>tryParents must be false, or ResourceManager would silently fall
    /// back to the Persian neutral resource and there would be no way to tell
    /// which language was at fault — the very bug we had would return.</item>
    /// <item>createIfNotExists must be true, or a not-yet-loaded target
    /// language assembly would return null and reject a valid translation.</item>
    /// </list>
    ///
    /// The value is also compared against Persian: if identical, that language
    /// lacks the key and we must not hand the Persian back as a "translation".
    /// </summary>
    private static string? ExactGet(string key, CultureInfo culture, string? persian)
    {
        try
        {
            var set = _rm.GetResourceSet(culture, tryParents: false, createIfNotExists: true);
            string? s = set?.GetObject(key) as string;
            if (string.IsNullOrEmpty(s)) return null;
            if (persian != null && string.Equals(s, persian, StringComparison.Ordinal)) return null;
            return s;
        }
        catch { return null; }
    }

    public static string Get(string key) => Instance[key];

    /// <summary>
    /// Is the current language right-to-left? We ask .NET's own culture info
    /// rather than a manual list. Previously only "fa" and "ar" were listed and
    /// "ur" (Urdu) — also RTL — was missed and shown with a LTR layout.
    /// </summary>
    public bool IsRightToLeft
    {
        get
        {
            try { return _culture.TextInfo.IsRightToLeft; }
            catch { return false; }
        }
    }

    public void SetCulture(string code)
    {
        // The language code comes from the settings file; a broken/unknown value must not take the app down.
        try
        {
            Culture = string.IsNullOrWhiteSpace(code)
                ? CultureInfo.GetCultureInfo("fa")
                : CultureInfo.GetCultureInfo(code);
        }
        catch (CultureNotFoundException)
        {
            Culture = CultureInfo.GetCultureInfo("fa");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
