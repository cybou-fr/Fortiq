using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using Fortiq.Desktop.ViewModels;

namespace Fortiq.Desktop;

/// <summary>
/// Puts the chosen appearance into effect - both halves of it, in one place.
/// </summary>
/// <remarks>
/// Fortiq paints itself from <see cref="DesignTokens"/> while Avalonia's Fluent theme paints the
/// controls, and the two have to be told the same thing. Startup told only one: choosing "System"
/// set the Fluent variant to Default and left the token palette on its light default, so on a machine
/// running Windows in dark mode the controls came up dark and the text on them stayed dark ink. That
/// is the same black-on-black this codebase has already fixed twice, arriving by a third route.
///
/// Startup and the settings screen also carried their own copies of the branch, which is how they
/// came to disagree. There is one copy now, and following the system means following it while the
/// application is open rather than only at launch.
/// </remarks>
public static class AppTheme
{
    private static AppThemePreference _preference = AppThemePreference.System;
    private static bool _watching;

    /// <summary>Applies <paramref name="preference"/> to the Fluent theme and the product palette.</summary>
    public static void Apply(AppThemePreference preference)
    {
        _preference = preference;

        if (Avalonia.Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = preference switch
        {
            AppThemePreference.Dark => ThemeVariant.Dark,
            AppThemePreference.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };

        DesignTokens.SetTheme(IsDark(preference, app));
        Watch(app);
    }

    private static bool IsDark(AppThemePreference preference, Avalonia.Application app) => preference switch
    {
        AppThemePreference.Dark => true,
        AppThemePreference.Light => false,
        // Asked of the platform rather than read from ActualThemeVariant, which is not yet resolved
        // while the application is still initialising - which is exactly when this first runs.
        _ => app.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark
    };

    /// <summary>Follows Windows when the person asked to follow Windows, and only then.</summary>
    private static void Watch(Avalonia.Application app)
    {
        if (_watching || app.PlatformSettings is not { } settings)
        {
            return;
        }

        _watching = true;
        settings.ColorValuesChanged += (_, values) =>
        {
            if (_preference == AppThemePreference.System)
            {
                DesignTokens.SetTheme(values.ThemeVariant == PlatformThemeVariant.Dark);
            }
        };
    }
}
