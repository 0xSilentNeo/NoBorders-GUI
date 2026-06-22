using System;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace NoBorder
{
    /// <summary>
    /// Reads the Windows "app theme" setting (the same one that controls whether most
    /// built-in apps render light or dark) and swaps NoBorder's color resources to match.
    /// Also supports a forced light/dark override via AppSettings.ThemePreference.
    /// </summary>
    public static class ThemeManager
    {
        private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string AppsUseLightThemeValue = "AppsUseLightTheme";

        public static bool IsSystemInLightMode()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
                object? value = key?.GetValue(AppsUseLightThemeValue);
                // The value is 1 for light mode, 0 for dark. Default to dark (our original
                // design) if the key is missing, e.g. on Windows versions that predate it.
                return value is int i && i == 1;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Resolves "system"/"dark"/"light" into an actual light/dark decision.</summary>
        public static bool ShouldUseLightPalette(string themePreference)
        {
            return themePreference switch
            {
                "light" => true,
                "dark" => false,
                _ => IsSystemInLightMode(), // "system" or anything unrecognized
            };
        }

        /// <summary>Overwrites the app's color brushes/colors in-place so every window and
        /// control bound via {DynamicResource ...} picks up the new palette immediately,
        /// without needing to rebuild or re-show any window. Requires DynamicResource, not
        /// StaticResource, in the XAML that consumes these keys - StaticResource resolves
        /// once at load time and won't observe a dictionary entry being replaced later.</summary>
        public static void Apply(bool light)
        {
            var resources = Application.Current.Resources;

            (string key, string hex)[] colors = light
                ? new[]
                {
                    ("BgColor", "#F3F3F5"),
                    ("BgRaisedColor", "#FFFFFF"),
                    ("StrokeColor", "#DCDEE3"),
                    ("TextColor", "#1B1D22"),
                    ("TextMutedColor", "#6B707A"),
                    ("AccentColor", "#1AA89E"),   // darker teal so it stays legible on white
                    ("AccentDimColor", "#BFE9E5"),
                    ("DangerColor", "#C24545"),
                }
                : new[]
                {
                    ("BgColor", "#1B1D22"),
                    ("BgRaisedColor", "#23262D"),
                    ("StrokeColor", "#33363F"),
                    ("TextColor", "#ECEDEF"),
                    ("TextMutedColor", "#8A8F99"),
                    ("AccentColor", "#7FE7E0"),
                    ("AccentDimColor", "#3E5654"),
                    ("DangerColor", "#E78F8F"),
                };

            foreach (var (key, hex) in colors)
            {
                var color = (Color)ColorConverter.ConvertFromString(hex)!;
                resources[key] = color;

                // Replace the brush entirely rather than mutating the existing instance's
                // Color property in place. WPF can produce frozen (read-only) Freezable
                // instances for brushes declared in XAML as a runtime optimization, even
                // without an explicit Freeze() call or PresentationOptions:Freeze attribute -
                // mutating a frozen brush throws InvalidOperationException. A brand-new
                // SolidColorBrush is never frozen unless we freeze it ourselves, so replacing
                // the dictionary entry sidesteps the question entirely instead of relying on
                // the existing brush being mutable.
                string brushKey = key.Replace("Color", "Brush");
                resources[brushKey] = new SolidColorBrush(color);
            }
        }
    }
}
