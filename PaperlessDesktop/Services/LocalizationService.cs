using System.Globalization;
using System.Windows;

namespace PaperlessDesktop.Services;

internal static class LocalizationService
{
    private static ResourceDictionary? strings;
    private static CultureInfo formatCulture = CultureInfo.GetCultureInfo("sr-Latn-RS");

    public static string Language { get; private set; } = "sr";
    public static event EventHandler? LanguageChanged;

    public static void Apply(Application application, string language)
    {
        var selected = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "sr";
        var merged = application.Resources.MergedDictionaries;
        var activeIndex = strings is null ? -1 : merged.IndexOf(strings);
        if (activeIndex >= 0 && Language == selected)
        {
            return;
        }

        strings = Load(selected);
        Language = selected;
        formatCulture = CultureInfo.GetCultureInfo(selected == "en" ? "en-US" : "sr-Latn-RS");
        if (activeIndex >= 0)
        {
            merged[activeIndex] = strings;
        }
        else
        {
            merged.Add(strings);
        }

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Text(string key) => (strings ??= Load(Language))[key] as string ?? key;

    public static string Format(string key, params object?[] args) =>
        args.Length == 0 ? Text(key) : string.Format(formatCulture, Text(key), args);

    private static ResourceDictionary Load(string language) => new()
    {
        Source = new Uri($"/PaperlessDesktop;component/Resources/Languages/Strings.{language}.xaml", UriKind.Relative),
    };
}

internal readonly record struct LocalizedMessage(string Key, params object?[] Arguments)
{
    public override string ToString() => LocalizationService.Format(Key, Arguments);
}
