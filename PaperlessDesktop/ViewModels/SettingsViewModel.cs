using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using PaperlessDesktop.Models;
using PaperlessDesktop.Services;

namespace PaperlessDesktop.ViewModels;

internal sealed class SettingsViewModel : ObservableViewModel
{
    private bool isDark;
    private bool isEnglish;
    private string accentHex;
    private int red;
    private int green;
    private int blue;

    public SettingsViewModel(AppSettings settings)
    {
        settings = settings.Normalize();
        isDark = settings.IsDark;
        isEnglish = settings.Language == "en";
        accentHex = settings.AccentColor;
        UpdateColorChannels(accentHex);
    }

    public bool IsDark
    {
        get => isDark;
        set
        {
            if (SetProperty(ref isDark, value))
            {
                NotifyPropertyChanged(nameof(IsLight));
            }
        }
    }

    public bool IsLight
    {
        get => !IsDark;
        set
        {
            if (value)
            {
                IsDark = false;
            }
        }
    }

    public bool IsEnglish
    {
        get => isEnglish;
        set
        {
            if (SetProperty(ref isEnglish, value))
            {
                NotifyPropertyChanged(nameof(IsSerbian));
            }
        }
    }

    public bool IsSerbian
    {
        get => !IsEnglish;
        set
        {
            if (value)
            {
                IsEnglish = false;
            }
        }
    }

    public string AccentHex
    {
        get => accentHex;
        set
        {
            if (SetProperty(ref accentHex, value))
            {
                if (ThemeService.TryNormalizeAccent(accentHex, out var normalized))
                {
                    UpdateColorChannels(normalized);
                }

                NotifyPropertyChanged(nameof(AccentPreview));
                NotifyPropertyChanged(nameof(HasValidAccent));
                NotifyPropertyChanged(nameof(HasAccentError));
            }
        }
    }

    public int Red
    {
        get => red;
        set => ChangeChannel(ref red, value);
    }

    public int Green
    {
        get => green;
        set => ChangeChannel(ref green, value);
    }

    public int Blue
    {
        get => blue;
        set => ChangeChannel(ref blue, value);
    }

    public Brush AccentPreview => new SolidColorBrush(Color.FromRgb((byte)red, (byte)green, (byte)blue));
    public bool HasValidAccent => ThemeService.TryNormalizeAccent(AccentHex, out _);
    public bool HasAccentError => !HasValidAccent;

    public AppSettings GetSettings() => new(IsDark, AccentHex, IsEnglish ? "en" : "sr");

    private void UpdateColorChannels(string color)
    {
        SetProperty(ref red, ReadChannel(color, 1), nameof(Red));
        SetProperty(ref green, ReadChannel(color, 3), nameof(Green));
        SetProperty(ref blue, ReadChannel(color, 5), nameof(Blue));
    }

    private static int ReadChannel(string color, int start) =>
        int.Parse(color.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private void ChangeChannel(ref int channel, int value, [CallerMemberName] string? name = null)
    {
        if (SetProperty(ref channel, Math.Clamp(value, 0, 255), name))
        {
            AccentHex = $"#{Red:X2}{Green:X2}{Blue:X2}";
        }
    }
}
