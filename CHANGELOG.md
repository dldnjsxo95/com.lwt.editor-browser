# Changelog

This package follows [Keep a Changelog](https://keepachangelog.com/) and
[SemVer](https://semver.org/).

## [0.4.6] - 2026-05-22

### Added (Step 4a — handoff)
- `ExternalBrowserHost.NavigateAsync(url)` — public async API that runs
  `Page.navigate` via the `EditorBrowser.Automation` CDP stack. Replaces
  the old static `CdpNavigate` (hardcoded `Page.navigate` over a one-shot
  `ClientWebSocket`). Caches the page-target WebSocket URL after the
  first `/json/list` query and reuses it across calls.
- `ExternalBrowserHost.EvaluateAsync(expression)` — returns the raw CDP
  JSON for `Runtime.evaluate` (with `returnByValue: true`).
- `ExternalBrowserHost.CaptureScreenshotAsync()` — sends
  `Page.captureScreenshot`, decodes the base64 PNG, returns `byte[]`.
- `BrowserWindow.GetActiveHost()` — public static accessor returning the
  host of the first open BrowserWindow. Used by the optional MCP layer.

### Added (Step 4b — optional MCP wiring)
- New `EditorBrowser.Mcp` assembly with `EditorBrowserMcpTool` exposing a
  single `editor_browser` MCP sub-tool to Claude / MCP clients. Actions:
  `navigate (url)`, `evaluate (expression)`, `screenshot`. Auto-discovered
  by UnityMCP's `CommandRegistry` via `[McpForUnityTool]` attribute.
  Returns plain anonymous objects via Newtonsoft.Json.
- `HandleCommand` is `async Task<object>` — `CommandRegistry` routes it
  through the async path, so CDP roundtrips run on a worker without ever
  blocking Unity's main thread.
- Schema parameters declared via a nested `Parameters` class with
  `[ToolParameter]` properties (the format `ToolDiscoveryService`
  consumes); shows up in the tool's JSON schema once the MCP client
  reconnects.

### Changed
- `EditorBrowser.Editor` asmdef now references `EditorBrowser.Automation`
  (host code uses `Page` / `Runtime` from the CDP library).
- `EditorBrowser.Automation` asmdef no longer references
  `EditorBrowser.Editor` — kept as a pure library with zero external
  dependencies. Cleans up the previous circular-reference risk.
- `ExternalBrowserHost` is now `public sealed class` (was `internal`) so
  the optional MCP assembly can reach its automation surface.

### Conditional compilation
- `Editor/Mcp/EditorBrowser.Mcp.asmdef` carries
  `defineConstraints: ["EDITORBROWSER_HAS_UNITY_MCP"]` plus
  `versionDefines` on `com.coplaydev.unity-mcp`. Without UnityMCP the
  `EditorBrowser.Mcp.dll` is **not built at all** — the browser package
  keeps working as a standalone Editor tool with zero MCP dependency
  and zero broken references.
- This satisfies the package portability rule (no external dependencies)
  while still auto-integrating with UnityMCP when present.

### Verified
- All four EditorBrowser.* assemblies build: `Editor`, `Automation`,
  `Mcp`, plus the host's `MCPAutoStart`.
- `ToolDiscoveryService.GetToolMetadata("editor_browser")` returns the
  full schema: 3 typed parameters with descriptions.
- `CommandRegistry` registers the handler as **async** (return type
  `Task<object>`).
- End-to-end via `host.NavigateAsync` on a thread pool: Chrome spawns,
  attaches, navigates to a real URL, CDP roundtrip succeeds.

### Known limitation
- Calling `HandleCommand` from Unity's main thread via
  `Task.Wait()` / `.GetAwaiter().GetResult()` deadlocks even with
  `ConfigureAwait(false)` everywhere — likely because Unity's
  `SynchronizationContext` or the Mono `ClientWebSocket` does not honor
  the false on every continuation. UnityMCP's actual dispatch path uses
  `await`, not `.Wait()`, so this does not affect MCP clients in
  practice — but any test that spawns the tool from the main thread
  must wrap the call in `Task.Run` to avoid the deadlock.

## [0.4.5] - 2026-05-22

### Added
- `EditorBrowser.Automation.Protocol.Page` — CDP `Page` domain wrapper
  over a `CdpSession`. Methods: `EnableAsync` (turn on Page events),
  `NavigateAsync(url)` (sends `Page.navigate` — no load-wait), and
  `CaptureScreenshotAsync` (returns raw JSON; PNG bytes are base64-
  encoded under `result.data` for caller to decode).
- `EditorBrowser.Automation.Protocol.Runtime` — CDP `Runtime` domain
  wrapper. Methods: `EnableAsync` (turn on Runtime events) and
  `EvaluateAsync(expression)` (sends `Runtime.evaluate` with
  `returnByValue:true` so the result is JSON-serialized).
- New subfolder `Editor/Automation/Protocol/` for future domain wrappers
  (Input, DOM, Network are v2 candidates).

No callers yet — `ExternalBrowserHost.CdpNavigate` keeps its inline
implementation. The Protocol layer rides dormant in
`EditorBrowser.Automation.dll` until the final integration option wires
it up.

### Notes
- Both `Page.cs` and `Runtime.cs` ship as separate files (unlike v0.4.4's
  combined `CdpConnection.cs`). The Unity 6 CompilationPipeline stale-
  cache that hit us last release did not recur — likely because the
  files live in a new subfolder (`Protocol/`) the Editor had not seen
  before, so it indexed them fresh.

## [0.4.4] - 2026-05-22

### Added
- `EditorBrowser.Automation.CdpConnection` — single-WebSocket CDP transport.
  Auto-incrementing message ids, send-await-response via
  `TaskCompletionSource`, event dispatch by method name, graceful close,
  thread-safe send via async semaphore, background receive loop with
  pending-TCS fail-on-close cleanup. Rewrites `ws://localhost:` to
  `ws://127.0.0.1:` to dodge the IPv6 DNS fallback that adds ~2s on .NET.
- `EditorBrowser.Automation.CdpSession` — thin logical wrapper over
  `CdpConnection` with an optional `sessionId`. Null sessionId targets
  the per-target WS endpoint directly (current default); a set sessionId
  prefixes every command with `"sessionId":"..."` for the future
  browser-WS / pipe multiplexing path.

No callers yet — the existing `ExternalBrowserHost.CdpNavigate` keeps
its inline implementation. These library classes are the foundation that
future Protocol/Page, Protocol/Runtime, and McpTools layers will build on.

### Notes
- `CdpConnection` and `CdpSession` currently live in the same source file
  (`CdpConnection.cs`) because this Unity Editor session held a stale
  CompilationPipeline cache that refused to recognize a sibling .cs file
  in the same asmdef. Splitting into two files is purely organizational
  and can be done in a future Editor session.

## [0.4.3] - 2026-05-22

### Added
- Empty `EditorBrowser.Automation` asmdef scaffold at
  `Editor/Automation/EditorBrowser.Automation.asmdef`. Editor-only,
  `rootNamespace=EditorBrowser.Automation`, references `EditorBrowser.Editor`
  (so future code can reach `ExternalBrowserHost`). No `.cs` files yet, so
  Unity does not build a DLL — this is purely an assembly boundary marker
  that future CDP automation code (`CdpConnection`, `CdpSession`,
  `Protocol/Page`, `Protocol/Runtime`, `McpTools`) will live in. No-op
  for current consumers.

## [0.4.2] - 2026-05-22

### Changed
- Replaced the hardcoded `--remote-debugging-port=9222` with
  `--remote-debugging-port=0`. The OS picks a free port at Chrome startup,
  eliminating conflicts with any unrelated Chrome / debugger instance the
  user may have on 9222.
- Chrome writes the chosen port to `<user-data-dir>/DevToolsActivePort`
  immediately after the remote-debugging listener binds. The host now
  discovers the port from this file:
  - `TryAttach` does an opportunistic single-shot read at the end of a
    successful attach (Chrome has been alive ≥1.2s by then, so the file
    is almost always present).
  - If still 0 at the next `Navigate`, the background Task retries via
    `WaitForDevToolsPort` (100ms × 20, ≤2s budget) so the main thread is
    never blocked.
- `CdpNavigate` refactored to accept the discovered port as a parameter
  rather than hardcoding `127.0.0.1:9222`.
- `_discoveredDevToolsPort` resets on `Start` and `DisposeProcess` so a
  stale value from a prior Chrome session can never leak into the next.

### Verified
- Spawned Chrome with `--remote-debugging-port=0`, observed
  `DevToolsActivePort` populated with `8311\n/devtools/browser/<uuid>`.
- `_discoveredDevToolsPort` populated correctly after `TryAttach`.
- End-to-end CDP navigation works: `Navigate("https://example.com/")`
  swapped the page via the discovered port (confirmed by direct
  `/json/list` query showing the new URL).

## [0.4.1] - 2026-05-22

### Changed
- Removed the remaining nine `Debug.LogError` calls that v0.4.0 had kept
  (browser-not-detected, `CreateProcess` failures x2, reflection-cache
  failures x6). Verification of the dock/undock cycle with programmatic
  measurement showed they were not load-bearing: failure modes are either
  visible directly to the user (browser not appearing) or guarded by
  `s_reflectionFailed` (one-shot, harmless if silenced). Net result: the
  package produces zero console output under all normal and degraded paths.
- Also removed the now-unused `LogPrefix` constants and the
  `using Debug = UnityEngine.Debug;` alias from ExternalBrowserHost.cs.
- Outdated CreateProcess fallback comment refreshed to match the
  silent-fail behavior.

### Verified
- Programmatic dock (via `DockArea.AddTab` reflection) → snapshot →
  programmatic undock → snapshot. Floating-before and floating-after
  computed absRect are bit-identical: `(1314, 566) 673x604`. Chrome
  HWND actual `GetWindowRect` matches the computed value exactly.
- User-perceived "margin variation" across dock/undock is Unity's own
  2px tab-strip height difference between floating (26px) and docked
  (24px) hosts, and the View-tree depth/offset change (2 levels at
  acc=(0,0) vs 4 levels at acc=(0,36)). Both are expected.

## [0.4.0] - 2026-05-22

### Changed
- Stripped diagnostic scaffolding from the package: removed eight
  troubleshooting menu items (`Editor Browser Test Move/Resize/Drag Sim`,
  `Toggle Sync Trace`, `Dump Sync Ring`, `Reset Sync Ring`,
  `Reset Chrome Ring`, `Diagnostics`), the sync-ring + chrome-ring buffers,
  trace counters, `RecordSyncEntry`, `DumpTraceIfDue`, `BuildSyncRingDump`,
  `BuildChromeRingDump`, and `DumpDiagnostics`. Net −289 lines.
- Kept nine `Debug.LogError` calls for user-visible failures only:
  browser-not-detected, `CreateProcess` failures (both paths), and the
  six reflection-cache breakages (`s_reflectionFailed` ensures each fires
  at most once per session).
- Watchdog enforce path (`SetWindowPos` from the background thread when
  the browser HWND drifts) is preserved — only its diagnostic logging
  block was removed.

## [0.3.0] - 2026-05-22

### Changed
- Code-wide pass: translated all source comments and XML docs to English,
  removed redundant comments that only described what the code does, and
  trimmed stale references to historical fixes. Behavior unchanged.

## [0.2.4] - 2026-05-22

### Changed
- GitHub repository renamed `com.lwt.editor-browser` → `UnityEditorBrowser`.
  New URL: `https://github.com/dldnjsxo95/UnityEditorBrowser.git`. GitHub
  auto-redirects the old URL, but consumers should update their
  `Packages/manifest.json` entry to the new URL for clarity.
- Package name (`com.lwt.editor-browser`) is unchanged — UPM convention
  prefers reverse-DNS scope.

## [0.2.3] - 2026-05-21

### Changed
- Silenced the noisy `CREATE_BREAKAWAY_FROM_JOB 거부 ... fallback 재시도`
  warning that fired on every spawn on Unity 2022.3. The fallback is a
  normal, expected path; only the fallback **failure** case still logs.

## [0.2.2] - 2026-05-21

### Fixed
- Unity 2022.3 compatibility: `CreateProcess` failed with `ERROR_ACCESS_DENIED`
  (lastError=5) because the Unity 2022.3 Job Object disallows
  `CREATE_BREAKAWAY_FROM_JOB`. The host now retries without that flag when the
  first attempt is rejected with ACCESS_DENIED. Unity 6 keeps using breakaway
  as before.

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
