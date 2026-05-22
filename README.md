# Editor Browser

[![Unity](https://img.shields.io/badge/Unity-2022.3%2B-black?logo=unity)](https://unity.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-blue)](#requirements)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![UPM](https://img.shields.io/badge/UPM-Git%20URL-orange)](#installation)

A web browser that lives inside the Unity Editor as a regular dockable Tab —
the same way `Inspector`, `Hierarchy`, or `Console` does. No need to leave the
editor to look up docs, browse the Asset Store, or check internal tools.

## Features

- **Real dockable EditorWindow** — dock, undock, drag between docks, resize.
  Behaves exactly like any built-in Unity window.
- **Chrome-first, Edge fallback** — uses installed Chrome (PWA `--app=` mode)
  if available; falls back to Edge on Windows 11.
- **Fast in-place navigation** — URL changes use Chrome DevTools Protocol
  (`Page.navigate`) for near-instant page swap without spawning a new process.
- **Address bar with smart resolve** — typed text becomes a URL, a domain
  (auto-prefixed with `https://`), or a Google search.
- **Single-folder install / uninstall** — drop one UPM package in, delete one
  folder out. No global state, no leftover EditorPrefs.

## Installation

Add this line to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.lwt.editor-browser": "https://github.com/dldnjsxo95/UnityEditorBrowser.git#v0.4.9"
  }
}
```

The `#v0.4.9` tag locks the version. Omit it to always follow `main`.

Unity Package Manager fetches and compiles the package automatically. The
menu item `Window > Editor Browser` and the shortcut `Shift + Alt + W`
become available immediately.

### Alternative: local file dependency (during development)

```json
{
  "dependencies": {
    "com.lwt.editor-browser": "file:../EditorBrowser/Packages/com.lwt.editor-browser"
  }
}
```

### Optional: Claude Code `/eb` slash command

If you use [Claude Code](https://claude.com/claude-code) with the
[UnityMCP](https://github.com/CoplayDev/unity-mcp) bridge, the package
ships a `/eb <natural-language>` slash command that drives the browser
via the `editor_browser` MCP tool (e.g. `/eb open naver and search 맛집`).

**Install (recommended, since v0.4.9):**

In Unity, run `Window > Editor Browser Setup > Install eb Slash Command…`
and pick either **User-Global** (`%USERPROFILE%\.claude\commands\eb.md`,
available in every Claude Code session) or **Current Project**
(`<project>/.claude/commands/eb.md`). Restart Claude Code afterwards to
register `/eb`.

**Manual install** (if you prefer not to use the Editor menu):

Copy the bundled
`Packages/com.lwt.editor-browser/Editor/Claude/eb.md` (UPM install) or
`.claude/commands/eb.md` (repo checkout) into either
`<project>/.claude/commands/` or `%USERPROFILE%\.claude\commands\`
(`~/.claude/commands/` on Linux/macOS). No editing required since v0.4.8
— the file resolves your project path via `pwd` and discovers the latest
Unity 6 install under `Hub/Editor/`.

When multiple Unity instances are open at once, the skill prefers the
instance whose project path matches your current working directory and
will ask once if there is genuine ambiguity.

## Usage

- **Open**: `Window > Editor Browser`, or press `Shift + Alt + W`.
- **Default homepage**: `https://www.google.com/`.
- **Address bar**: type and press `Enter`.
  - Valid URL → direct navigation
  - Bare domain (`example.com`) → auto-prefixed with `https://`
  - Anything else → Google search
- **Back / Forward / Refresh** buttons work like any browser.

## Uninstallation

Remove the dependency line from `Packages/manifest.json` (or delete the
embedded `Packages/com.lwt.editor-browser/` folder). The following are cleared
automatically:

- Menu items and shortcuts
- The EditorWindow itself
- `EditorPrefs` / `SessionState` keys — none used to begin with (zero leftover)

The only manual cleanup left is the Chrome user-data directory at
`%LOCALAPPDATA%\EditorBrowser\BrowserProfile\` (per-user cookies, history,
cache). Delete it if you want a full wipe.

## Requirements

- Unity 2022.3 or newer (Unity 6 recommended)
- Windows 10 / 11 (browser embedding is Windows-only at this stage; the UI
  shell itself is cross-platform)
- Google Chrome **or** Microsoft Edge installed

## How it works (brief)

The package spawns Chrome as a **separate process** with `--app=<url>` and
attaches its main window as an **owner-popup** of Unity's main HWND, then
synchronizes its position/size every frame to match the body region of the
EditorWindow Tab. URL changes are sent over Chrome DevTools Protocol
(`Page.navigate`), so the same Chrome process serves multiple navigations
without restart.

Known trade-offs of the owner-popup model:
- A ~7px DWM invisible border margin around the page
- A ~32px Chrome PWA mini titlebar at the top
- Window manager edge cases during Tab drag, foreground swap, etc., which the
  package mitigates with `WinEventHook`, a background watchdog thread, and
  reflection-based `ContainerWindow` coordinate tracking.

For full architectural details, see commit history and inline comments in
`Editor/ExternalBrowserHost.cs`.

## License

[MIT](LICENSE) © 2026 LEEWONTAE
