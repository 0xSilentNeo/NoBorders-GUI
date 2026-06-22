using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoBorder
{
    /// <summary>
    /// All user-configurable settings, persisted as JSON under
    /// %AppData%\NoBorder\settings.json. Plain data only - no Win32/WPF types here,
    /// so this can be unit-tested or reused from the CLI path without dragging in UI code.
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>If true, applies AccentColor instead of "no border" (DWMWA_COLOR_NONE).</summary>
        public bool UseCustomColor { get; set; } = false;

        /// <summary>ARGB hex string, e.g. "#FF7FE7E0". Only used when UseCustomColor is true.</summary>
        public string AccentColorHex { get; set; } = "#FF7FE7E0";

        /// <summary>Process names (without .exe) and/or window-title substrings to skip entirely.
        /// Matching is case-insensitive "contains" against either the process name or title.</summary>
        public List<string> ExcludedApps { get; set; } = new();

        /// <summary>Whether a global hotkey toggles watch mode.</summary>
        public bool HotkeyEnabled { get; set; } = false;

        /// <summary>Win32 virtual-key code for the hotkey's main key (e.g. 0x42 = 'B').</summary>
        public uint HotkeyKey { get; set; } = 0x42; // 'B'

        /// <summary>Modifier flags (MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_WIN), see HotkeyManager.</summary>
        public uint HotkeyModifiers { get; set; } = 0x0001 | 0x0002; // Alt + Ctrl

        /// <summary>When launched at startup, whether the window opens visibly or stays tray-only.</summary>
        public bool ShowWindowOnStartup { get; set; } = false;

        /// <summary>"system", "dark", or "light". "system" follows the Windows app theme setting.</summary>
        public string ThemePreference { get; set; } = "system";

        /// <summary>Whether to show a toast notification when watch mode starts/stops.</summary>
        public bool ShowToastNotifications { get; set; } = true;

        [JsonIgnore]
        public static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NoBorder", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                string path = SettingsPath;
                if (!File.Exists(path)) return new AppSettings();

                string json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded == null) return new AppSettings();

                // A hand-edited or corrupted settings file could have an explicit "null"
                // for a list property, which deserialization honors literally and would
                // otherwise bypass the property's normal default initializer.
                loaded.ExcludedApps ??= new List<string>();
                loaded.AccentColorHex ??= "#FF7FE7E0";
                loaded.ThemePreference ??= "system";

                return loaded;
            }
            catch
            {
                // Corrupt or unreadable settings file shouldn't prevent the app from starting -
                // fall back to defaults rather than crash on load.
                return new AppSettings();
            }
        }

        public void Save()
        {
            try
            {
                string path = SettingsPath;
                string? dir = Path.GetDirectoryName(path);
                if (dir != null) Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch
            {
                // Best-effort persistence - a failed save (e.g. permissions, disk full)
                // shouldn't crash whatever UI action triggered it.
            }
        }
    }
}
