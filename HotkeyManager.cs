using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NoBorder
{
    /// <summary>
    /// Registers a single global hotkey via the Win32 RegisterHotKey API and raises
    /// Pressed whenever it's triggered, regardless of which app has focus. Needs a window
    /// handle to attach to (Win32 hotkeys are delivered as WM_HOTKEY messages to a specific
    /// HWND's message queue), so this hooks into MainWindow's message loop via HwndSource.
    /// </summary>
    public sealed class HotkeyManager : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;
        private const int HotkeyId = 0xB07D; // arbitrary fixed id, unique within our own process

        private HwndSource? _source;
        private bool _registered;

        public event Action? Pressed;

        /// <summary>Attaches to the given window and registers the hotkey. Call again (it will
        /// unregister first) if the key/modifier combination changes while still attached.</summary>
        public bool Register(Window window, uint virtualKey, uint modifiers)
        {
            Unregister();

            var helper = new WindowInteropHelper(window);
            IntPtr hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero)
            {
                // Window hasn't been shown yet (no HWND assigned). RegisterHotKey needs a
                // real window handle, so the caller must call this after the window is loaded
                // (e.g. in OnSourceInitialized or after Show()), not in the constructor.
                return false;
            }

            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(WndProc);

            _registered = RegisterHotKey(hwnd, HotkeyId, modifiers, virtualKey);
            return _registered;
        }

        public void Unregister()
        {
            if (_source != null)
            {
                if (_registered)
                {
                    UnregisterHotKey(_source.Handle, HotkeyId);
                }
                _source.RemoveHook(WndProc);
                _source = null;
            }
            _registered = false;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                Pressed?.Invoke();
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose() => Unregister();
    }
}
