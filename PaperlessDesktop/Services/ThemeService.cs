using System.Windows;
using System.Windows.Media;

namespace PaperlessDesktop.Services;

internal static class ThemeService
{
    public const string DefaultAccentColor = "#17664F";

    private static readonly Uri StylesSource = new("/PaperlessDesktop;component/Themes/ThemeResources.xaml", UriKind.Relative);

    public static bool TryNormalizeAccent(string? value, out string normalized)
    {
        var hex = value?.Trim();
        if (hex is not { Length: 7 } || hex[0] != '#' || hex[1..].Any(character => !char.IsAsciiHexDigit(character)))
        {
            normalized = DefaultAccentColor;
            return false;
        }

        normalized = hex.ToUpperInvariant();
        return true;
    }

    public static void Apply(Application application, bool isDark, string accentColor)
    {
        TryNormalizeAccent(accentColor, out var normalized);

        var accent = ParseColor(normalized);
        var surface = ParseColor(isDark ? "#1D2923" : "#FFFFFF");
        var text = ParseColor(isDark ? "#E5F2EB" : "#182722");
        var onAccent = Contrast(accent, Colors.White) >= Contrast(accent, Colors.Black) ? Colors.White : Colors.Black;
        var accentShade = onAccent == Colors.White ? Colors.Black : Colors.White;
        var focus = VisibleAccent(accent, surface, text);
        var selection = Mix(surface, focus, isDark ? 0.20 : 0.16);

        var palette = new ResourceDictionary
        {
            ["WindowBackgroundBrush"] = Brush(isDark ? "#141C18" : "#F4F6F5"),
            ["SurfaceBrush"] = Brush(surface),
            ["SurfaceAltBrush"] = Brush(isDark ? "#223029" : "#F7FAF8"),
            ["TextBrush"] = Brush(text),
            ["MutedTextBrush"] = Brush(isDark ? "#B6C9C0" : "#39574B"),
            ["BorderBrush"] = Brush(isDark ? "#385045" : "#D9E3DE"),
            ["FieldBorderBrush"] = Brush(isDark ? "#729083" : "#6A897A"),
            ["HoverBrush"] = Brush(isDark ? "#2B4035" : "#E8F0EC"),
            ["DisabledBackgroundBrush"] = Brush(isDark ? "#28362E" : "#E7EEE9"),
            ["DisabledTextBrush"] = Brush(isDark ? "#A3B7AC" : "#586E62"),
            ["AccentBrush"] = Brush(accent),
            ["AccentHoverBrush"] = Brush(Mix(accent, accentShade, 0.14)),
            ["OnAccentBrush"] = Brush(onAccent),
            ["FocusBrush"] = Brush(focus),
            ["SelectionBrush"] = Brush(selection),
            ["DangerBrush"] = Brush(isDark ? "#F5A0A3" : "#A42C32")
        };

        palette[SystemColors.WindowBrushKey] = palette["SurfaceBrush"];
        palette[SystemColors.WindowTextBrushKey] = palette["TextBrush"];
        palette[SystemColors.ControlBrushKey] = palette["SurfaceAltBrush"];
        palette[SystemColors.ControlTextBrushKey] = palette["TextBrush"];
        palette[SystemColors.GrayTextBrushKey] = palette["DisabledTextBrush"];
        palette[SystemColors.HighlightBrushKey] = palette["SelectionBrush"];
        palette[SystemColors.HighlightTextBrushKey] = palette["TextBrush"];
        palette[SystemColors.InactiveSelectionHighlightBrushKey] = palette["SelectionBrush"];
        palette[SystemColors.InactiveSelectionHighlightTextBrushKey] = palette["TextBrush"];

        var dictionaries = application.Resources.MergedDictionaries;
        var theme = dictionaries.FirstOrDefault(dictionary => dictionary.Source == StylesSource);
        if (theme is null)
        {
            theme = new ResourceDictionary { Source = StylesSource };
            theme.MergedDictionaries.Add(palette);
            dictionaries.Add(theme);
        }
        else
        {
            theme.MergedDictionaries[0] = palette;
        }
    }

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush Brush(string hex) => Brush(ParseColor(hex));

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color VisibleAccent(Color accent, Color surface, Color text)
    {
        // Blend toward the theme text color until borders stay visible on the theme surface.
        for (int step = 0; step < 10; step++)
        {
            var candidate = Mix(accent, text, step / 10.0);
            if (Contrast(candidate, surface) >= 3.2)
            {
                return candidate;
            }
        }

        return text;
    }

    private static Color Mix(Color first, Color second, double ratio) => Color.FromRgb(
        (byte)Math.Round(first.R + (second.R - first.R) * ratio),
        (byte)Math.Round(first.G + (second.G - first.G) * ratio),
        (byte)Math.Round(first.B + (second.B - first.B) * ratio));

    private static double Contrast(Color first, Color second)
    {
        var light1 = Luminance(first);
        var light2 = Luminance(second);
        return (Math.Max(light1, light2) + 0.05) / (Math.Min(light1, light2) + 0.05);
    }

    private static double Luminance(Color color) => 0.2126 * LinearChannel(color.R) +
        0.7152 * LinearChannel(color.G) + 0.0722 * LinearChannel(color.B);

    private static double LinearChannel(byte value)
    {
        var channel = value / 255.0;
        return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }
}
