using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
// These aliases are now mostly redundant defensive documentation: NoBorder.csproj
// removes the implicit global usings for System.Drawing and System.Windows.Forms
// (added automatically by UseWindowsForms=true) specifically because they kept
// colliding with same-named WPF types across this file and others. Left here anyway
// as cheap insurance and as a note of which names have historically been a problem.
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using Color = System.Windows.Media.Color;

namespace NoBorder
{
    public partial class SettingsView : UserControl
    {
        private readonly AppSettings _settings;
        private readonly BorderRemovalEngine _engine;
        private readonly HotkeyManager _hotkeyManager;
        private bool _isLoadingFromSettings; // suppresses change-handlers while populating UI from saved state
        private bool _isCapturingHotkey;

        // A small fixed swatch palette, in addition to the "current color" swatch which
        // opens the full picker. Covers common preferences without forcing a dialog open
        // every time for people who just want "a different color than teal."
        private static readonly (string Name, string Hex)[] PresetSwatches =
        {
            ("Teal", "#FF7FE7E0"),
            ("Blue", "#FF5B9BD5"),
            ("Purple", "#FFB389D9"),
            ("Pink", "#FFE391C4"),
            ("Amber", "#FFE3B05B"),
            ("Red", "#FFE36B6B"),
        };

        public event Action? SettingsChanged;

        public SettingsView(AppSettings settings, BorderRemovalEngine engine, HotkeyManager hotkeyManager)
        {
            InitializeComponent();
            _settings = settings;
            _engine = engine;
            _hotkeyManager = hotkeyManager;

            BuildSwatches();
            LoadFromSettings();
        }

        private void LoadFromSettings()
        {
            _isLoadingFromSettings = true;

            if (_settings.UseCustomColor)
            {
                CustomColorRadio.IsChecked = true;
                ColorPickerRow.Visibility = Visibility.Visible;
            }
            else
            {
                NoBorderRadio.IsChecked = true;
                ColorPickerRow.Visibility = Visibility.Collapsed;
            }
            UpdateCurrentColorSwatch();

            RefreshExclusionsList();

            HotkeyEnabledToggle.IsChecked = _settings.HotkeyEnabled;
            UpdateHotkeyButtonLabel();

            ShowWindowOnStartupToggle.IsChecked = _settings.ShowWindowOnStartup;
            ToastToggle.IsChecked = _settings.ShowToastNotifications;

            switch (_settings.ThemePreference)
            {
                case "dark": ThemeDarkRadio.IsChecked = true; break;
                case "light": ThemeLightRadio.IsChecked = true; break;
                default: ThemeSystemRadio.IsChecked = true; break;
            }

            if (!BorderRemovalEngine.IsBorderColorSupported())
            {
                CompatWarningBox.Visibility = Visibility.Visible;
            }

            _isLoadingFromSettings = false;
        }

        // ---------------- Border mode ----------------

        private void BuildSwatches()
        {
            foreach (var (name, hex) in PresetSwatches)
            {
                var swatch = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!),
                    Margin = new Thickness(0, 0, 8, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = name,
                    Tag = hex
                };
                swatch.MouseLeftButtonUp += PresetSwatch_Clicked;
                SwatchList.Items.Add(swatch);
            }
        }

