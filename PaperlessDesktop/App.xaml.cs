using System.Windows;
using PaperlessDesktop.Models;
using PaperlessDesktop.Services;

namespace PaperlessDesktop;

public partial class App : Application
{
    private readonly AppSettingsStore settingsStore = new(AppSettingsStore.DefaultPath);

    internal AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        Settings = settingsStore.Load();
        ApplySettings();
        base.OnStartup(e);
    }

    internal void SaveSettings(AppSettings settings)
    {
        var normalized = settings.Normalize();
        settingsStore.Save(normalized);
        Settings = normalized;
        ApplySettings();
    }

    private void ApplySettings()
    {
        ThemeService.Apply(this, Settings.IsDark, Settings.AccentColor);
        LocalizationService.Apply(this, Settings.Language);
    }
}
