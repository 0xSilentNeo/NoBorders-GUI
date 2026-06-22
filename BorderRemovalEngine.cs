using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NoBorder
{
    /// <summary>
    /// Core engine: finds top-level windows and applies (or clears) the DWM border color
    /// on them, honoring exclusions and the configured color mode. Contains no UI code so
    /// it can be driven from a tray app, a console, or anything else.
    /// </summary>
    public sealed class BorderRemovalEngine : IDisposable
    {
        private const int DWMWA_BORDER_COLOR = 34;
        private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
        private const uint GA_ROOT = 2;

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
        private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WM_QUIT = 0x0012;

        private WinEventDelegate? _hookDelegate;
        private Thread? _watchThread;
        private uint _watchThreadId;
        private volatile bool _isWatching;

        public bool IsWatching => _isWatching;

        /// <summary>Raised on the calling thread whenever the watcher applies the fix to a window. Useful for UI status/log.</summary>
        public event Action<int>? WindowsTouched;

        private int _windowsTouchedThisRun;

        /// <summary>Live settings driving color mode and exclusions. The UI can mutate this
        /// object's properties directly (e.g. when the user flips a toggle); changes take
        /// effect on the next window touched, with no restart needed.</summary>
        public AppSettings Settings { get; set; } = new();

        /// <summary>Applies the configured border treatment (none, or a custom color) to a
        /// single window, unless that window is excluded.</summary>
        public void ApplyToWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            if (IsExcluded(hwnd)) return;

            uint value = GetConfiguredColorValue();
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref value, sizeof(uint));
        }

        /// <summary>Restores the default border color (removes our override) on a single
        /// window. Used when an app is excluded so a previously-touched window goes back
        /// to looking normal instead of staying stuck on our last setting.</summary>
        public static void ResetWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            // DWMWA_COLOR_DEFAULT restores the normal system-drawn border, as opposed to
            // DWMWA_COLOR_NONE which actively suppresses it.
            uint colorDefault = 0xFFFFFFFF;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorDefault, sizeof(uint));
        }

        private uint GetConfiguredColorValue()
        {
            if (!Settings.UseCustomColor)
            {
                return DWMWA_COLOR_NONE;
            }

            // DWMWA_BORDER_COLOR expects 0x00BBGGRR (no alpha), not the 0xAARRGGBB our
            // settings store the color as (matching .NET Color/WPF Color conventions), so
            // the channels need reordering here.
            if (TryParseColorHex(Settings.AccentColorHex, out byte r, out byte g, out byte b))
            {
                return (uint)(b << 16 | g << 8 | r);
            }
            return DWMWA_COLOR_NONE; // fall back safely if the stored color string is malformed
        }

        private static bool TryParseColorHex(string hex, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            hex = hex.TrimStart('#');
            try
            {
                // Accept either AARRGGBB (8 chars) or RRGGBB (6 chars).
                if (hex.Length == 8)
                {
                    r = Convert.ToByte(hex.Substring(2, 2), 16);
                    g = Convert.ToByte(hex.Substring(4, 2), 16);
                    b = Convert.ToByte(hex.Substring(6, 2), 16);
                    return true;
                }
                if (hex.Length == 6)
                {
                    r = Convert.ToByte(hex.Substring(0, 2), 16);
                    g = Convert.ToByte(hex.Substring(2, 2), 16);
                    b = Convert.ToByte(hex.Substring(4, 2), 16);
                    return true;
                }
            }
            catch (FormatException)
            {
                // malformed hex string - handled by the false return below
            }
            return false;
        }

        private static bool IsRealTopLevelWindow(IntPtr hwnd)
        {
            if (!IsWindowVisible(hwnd)) return false;
            if (GetAncestor(hwnd, GA_ROOT) != hwnd) return false;
            if (GetWindowTextLength(hwnd) == 0) return false;
            return true;
        }

        /// <summary>Returns the window's title text, or "" if it has none / can't be read.</summary>
        public static string GetWindowTitle(IntPtr hwnd)
        {
            int length = GetWindowTextLength(hwnd);
            if (length == 0) return "";
            var sb = new StringBuilder(length + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>Returns the owning process's name (without .exe), or "" if it can't be
        /// determined (e.g. the process has already exited, or access is denied).</summary>
        public static string GetWindowProcessName(IntPtr hwnd)
        {
            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return "";
                using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                return process.ProcessName;
            }
            catch
            {
                // Process may have exited between the call and lookup, or be inaccessible
                // (e.g. a protected system process) - either way, just treat as unknown.
                return "";
            }
        }

        /// <summary>A lightweight snapshot of one open top-level window, used by the
        /// exclusion picker UI. Deliberately doesn't expose the raw HWND, since by the time
        /// the UI acts on it (the user clicks a list item) the window may have closed.</summary>
        public readonly record struct OpenWindowInfo(string Title, string ProcessName);

        /// <summary>Snapshots all current real top-level windows, for populating an
        /// "exclude this app" picker. Excludes NoBorder's own window from the list.</summary>
        public static System.Collections.Generic.List<OpenWindowInfo> ListOpenWindows()
        {
            var results = new System.Collections.Generic.List<OpenWindowInfo>();
            uint currentPid = (uint)Environment.ProcessId;

            EnumWindows((hwnd, _) =>
            {
                if (IsRealTopLevelWindow(hwnd))
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid == currentPid) return true; // skip our own window

                    string title = GetWindowTitle(hwnd);
                    string processName = GetWindowProcessName(hwnd);
                    if (title.Length > 0)
                    {
                        results.Add(new OpenWindowInfo(title, processName));
                    }
                }
                return true;
            }, IntPtr.Zero);

            return results;
        }

        private bool IsExcluded(IntPtr hwnd)
        {
            if (Settings.ExcludedApps.Count == 0) return false;

            string processName = GetWindowProcessName(hwnd);
            string title = GetWindowTitle(hwnd);

            foreach (var pattern in Settings.ExcludedApps)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                if (processName.Length > 0 && processName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (title.Length > 0 && title.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Applies the fix once to every current top-level window. Returns how many were touched.</summary>
        public int ApplyOnce()
        {
            int count = 0;
            EnumWindows((hwnd, _) =>
            {
                if (IsRealTopLevelWindow(hwnd))
                {
                    if (IsExcluded(hwnd))
                    {
                        ResetWindow(hwnd);
                    }
                    else
                    {
                        ApplyToWindow(hwnd);
                        count++;
                    }
                }
                return true;
            }, IntPtr.Zero);
            return count;
        }

        /// <summary>Starts the background watcher on a dedicated thread. No-op if already watching.</summary>
        public void StartWatching()
        {
            if (_isWatching) return;
            _isWatching = true;
            _windowsTouchedThisRun = ApplyOnce();
            WindowsTouched?.Invoke(_windowsTouchedThisRun);

            _watchThread = new Thread(WatchThreadProc)
            {
                IsBackground = true,
                Name = "NoBorderWatcher"
            };
            _watchThread.Start();
        }

        /// <summary>Stops the background watcher. No-op if not watching. Does not block the
        /// calling thread - the watcher thread unhooks and exits on its own shortly after.</summary>
        public void StopWatching()
        {
            if (!_isWatching) return;
            _isWatching = false;

            if (_watchThreadId != 0)
            {
                // Post WM_QUIT to break the message loop on the watcher thread.
                // Deliberately not Join()-ing here: if the watcher thread is mid-callback
                // and that callback raises WindowsTouched (which subscribers may handle via
                // Dispatcher.Invoke back onto this very thread), blocking this thread with
                // Join would deadlock against that Invoke. Letting the watcher thread finish
                // asynchronously avoids that, at the cost of StopWatching returning slightly
                // before the thread has fully exited (it's a background thread, so this is safe).
                PostThreadMessage(_watchThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            _watchThread = null;
        }

        /// <summary>Re-applies the fix to all current windows using the current Settings.
        /// Call after the user changes color mode/exclusions while watching is already on,
        /// so the change is visible immediately rather than waiting for the next window event.</summary>
        public int ReapplyAll() => ApplyOnce();

        private void WatchThreadProc()
        {
            try
            {
                _watchThreadId = GetCurrentThreadId();
                WinEventDelegate hookDelegate = WinEventCallback;
                _hookDelegate = hookDelegate; // keep a reference alive on the instance too, for GC safety

                IntPtr[] hooks =
                {
                    SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, hookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT),
                    SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, hookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT),
                    SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, hookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT),
                    SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, hookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT),
                    SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, hookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT),
                };

                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) != 0)
                {
                    if (msg.message == WM_QUIT) break;
                }

                // Local array, not the shared field - a fast stop/start cycle that spins up a
                // new thread can't stomp on this thread's own hooks mid-cleanup, since each
                // thread only ever unhooks the array it itself created.
                foreach (var h in hooks)
                {
                    if (h != IntPtr.Zero) UnhookWinEvent(h);
                }
            }
            catch
            {
                // A background thread (IsBackground = true) that throws unhandled will
                // tear down the entire process with no dialog and no chance to recover -
                // exactly the "app just silently closed" failure mode this guards against.
                // Swallowing here means at worst watching silently stops; the rest of the
                // app (tray icon, window) keeps running so the user isn't left guessing.
                _isWatching = false;
            }
        }

        private void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (!_isWatching) return; // shutting down - don't do work or raise events anymore
            if (idObject != 0 || hwnd == IntPtr.Zero) return;
            if (!IsRealTopLevelWindow(hwnd)) return;

            if (IsExcluded(hwnd))
            {
                ResetWindow(hwnd);
                return;
            }

            ApplyToWindow(hwnd);
            _windowsTouchedThisRun++;
            WindowsTouched?.Invoke(_windowsTouchedThisRun);
        }

        /// <summary>Checks whether the current Windows build actually supports
        /// DWMWA_BORDER_COLOR (added in Windows 11 22H2 / build 22621). On older builds the
        /// DWM call silently no-ops, so the UI can use this to surface a warning instead of
        /// leaving the user wondering why nothing happens.</summary>
        public static bool IsBorderColorSupported()
        {
            // Environment.OSVersion reports the Win32 build number reliably on Windows 10/11
            // when the app manifest declares Windows 10/11 compatibility (which app.manifest
            // does not currently restrict, so this reads the true build number).
            return Environment.OSVersion.Version.Build >= 22621;
        }

        public void Dispose()
        {
            StopWatching();
        }
    }
}
