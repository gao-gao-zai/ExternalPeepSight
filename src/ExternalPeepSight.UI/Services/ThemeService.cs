using Avalonia;
using Avalonia.Styling;

namespace ExternalPeepSight.UI.Services;

internal sealed class ThemeService
{
    public AppTheme Theme { get; private set; }

    public void Apply(AppTheme theme)
    {
        Theme = theme;
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }
}
