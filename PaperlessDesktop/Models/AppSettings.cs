using PaperlessDesktop.Services;

namespace PaperlessDesktop.Models;

internal sealed record AppSettings(
    bool IsDark = false,
    string AccentColor = ThemeService.DefaultAccentColor,
    string Language = "sr")
{
    public AppSettings Normalize() => this with
    {
        AccentColor = ThemeService.TryNormalizeAccent(AccentColor, out var accent) ? accent : ThemeService.DefaultAccentColor,
        Language = Language == "en" ? "en" : "sr"
    };
}
