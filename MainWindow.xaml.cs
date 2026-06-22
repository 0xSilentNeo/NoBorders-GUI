using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace NoBorder
{
    public partial class MainWindow : Window
    {
        private readonly BorderRemovalEngine _engine;
        private readonly AppSettings _settings;
        private readonly HotkeyManager _hotkeyManager;
        private SettingsView? _settingsView;
        private IDisposable? _stopListener;
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "NoBorder";

        public MainWindow(BorderRemovalEngine engine, AppSettings settings, HotkeyManager hotkeyManager)
        {
            InitializeComponent();
            _engine = engine;
            _settings = settings;
            _hotkeyManager = hotkeyManager;
            _engine.Settings = settings;
            _engine.WindowsTouched += OnWindowsTouched;
            _hotkeyManager.Pressed += HotkeyManager_Pressed;

            StartupToggle.IsChecked = IsStartupInstalled();

            // If the engine was already started (e.g. launched via --watch and then
            // the user opened the UI), reflect that state instead of assuming "off".
            WatchToggle.IsChecked = _engine.IsWatching;
            UpdateStatusVisuals(_engine.IsWatching, animate: false);

            // Apply the saved theme preference once resources are available (Application.Current
            // is set by this point since App.xaml.cs creates MainWindow after InitializeComponent).
            bool light = ThemeManager.ShouldUseLightPalette(_settings.ThemePreference);
            ThemeManager.Apply(light);

            // Listen for an external --stop command (e.g. from a terminal) so the
            // tray UI stays in sync if something else stops the watcher.
            _stopListener = StopSignal.Listen(() =>
            {
                Dispatcher.Invoke(() =>
                {
                    _engine.StopWatching();
                    WatchToggle.IsChecked = false;
                    UpdateStatusVisuals(false, animate: true);
                    FooterText.Text = "Stopped from another window";
                });
            });

            // SettingsView needs a real HWND for hotkey registration (RegisterHotKey requires
            // one), so build it after the window is loaded rather than in the constructor.
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _settingsView = new SettingsView(_settings, _engine, _hotkeyManager);
            _settingsView.SettingsChanged += () => { /* room for future reactive UI hooks */ };
            SettingsPageHost.Content = _settingsView;

            // Now that there's a real HWND, apply the saved hotkey setting if enabled.
            _settingsView.ApplyHotkeyRegistration();
        }

        private void HotkeyManager_Pressed()
        {
            // Always marshal to the UI thread - WM_HOTKEY arrives via the window's own
            // message pump, which is the UI thread here, but Dispatcher.Invoke from the
            // same thread is a safe no-op, so this is defensive rather than required.
            Dispatcher.Invoke(() =>
            {
                WatchToggle.IsChecked = !(WatchToggle.IsChecked == true);
            });
        }

        /// <summary>Refreshes the toggle/status visuals to match the engine's current state.
        /// Used when watching is started/stopped from somewhere other than this window
        /// (the tray menu, or an external --stop signal).</summary>
        public void SyncToEngineState()
        {
            bool watching = _engine.IsWatching;
            // Avoid re-triggering Checked/Unchecked handlers (which would call StartWatching/
            // StopWatching again) by only updating if the value actually changed.
            if (WatchToggle.IsChecked != watching)
            {
                WatchToggle.IsChecked = watching;
            }
            UpdateStatusVisuals(watching, animate: true);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void HideButton_Click(object sender, RoutedEventArgs e) => Hide();

        // ---------------- Tab switching ----------------

        private void HomeTabButton_Click(object sender, RoutedEventArgs e) => ShowHomePage();

        private void SettingsTabButton_Click(object sender, RoutedEventArgs e) => ShowSettingsPage();

        private void ShowHomePage()
        {
            HomeTabButton.IsChecked = true;
            SettingsTabButton.IsChecked = false;
            HomePage.Visibility = Visibility.Visible;
            SettingsPageHost.Visibility = Visibility.Collapsed;
        }

        private void ShowSettingsPage()
        {
            HomeTabButton.IsChecked = false;
            SettingsTabButton.IsChecked = true;
            HomePage.Visibility = Visibility.Collapsed;
            SettingsPageHost.Visibility = Visibility.Visible;
        }

        // ---------------- Watch toggle ----------------

        private void WatchToggle_Checked(object sender, RoutedEventArgs e)
        {
            _engine.StartWatching();
            UpdateStatusVisuals(true, animate: true);
            FooterText.Text = "";
            MaybeShowToast("NoBorder", "Watching for new windows");
        }

        private void WatchToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            _engine.StopWatching();
            UpdateStatusVisuals(false, animate: true);
            FooterText.Text = "";
            MaybeShowToast("NoBorder", "Stopped watching");
        }

        private void MaybeShowToast(string title, string message)
        {
            if (_settings.ShowToastNotifications && Application.Current is App app)
            {
                app.ShowTrayNotification(title, message);
            }
        }

        private void ApplyOnceButton_Click(object sender, RoutedEventArgs e)
        {
            int count = _engine.ApplyOnce();
            FooterText.Text = count == 1 ? "Fixed 1 window" : $"Fixed {count} windows";

            // Brief visual pulse even if watch mode is off, so the one-time action feels responsive.
            if (!_engine.IsWatching)
            {
                AnimateSeam(toVisible: false);
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (!_engine.IsWatching) AnimateSeam(toVisible: true);
                };
                timer.Start();
            }
        }

        private void StartupCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            InstallStartup();
            FooterText.Text = "Will start automatically at sign-in";
        }

        private void StartupCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            UninstallStartup();
            FooterText.Text = "Removed from startup";
        }

        private void OnWindowsTouched(int totalCount)
        {
            // Engine event arrives on the watcher thread; marshal to the UI thread.
            Dispatcher.Invoke(() =>
            {
                if (_engine.IsWatching)
                {
                    FooterText.Text = totalCount == 1 ? "Fixed 1 window so far" : $"Fixed {totalCount} windows so far";
                }
            });
        }

        private void UpdateStatusVisuals(bool watching, bool animate)
        {
            StatusText.Text = watching ? "Watching" : "Not watching";
            StatusDot.Fill = watching
                ? (System.Windows.Media.Brush)FindResource("AccentBrush")
                : (System.Windows.Media.Brush)FindResource("TextMutedBrush");

            if (animate)
            {
                AnimateSeam(toVisible: !watching);
            }
            else
            {
                Seam.Opacity = watching ? 0 : 1;
            }
        }

        private void AnimateSeam(bool toVisible)
        {
            var anim = new DoubleAnimation
            {
                To = toVisible ? 1.0 : 0.0,
                Duration = TimeSpan.FromMilliseconds(450),
                EasingFunction = new CubicEase { EasingMode = toVisible ? EasingMode.EaseIn : EasingMode.EaseOut }
            };
            Seam.BeginAnimation(System.Windows.Shapes.Rectangle.OpacityProperty, anim);
        }

        // ---- Startup registration (same mechanism as the CLI's --install-startup) ----

        private static bool IsStartupInstalled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(RunValueName) != null;
        }

        private void InstallStartup()
        {
            string exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
            // --tray-or-watch isn't a real flag; the actual decision of whether to show the
            // window happens inside App.xaml.cs based on AppSettings.ShowWindowOnStartup,
            // which is read at launch - so --watch here just means "start watching", and the
            // visibility choice is handled separately rather than baked into the command line.
            string command = $"\"{exePath}\" --watch";
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                             ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key.SetValue(RunValueName, command, RegistryValueKind.String);
        }

        private static void UninstallStartup()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }

        protected override void OnClosed(EventArgs e)
        {
            _stopListener?.Dispose();
            _hotkeyManager.Pressed -= HotkeyManager_Pressed;
            base.OnClosed(e);
        }
    }
}
