# NoBorder

Removes the white DWM border that appears around windows on Windows 11
(most noticeable when snapping windows side by side), using the documented
`DWMWA_BORDER_COLOR` window attribute. Has grown from a one-shot fix into
a small tray-resident app with a Settings page for color mode, app
exclusions, a global hotkey, theme, and notifications.

## Requirements

- Windows 11 22H2 or later (`DWMWA_BORDER_COLOR` was added then; the
  Settings page shows a warning banner if your build is older).
- [.NET 8 SDK](https://dotnet.microsoft.com/download) to build it.

## Build

```
dotnet build -c Release
```

Output: `bin\Release\net8.0-windows\NoBorder.exe`. Copy that single .exe
anywhere you like.

## Using the app

Double-click `NoBorder.exe` (or pin a shortcut) to open the control panel.
It has two tabs:

### Home
- **Watch for new windows** — live background watcher; keeps the fix
  applied as you open, move, snap, minimize, or restore windows.
- **Fix open windows once** — applies the fix immediately without turning
  on the watcher.
- **Start automatically on startup** — registers per-user autostart (no
  admin rights needed).

### Settings
- **Border** — choose **No border** (the original behavior) or **Custom
  color**, with six preset swatches plus a full custom color picker (a
  saturation/lightness square, hue slider, hex, and RGB inputs, styled to
  match the rest of the app rather than the old Windows color dialog).
- **Excluded apps** — type a process name or window title to skip it
  entirely, or click **Pick from open windows...** to choose from what's
  currently running. Matching is case-insensitive "contains" against
  either the process name or the window title.
- **Global hotkey** — toggle watching from anywhere with a key combo.
  Click the button showing the current combo, then press your preferred
  keys (must include at least one modifier). Off by default.
- **Startup** — whether an autostart launch shows the window or stays
  tray-only.
- **Appearance** — Match Windows / Dark / Light. "Match Windows" follows
  your system's app theme (Settings > Personalization > Colors) and
  re-applies automatically each time you open the window.
- **Notifications** — toggles a small tray balloon when watching starts
  or stops.

All settings persist to `%AppData%\NoBorder\settings.json` and take effect
immediately — no restart needed, including while watching is already on.

Right-click the tray icon for **Open**, **Toggle watching**, or **Exit**.
Launching `NoBorder.exe` again while it's already running brings the
existing window forward instead of starting a second copy.

The same artwork (two boxes with a glowing seam) is used for the exe icon,
window icon, and tray icon, generated as a multi-resolution `.ico` under
`Resources/icon.ico`.

## Command line (for scripting / Task Scheduler)

| Command | What it does |
|---|---|
| `NoBorder.exe` | Opens the control panel. |
| `NoBorder.exe --watch` | Starts watching immediately. Window visibility follows the "show window on startup" setting. This is what autostart registers. |
| `NoBorder.exe --once` | Applies the fix once to all currently open windows (honoring saved color mode and exclusions), then exits. No UI at all. |
| `NoBorder.exe --stop` | Signals a currently running watcher to stop cleanly, whether it's the UI or a bare `--watch` process. |
| `NoBorder.exe --help` | Lists these commands. |

## How it works

- **Border removal**: enumerates visible top-level windows and calls
  `DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ...)` on each, using
  either `DWMWA_COLOR_NONE` or a packed `0x00BBGGRR` custom color
  depending on settings (`BorderRemovalEngine.cs`).
- **Watching**: `SetWinEventHook` listens system-wide for foreground
  changes, move/resize (covers snap), minimize/restore, and window-show
  events, reapplying the fix to whichever window triggered the event.
- **Exclusions**: each window is checked against the saved patterns (via
  process name and title) before the fix is applied; excluded windows are
  explicitly reset to the system default border instead of being skipped
  silently, so a window that's already been touched doesn't stay stuck.
- **Global hotkey**: a single `RegisterHotKey`/`WM_HOTKEY` registration
  (`HotkeyManager.cs`), attached to the main window's message loop.
- **Theme**: reads `HKCU\...\Themes\Personalize\AppsUseLightTheme` for
  "Match Windows", and swaps the app's `SolidColorBrush` resources in
  place (`ThemeManager.cs`) — no window reload needed.
- **Notifications**: `NotifyIcon.ShowBalloonTip` — the classic WinForms
  tray balloon API, which still renders through Windows 11's modern
  notification system despite the old name, with no extra dependencies.
- **Single instance / cross-process signals**: a named `Mutex` blocks a
  second launch from starting its own tray icon; named `EventWaitHandle`s
  (`StopSignal.cs`) let any process ask a running instance to stop or
  come to the foreground.
- **Settings page scrolling**: a custom `ScrollBar`/`ScrollViewer` style
  (`App.xaml`) replaces the default Win32-chrome scrollbar with a thin
  floating thumb, matching the rest of the app instead of standing out as
  OS-default chrome.
- **Color picker**: `ColorPickerDialog.xaml`/`.cs` is a small custom WPF
  dialog (HSV math, draggable saturation/lightness square, hue slider,
  hex/RGB inputs) replacing the old `System.Windows.Forms.ColorDialog`,
  so the picker looks like part of NoBorder instead of a Windows 7 relic.
- Settings persist as plain JSON at `%AppData%\NoBorder\settings.json`
  (`AppSettings.cs`) — no registry usage except the startup `Run` key.

## Uninstall / revert

- Untick **Start automatically on startup** to stop autostart.
- Quit via the tray icon's **Exit**, or `NoBorder.exe --stop` if running
  hidden.
- Delete `%AppData%\NoBorder\settings.json` to reset all settings to
  defaults.
- Borders return to normal automatically once the app stops — there's no
  persistent system modification beyond the optional startup entry.
