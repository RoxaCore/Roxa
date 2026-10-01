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

    public string this[string key] => _rm.GetString(key, _culture) ?? $"[{key}]";

    public static string Get(string key) => Instance[key];

    public void SetCulture(string code)
    {
        Culture = CultureInfo.GetCultureInfo(code);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
