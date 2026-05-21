# Changelog

This package follows [Keep a Changelog](https://keepachangelog.com/) and
[SemVer](https://semver.org/).

## [0.2.1] - 2026-05-21

### Fixed
- Added `LICENSE.meta` so Unity stops emitting "no meta file, but it's in an
  immutable folder. The asset will be ignored." warnings when the package is
  imported as a UPM dependency. Unity requires a `.meta` file next to every
  asset inside an immutable package folder.

## [0.2.0] - 2026-05-21

### Added
- `LICENSE` (MIT) file at the package root.
- Polished GitHub-facing `README.md` with badges, feature list, "How it works"
  section.

### Changed
- Package name `com.pncsolution.editor-browser` → `com.lwt.editor-browser`.
- Author email `wtlee@pncsolution.co.kr` → `dldnjsxo95@gmail.com`.

## [0.1.0] - 2026-05-21 (initial public release)

### Added

**Core UI shell**
- `BrowserWindow` EditorWindow — toolbar (back/forward/refresh/URL),
  embedded body region, status bar.
- `UrlResolver` — pure function that turns Enter input into URL / domain /
  search query.
- `BrowserHistory` — back/forward navigation state.
- Menu item `Window > Editor Browser` and shortcut **Shift+Alt+W**.
- Default homepage `https://www.google.com/`.

**Chrome embedding**
- `BrowserDetector` — Chrome-first, Edge fallback (based on known install
  paths).
- `ExternalBrowserHost` — spawns Chrome / Edge as a separate process,
  attaches its main HWND as an owner-popup of Unity's main window, and
  synchronizes its position/size with the EditorWindow body region every
  frame.
  - `--app=<url>` flag to strip Chrome's tab/address/menu UI.
  - Win32 `SetWindowLong` to strip `WS_CAPTION`, `WS_THICKFRAME`,
    `WS_SYSMENU`, and other decorations.
  - Per-frame `body.worldBound` → screen pixels → `SetWindowPos` sync.
  - Drift gate skips redundant SetWindowPos when RECT is unchanged.
  - `AssemblyReloadEvents.beforeAssemblyReload` + `EditorApplication.quitting`
    cleanup hooks.
- CDP-based in-place navigation (`Page.navigate`) — URL change no longer
  restarts the Chrome process. ~100 ms typical navigate latency.
- ContainerWindow API reflection — `EditorWindow.position` is replaced by
  `Internal_GetTopleftScreenPosition()` + View tree `m_Position` accumulation
  to avoid false transient `(0, 26)` values during dock-system frame races.
- `WinEventHook` + background watchdog thread to keep Chrome positioned
  correctly even during the OS drag modal loop.
- URL Enter handling with `TrickleDown` + `NavigationSubmitEvent` so a single
  Enter submits (previously needed two Enters due to IME composition).
- `Native/Win32.cs` — user32 / kernel32 / gdi32 P/Invoke definitions
  (SetWindowPos, SetWinEventHook, CreateProcess with
  `CREATE_BREAKAWAY_FROM_JOB`, etc.).

### Notes
- Chrome user-data directory: `%LOCALAPPDATA%\EditorBrowser\BrowserProfile`
  (isolated from the host user's normal Chrome profile).
- Windows-only browser embedding — on other platforms the UI shell runs but
  the embedded browser is disabled.
- Known trade-offs documented in `README.md`:
  - ~7 px DWM invisible border margin around the page.
  - ~32 px Chrome PWA mini titlebar visible at top of body region.
