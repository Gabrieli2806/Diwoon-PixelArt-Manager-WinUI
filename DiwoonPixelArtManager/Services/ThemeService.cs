using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Media;

namespace DiwoonPixelArtManager.Services;

public static class ThemeService
{
    public static void Apply(Window window)
    {
        var settings = SettingsService.Instance.Settings;
        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = settings.Theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }

        window.SystemBackdrop = settings.Backdrop switch
        {
            "Mica" => new MicaBackdrop { Kind = MicaKind.Base },
            "MicaAlt" => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            "Acrylic" => new DesktopAcrylicBackdrop(),
            _ => null
        };
    }
}
