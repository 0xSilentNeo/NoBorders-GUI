using System;
using System.Drawing;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace NoBorder
{
    public partial class App : Application
    {
        private BorderRemovalEngine? _engine;
        private MainWindow? _mainWindow;
        private NotifyIcon? _trayIcon;
        private IDisposable? _showRequestListener;
        private AppSettings? _settings;
        private HotkeyManager? _hotkeyManager;

        // Fixed, namespaced name so this doesn't collide with unrelated apps.
        // "Local\" scopes it to the current login session, matching how almost all
        // single-instance desktop apps behave (a different user session can run their own).
        private const string SingleInstanceMutexName = @"Local\NoBorder_SingleInstance_8F2A1C77";
        private static Mutex? _singleInstanceMutex;

        private static void LogCrash(Exception ex)
        {
            try
            {
                string logPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "NoBorder", "crash.log");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath)!);
                System.IO.File.WriteAllText(logPath, $"{DateTime.Now}\n{ex}\n");
            }
            catch
            {
                // If even writing the log fails, there's nothing further to do here - the
                // caller still shows a message box, which doesn't depend on the filesystem.
            }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            // ---- Plain CLI commands: no UI, exit immediately. Useful for scripts/Task Scheduler. ----
            // These deliberately bypass single-instance enforcement below - --stop and --once
            // are meant to be run alongside an already-running instance, not blocked by it.
            if (args.Length > 0)
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "--stop":
                        Console.WriteLine(StopSignal.Send()
                            ? "Stop signal sent."
                            : "NoBorder isn't currently running.");
                        return;

                    case "--once":
                        using (var engine = new BorderRemovalEngine())
                        {
                            engine.Settings = AppSettings.Load();
                            int count = engine.ApplyOnce();
                            Console.WriteLine($"Applied fix to {count} window(s).");
                        }
                        return;

                    case "--help":
                    case "-h":
                    case "/?":
                        PrintUsage();
                        return;
                }
            }

            // ---- Single-instance guard for the UI/watch paths (no args, --watch, --tray). ----
            // CreateNew tells us whether *this* call created the mutex (true = we're first)
            // or it already existed (false = another instance is already running).
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                // Another instance is already running - ask it to show its window instead
                // of starting a second one (which is what was creating duplicate tray icons).
                StopSignal.SendShowWindow();
                return;
            }

            // ---- Everything else (no args, --watch, --tray, --ui) launches the WPF app. ----
            try
            {
                var app = new App();
                app.InitializeComponent();
                app.RunWithArgs(args);
            }
            catch (Exception ex)
            {
                // A WinExe with no attached console can fail completely silently on an
                // unhandled exception during startup - no window, no tray icon, nothing in
                // Task Manager by the time you check. Write the failure somewhere visible
                // instead of letting that happen again.
                LogCrash(ex);

                System.Windows.MessageBox.Show(
                    $"NoBorder failed to start:\n\n{ex.Message}\n\nDetails were saved to %AppData%\\NoBorder\\crash.log",
                    "NoBorder - Startup Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }

            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }

        private static void PrintUsage()
        {
            Console.WriteLine("NoBorder - removes the white DWM border around windows (e.g. when snapped)");
            Console.WriteLine();
            Console.WriteLine("  NoBorder.exe                  Open the control panel");
            Console.WriteLine("  NoBorder.exe --tray           Start minimized to the tray (used by autostart)");
            Console.WriteLine("  NoBorder.exe --watch          Start watching immediately, window hidden");
            Console.WriteLine("  NoBorder.exe --once           Apply the fix once to all open windows, then exit");
            Console.WriteLine("  NoBorder.exe --stop           Stop a currently running watcher (UI or background)");
        }

        private void RunWithArgs(string[] args)
        {
            this.DispatcherUnhandledException += (_, e) =>
            {
                LogCrash(e.Exception);
                System.Windows.MessageBox.Show(
                    $"NoBorder hit an unexpected error and needs to close:\n\n{e.Exception.Message}\n\nDetails were saved to %AppData%\\NoBorder\\crash.log",
                    "NoBorder - Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                e.Handled = true; // prevents the default "silently terminate" behavior where possible
            };

            _settings = AppSettings.Load();
            _engine = new BorderRemovalEngine { Settings = _settings };
            _hotkeyManager = new HotkeyManager();
            _mainWindow = new MainWindow(_engine, _settings, _hotkeyManager);

            SetupTrayIcon();

            // If a second NoBorder.exe gets launched while this one is already running,
            // Main() (see the single-instance guard above) redirects it into asking us to
            // show our window instead of starting its own - this is what we listen for.
            _showRequestListener = StopSignal.ListenForShowRequests(() =>
            {
                Dispatcher.Invoke(ShowMainWindow);
            });

            bool launchedViaAutostart = args.Length > 0 &&
                (string.Equals(args[0], "--tray", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(args[0], "--watch", StringComparison.OrdinalIgnoreCase));

            bool startWatching = args.Length > 0 &&
                string.Equals(args[0], "--watch", StringComparison.OrdinalIgnoreCase);

            if (startWatching)
            {
                _engine.StartWatching();
            }

            // Visibility on an autostart launch follows the user's saved preference rather
            // than always starting hidden - someone who wants to see the window immediately
            // at login shouldn't have to dig into the tray to find it.
            bool startHidden = launchedViaAutostart && !_settings.ShowWindowOnStartup;

            if (startHidden)
            {
                _mainWindow.WindowState = WindowState.Normal;
                _mainWindow.Hide();
            }
            else
            {
                _mainWindow.Show();
            }

            this.Run();

            _showRequestListener?.Dispose();
            _hotkeyManager?.Dispose();
            _trayIcon?.Dispose();
            _engine?.Dispose();
        }

        private void SetupTrayIcon()
        {
            _trayIcon = new NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Visible = true,
                Text = "NoBorder"
            };

            var menu = new ContextMenuStrip();
            var openItem = new ToolStripMenuItem("Open NoBorder");
            openItem.Click += (_, _) => Dispatcher.Invoke(ShowMainWindow);

            var toggleItem = new ToolStripMenuItem("Toggle watching");
            toggleItem.Click += (_, _) =>
            {
                if (_engine!.IsWatching) _engine.StopWatching();
                else _engine.StartWatching();

                // Defensive dispatch: tray events typically run on the same STA thread as
                // the WPF dispatcher here, but Dispatcher.Invoke from that same thread is a
                // safe no-op, so this costs nothing and protects against edge cases where
                // that assumption doesn't hold.
                _mainWindow?.Dispatcher.Invoke(SyncMainWindowToggle);
            };

            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (_, _) => Shutdown();

            menu.Items.Add(openItem);
            menu.Items.Add(toggleItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        }

        private void ShowMainWindow()
        {
            if (_mainWindow == null) return;
            _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }
            _mainWindow.Activate();
            _mainWindow.Topmost = true;  // brief nudge to the front...
            _mainWindow.Topmost = false; // ...then release, so it doesn't stay pinned above other windows
        }

        private void SyncMainWindowToggle()
        {
            // Reflect tray-menu toggle changes in the window if it's open.
            // MainWindow listens to the engine's own state on next Show(), but
            // if it's currently visible we nudge it directly via its public toggle.
            if (_mainWindow != null && _mainWindow.IsVisible)
            {
                _mainWindow.SyncToEngineState();
            }
        }

        /// <summary>Shows a tray balloon notification. Safe to call even if the tray icon
        /// hasn't been set up yet (no-ops in that case) - callers don't need to check.</summary>
        public void ShowTrayNotification(string title, string message)
        {
            if (_trayIcon == null) return;
            _trayIcon.BalloonTipTitle = title;
            _trayIcon.BalloonTipText = message;
            _trayIcon.ShowBalloonTip(3000);
        }

        private static Icon LoadTrayIcon()
        {
            // Same .ico that's embedded as the exe's icon and used for the window/taskbar
            // icon, so the tray, taskbar, and Task Manager all show the identical artwork
            // instead of three different representations of "NoBorder".
            var uri = new Uri("pack://application:,,,/Resources/icon.ico");
            var streamInfo = System.Windows.Application.GetResourceStream(uri);
            if (streamInfo == null)
            {
                // Extremely unlikely (would mean the resource failed to embed at build time),
                // but fall back to a system icon rather than crash if it ever happens.
                return SystemIcons.Application;
            }

            using var stream = streamInfo.Stream;
            try
            {
                // Explicitly ask for the 16x16 frame - System.Drawing.Icon's default frame
                // selection from a multi-size, PNG-backed .ico isn't always reliable, so name
                // the size we actually want for the tray instead of letting it guess.
                return new Icon(stream, 16, 16);
            }
            catch
            {
                // If that frame selection ever fails for some reason, fall back to whatever
                // the default constructor picks rather than crashing the app over an icon.
                stream.Position = 0;
                return new Icon(stream);
            }
        }
    }
}