        private void PresetSwatch_Clicked(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { Tag: string hex })
            {
                SetAccentColor(hex);
            }
        }

        private void CustomSwatch_Clicked(object sender, MouseButtonEventArgs e)
        {
            Color current;
            try
            {
                current = (Color)ColorConverter.ConvertFromString(_settings.AccentColorHex)!;
            }
            catch
            {
                current = (Color)ColorConverter.ConvertFromString("#FF7FE7E0")!;
            }

            var dialog = new ColorPickerDialog(current) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true && dialog.SelectedColor is Color picked)
            {
                string hex = $"#FF{picked.R:X2}{picked.G:X2}{picked.B:X2}";
                SetAccentColor(hex);
            }
        }

        private void SetAccentColor(string hex)
        {
            _settings.AccentColorHex = hex;
            _settings.Save();
            UpdateCurrentColorSwatch();
            ReapplyIfWatching();
        }

        private void UpdateCurrentColorSwatch()
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(_settings.AccentColorHex)!;
                CurrentColorSwatch.Background = new SolidColorBrush(color);
            }
            catch
            {
                CurrentColorSwatch.Background = (Brush)FindResource("AccentBrush");
            }
        }

        private void BorderModeRadio_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingFromSettings) return;

            bool useCustom = CustomColorRadio.IsChecked == true;
            ColorPickerRow.Visibility = useCustom ? Visibility.Visible : Visibility.Collapsed;

            _settings.UseCustomColor = useCustom;
            _settings.Save();
            ReapplyIfWatching();
        }

        // ---------------- Exclusions ----------------

        private void RefreshExclusionsList()
        {
            ExclusionsList.Items.Clear();
            foreach (var pattern in _settings.ExcludedApps)
            {
                ExclusionsList.Items.Add(BuildExclusionChip(pattern));
            }
            NoExclusionsText.Visibility = _settings.ExcludedApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private Border BuildExclusionChip(string pattern)
        {
            var chip = new Border { Style = (Style)FindResource("ExclusionChip") };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new TextBlock
            {
                Text = pattern,
                Style = (Style)FindResource("BodyText"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(text, 0);

            var removeButton = new Button
            {
                Content = "\uE711", // Segoe MDL2 Assets "X" glyph
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Style = (Style)FindResource("QuietButton"),
                Padding = new Thickness(6),
                Tag = pattern
            };
            removeButton.Click += RemoveExclusion_Click;
            Grid.SetColumn(removeButton, 1);

            grid.Children.Add(text);
            grid.Children.Add(removeButton);
            chip.Child = grid;
            return chip;
        }

        private void RemoveExclusion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string pattern })
            {
                _settings.ExcludedApps.Remove(pattern);
                _settings.Save();
                RefreshExclusionsList();
                ReapplyIfWatching();
            }
        }

        private void AddExclusionFromInput()
        {
            string text = ExclusionInput.Text.Trim();
            if (text.Length == 0) return;

            // Avoid exact duplicate entries (case-insensitive) cluttering the list.
            bool alreadyPresent = _settings.ExcludedApps.Exists(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase));
            if (!alreadyPresent)
            {
                _settings.ExcludedApps.Add(text);
                _settings.Save();
                RefreshExclusionsList();
                ReapplyIfWatching();
            }
            ExclusionInput.Text = "";
        }

        private void AddExclusionButton_Click(object sender, RoutedEventArgs e) => AddExclusionFromInput();

        private void ExclusionInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddExclusionFromInput();
                e.Handled = true;
            }
        }

        private void PickFromOpenWindowsButton_Click(object sender, RoutedEventArgs e)
        {
            var windows = BorderRemovalEngine.ListOpenWindows();
            var dialog = new WindowPickerDialog(windows);
            if (dialog.ShowDialog() == true && dialog.SelectedPattern is { Length: > 0 } pattern)
            {
                bool alreadyPresent = _settings.ExcludedApps.Exists(p => string.Equals(p, pattern, StringComparison.OrdinalIgnoreCase));
                if (!alreadyPresent)
                {
                    _settings.ExcludedApps.Add(pattern);
                    _settings.Save();
                    RefreshExclusionsList();
                    ReapplyIfWatching();
                }
            }
        }

        // ---------------- Hotkey ----------------

        private void HotkeyEnabledToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingFromSettings) return;

            _settings.HotkeyEnabled = HotkeyEnabledToggle.IsChecked == true;
            _settings.Save();
            ApplyHotkeyRegistration();
        }

        /// <summary>(Re)registers or unregisters the global hotkey based on current settings.
        /// Exposed so MainWindow can call this once at startup too (after the window has a
        /// real HWND, which the constructor path doesn't have yet).</summary>
        public void ApplyHotkeyRegistration()
        {
            if (_settings.HotkeyEnabled)
            {
                var window = Window.GetWindow(this);
                if (window != null)
                {
                    _hotkeyManager.Register(window, _settings.HotkeyKey, _settings.HotkeyModifiers);
                }
                // If window is null here, this control isn't attached to a window yet -
                // the caller (MainWindow_Loaded) is expected to call this again once it is.
            }
            else
            {
                _hotkeyManager.Unregister();
            }
        }

        private void UpdateHotkeyButtonLabel()
        {
            HotkeyCaptureButton.Content = FormatHotkeyLabel(_settings.HotkeyModifiers, _settings.HotkeyKey);
        }

        private static string FormatHotkeyLabel(uint modifiers, uint key)
        {
            const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;
            var parts = new List<string>();
            if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
            if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
            if ((modifiers & MOD_WIN) != 0) parts.Add("Win");
            parts.Add(VirtualKeyToDisplayName(key));
            return string.Join(" + ", parts);
        }

        private static string VirtualKeyToDisplayName(uint vk)
        {
            // Covers letters/digits directly; anything else falls back to a generic
            // "Key 0xNN" label rather than guessing at a name we might get wrong.
            if (vk >= 0x30 && vk <= 0x5A) return ((char)vk).ToString();
            return $"Key 0x{vk:X2}";
        }

        private void HotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
        {
            _isCapturingHotkey = true;
            HotkeyCaptureButton.Content = "Press a key combo...";
            HotkeyHintText.Text = "Press Esc to cancel.";
            Keyboard.Focus(HotkeyCaptureButton);
        }

        private void HotkeyCaptureButton_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isCapturingHotkey) return;
            e.Handled = true;

            if (e.Key == Key.Escape)
            {
                _isCapturingHotkey = false;
                UpdateHotkeyButtonLabel();
                HotkeyHintText.Text = "";
                return;
            }

            // Ignore bare modifier presses - wait for an actual key combined with modifiers.
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
            {
                return;
            }

            const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;
            uint modifiers = 0;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= MOD_CONTROL;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= MOD_ALT;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= MOD_SHIFT;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= MOD_WIN;

            if (modifiers == 0)
            {
                HotkeyHintText.Text = "Include at least one modifier key (Ctrl, Alt, Shift, or Win).";
                return;
            }

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(e.Key);

            _isCapturingHotkey = false;
            _settings.HotkeyKey = vk;
            _settings.HotkeyModifiers = modifiers;
            _settings.Save();
            UpdateHotkeyButtonLabel();
            HotkeyHintText.Text = "Saved.";

            if (_settings.HotkeyEnabled)
            {
                ApplyHotkeyRegistration();
            }
        }

        // ---------------- Startup behavior ----------------

        private void ShowWindowOnStartupToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingFromSettings) return;
            _settings.ShowWindowOnStartup = ShowWindowOnStartupToggle.IsChecked == true;
            _settings.Save();
        }

        // ---------------- Theme ----------------

        private void ThemeRadio_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingFromSettings) return;

            string preference = "system";
            if (ThemeDarkRadio.IsChecked == true) preference = "dark";
            else if (ThemeLightRadio.IsChecked == true) preference = "light";

            _settings.ThemePreference = preference;
            _settings.Save();

            bool light = ThemeManager.ShouldUseLightPalette(preference);
            ThemeManager.Apply(light);
        }

        // ---------------- Notifications ----------------

        private void ToastToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingFromSettings) return;
            _settings.ShowToastNotifications = ToastToggle.IsChecked == true;
            _settings.Save();
        }

        // ---------------- Shared ----------------

        private void ReapplyIfWatching()
        {
            if (_engine.IsWatching)
            {
                _engine.ReapplyAll();
            }
            SettingsChanged?.Invoke();
        }
    }
}
