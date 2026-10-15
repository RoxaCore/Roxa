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

    // منبعِ خنثیِ این پروژه (Strings.resx) فارسی است و فایلِ جداگانه‌ای به نام
    // Strings.fa.resx نداریم؛ یعنی "fa" خودِ همان منبعِ خنثی است.
    //
    // دانستنِ این نکته حیاتی است: ResourceManager وقتی با tryParents: false بخواهید،
    // فقط فایلِ خودِ همان زبان را نگاه می‌کند و اگر نبود null می‌دهد. اگر "fa" را هم
    // مثلِ بقیه جست‌وجو می‌کردیم، هیچ‌چیز پیدا نمی‌شد و همه‌چیز به انگلیسی می‌پرید —
    // یعنی زبانِ پیشفرضِ فارسی، بی‌صدا انگلیسی می‌شد. پس فارسی همیشه مستقیم از
    // خودِ منبعِ خنثی خوانده می‌شود و زنجیره فقط برایِ زبان‌هایِ دیگر به کار می‌رود.
    private const string NeutralLang = "fa";

    // زبانِ دومِ زنجیره: برایِ هر کلیدِ هنوز ترجمه‌نشده، به‌جایِ فارسی انگلیسی نشان
    // داده می‌شود. بدونِ این، هر کلیدِ جاافتاده در ۱۲ زبانِ دیگر مستقیم فارسی بود.
    private static readonly CultureInfo Fallback = CultureInfo.GetCultureInfo("en");

    // منبعِ خنثیِ این پروژه همین زبان است. عمداً از InvariantCulture استفاده نمی‌کنیم:
    // GetString رویِ "fa" همان مسیرِ قدیمی را می‌رود (fa ← خنثی) و همان چیزی را می‌دهد
    // که پیش از این تغییر می‌داد؛ نتیجه همان است، فقط بدونِ اتکا بهِ اینکه خنثی
    // دقیقاً چطور نگاشت می‌شود.
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
            // ۱) منبعِ خنثی (فارسی): هم پیشفرض، هم پناهگاهِ آخر برایِ هر کلیدِ
            //    جاافتاده در هر زبانی.
            string? persian = NeutralGet(key);

            // زبانِ فعال همان فارسی است؛ دیگر چیزی برایِ جست‌وجو نداریم.
            if (IsNeutralSelected) return persian ?? $"[{key}]";

            // ۲) زبانِ فعال — فقط اگر واقعاً در فایلِ خودِ آن زبان باشد.
            string? own = ExactGet(key, _culture, persian);
            if (own != null) return own;

            // ۳) انگلیسی برایِ کلیدهایِ ترجمه‌نشده.
            string? english = ExactGet(key, Fallback, persian);
            if (english != null) return english;

            // ۴) کلیدی که اصلاً در پروژه نیست: همان رفتارِ قدیمی.
            return persian ?? $"[{key}]";
        }
    }

    private bool IsNeutralSelected =>
        _culture.Name.Length == 0 ||
        _culture.Name.Equals(NeutralLang, StringComparison.OrdinalIgnoreCase);

    /// <summary>مقدارِ فارسی، مستقیم از منبعِ خنثیِ مونتاژ — بی‌آنکه بهِ fallback وابسته باشد.</summary>
    private static string? NeutralGet(string key)
    {
        try
        {
            return _rm.GetString(key, Neutral) is { Length: > 0 } s ? s : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// فقط اگر کلید واقعاً در فایلِ خودِ همان زبان باشد، همان را برمی‌گرداند.
    ///
    /// دو نکته:
    /// <list type="bullet">
    /// <item>tryParents باید false باشد، وگرنه ResourceManager بی‌صدا به منبعِ خنثیِ
    /// فارسی برمی‌گشت و دیگر نمی‌شد فهمید زبانِ مقصر کدام است — یعنی همان باگی که
    /// داشتیم دوباره سر می‌زد.</item>
    /// <item>createIfNotExists باید true باشد، وگرنه اگر مونتاژِ زبانِ مقصد هنوز
    /// بارگذاری نشده بود، null می‌داد و ترجمهِ موجود هم بی‌دلیل رد می‌شد.</item>
    /// </list>
    ///
    /// مقدار با فارسی هم مقایسه می‌شود: اگر یکی بود یعنی آن زبان کلید را ندارد و
    /// نباید همان فارسی را به‌عنوانِ «ترجمه» پس بدهیم.
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
    /// آیا زبانِ فعلی راست‌به‌چپ است؟ از خودِ دانشنامهٔ زبانِ .NET می‌پرسیم، نه از یک
    /// فهرستِ دستی. قبلاً فقط "fa" و "ar" در فهرست بودند و "ur" (اردو) — که آن هم
    /// راست‌به‌چپ است — جا می‌ماند و با چیدمانِ چپ‌به‌راست نشان داده می‌شد.
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
        // کدِ زبان از فایل تنظیمات می‌آید؛ مقدار خراب/ناشناخته نباید برنامه را بیندازد.
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
