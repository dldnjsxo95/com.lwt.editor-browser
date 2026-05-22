using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using EditorBrowser.Automation;
using EditorBrowser.Automation.Protocol;
using EditorBrowser.Native;
using UnityEngine;

namespace EditorBrowser
{
    /// <summary>
    /// Hosts an external browser (Chrome / Edge) as a sibling process and
    /// attaches its main window as an owner-popup of Unity's main HWND,
    /// re-syncing its absolute screen position/size every editor tick to
    /// track the EditorWindow body region.
    ///
    /// <para>WS_CHILD reparenting is unusable here: on Unity 6 the DirectX
    /// swap chain present overwrites WS_CHILD areas every frame, so the
    /// browser is invisible despite a correct hierarchy. Owner-popup is a
    /// separate top-level window that DWM composites on top of Unity. The
    /// trade-off is that the four well-known owner-popup symptoms apply
    /// (out-of-clip, covers Unity UI, flicker on tab switch, escapes when
    /// Unity is deactivated) and need explicit mitigation.</para>
    ///
    /// <para>The browser's native chrome is stripped in two steps:
    /// (1) <c>chrome.exe --app=&lt;url&gt;</c> removes the tabstrip / address
    /// bar / menu; (2) <c>SetWindowLong</c> strips any remaining
    /// <c>WS_CAPTION</c>, <c>WS_THICKFRAME</c>, etc. and sets
    /// <c>WS_POPUP</c>.</para>
    ///
    /// Windows-only. <see cref="Start"/> is a no-op on other platforms.
    /// </summary>
    public sealed class ExternalBrowserHost : IDisposable
    {
        // Chrome's main app window class prefix (Chrome_WidgetWin_0 or _1).
        private const string ChromeWindowClassPrefix = "Chrome_WidgetWin_";

        private static readonly string UserDataDirRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EditorBrowser", "BrowserProfile");

        private Process _process;
        private int _chromePid;
        private IntPtr _browserHwnd;
        private IntPtr _unityHwnd;
        private bool _attached;
        private bool _visible;
        private bool _disposed;
        private DateTime _processStartUtc;

        // Actual DevTools port assigned by the OS via --remote-debugging-port=0.
        // Discovered by reading <user-data-dir>/DevToolsActivePort after
        // Chrome starts. Volatile because Navigate's background Task may
        // observe and write this from a worker thread.
        private volatile int _discoveredDevToolsPort;

        // Cached page-target WebSocket URL after the first /json/list query.
        // Per-call CDP commands (NavigateAsync / EvaluateAsync /
        // CaptureScreenshotAsync) reuse this so they don't keep re-fetching
        // /json/list. The URL stays stable for the lifetime of the Chrome
        // process; reset on Start and DisposeProcess.
        private volatile string _cachedPageWsUrl;

        // Reparenting before Chrome's rendering surface is initialized turns
        // the page black, and moving the window before first paint shows
        // empty content. Wait at least this long after spawn before attach.
        private static readonly TimeSpan AttachMinDelay = TimeSpan.FromMilliseconds(1200);

        private int _lastX, _lastY, _lastW, _lastH;

        // Background watchdog enforces the last known good position even
        // while Unity's main thread is blocked inside the OS drag modal
        // loop. The watchdog (in BrowserWindow) reads these statics and
        // re-applies SetWindowPos if the browser drifts.
        public static volatile int s_enforceX;
        public static volatile int s_enforceY;
        public static volatile int s_enforceW;
        public static volatile int s_enforceH;
        public static volatile bool s_enforceEnabled;

        public bool IsAlive
        {
            get
            {
                if (_chromePid <= 0) return _process != null && !_process.HasExited;
                try { var p = Process.GetProcessById(_chromePid); return !p.HasExited; }
                catch { return false; }
            }
        }
        public bool IsAttached => _attached && _browserHwnd != IntPtr.Zero && Win32.IsWindow(_browserHwnd);
        public IntPtr BrowserHwnd => _browserHwnd;
        public IntPtr UnityHwnd => _unityHwnd;
        public int ProcessId => _chromePid > 0 ? _chromePid : (_process?.Id ?? 0);

        /// <summary>
        /// Navigate to a new URL. If a browser process is already alive the
        /// page is swapped via Chrome DevTools Protocol (Page.navigate), so
        /// the user sees an immediate transition without a process restart.
        /// Otherwise a fresh browser is spawned at the body's screen rect.
        /// </summary>
        public void Navigate(string url, int bodyX, int bodyY, int bodyW, int bodyH)
        {
            if (string.IsNullOrEmpty(url)) return;

            if (!IsRunningOnWindows()) return;

            if (IsAlive)
            {
                // CDP roundtrip blocks until response — fire-and-forget on
                // a worker so the main thread isn't held.
                var captured = url;
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try { await NavigateAsync(captured); }
                    catch { }
                });
                return;
            }

            Start(url, bodyX, bodyY, bodyW, bodyH);
        }

        /// <summary>
        /// Send <c>Page.navigate</c> to the currently-alive browser via the
        /// <see cref="EditorBrowser.Automation"/> CDP stack. Builds a short-
        /// lived <see cref="CdpConnection"/> for each call — for typical
        /// usage (one navigate per few seconds, occasional MCP-driven
        /// automation) per-call setup is cheap (~100 ms) and avoids the
        /// complexity of a persistent socket bound to the host's lifecycle.
        /// </summary>
        public async System.Threading.Tasks.Task<bool> NavigateAsync(string url, int timeoutMs = 10000)
        {
            if (string.IsNullOrEmpty(url)) return false;
            var wsUrl = await GetPageWebSocketUrlAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(wsUrl)) return false;
            using (var conn = new CdpConnection())
            {
                await conn.ConnectAsync(wsUrl, timeoutMs: 5000).ConfigureAwait(false);
                var page = new Page(new CdpSession(conn));
                await page.NavigateAsync(url, timeoutMs).ConfigureAwait(false);
            }
            return true;
        }

        /// <summary>
        /// Send <c>Runtime.evaluate</c> with <c>returnByValue: true</c>.
        /// Returns the raw JSON response (caller parses
        /// <c>result.value</c> / <c>result.exceptionDetails</c>).
        /// Returns <c>null</c> if the browser isn't alive or the CDP
        /// endpoint can't be reached.
        /// </summary>
        public async System.Threading.Tasks.Task<string> EvaluateAsync(string expression, int timeoutMs = 5000)
        {
            if (expression == null) return null;
            var wsUrl = await GetPageWebSocketUrlAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(wsUrl)) return null;
            using (var conn = new CdpConnection())
            {
                await conn.ConnectAsync(wsUrl, timeoutMs: 5000).ConfigureAwait(false);
                var runtime = new Runtime(new CdpSession(conn));
                return await runtime.EvaluateAsync(expression, timeoutMs).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Send <c>Page.captureScreenshot</c> and return the decoded PNG
        /// bytes. Returns <c>null</c> if the browser isn't alive, the CDP
        /// endpoint can't be reached, or the base64 payload couldn't be
        /// parsed out of the response.
        /// </summary>
        public async System.Threading.Tasks.Task<byte[]> CaptureScreenshotAsync(int timeoutMs = 10000)
        {
            var wsUrl = await GetPageWebSocketUrlAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(wsUrl)) return null;
            string json;
            using (var conn = new CdpConnection())
            {
                await conn.ConnectAsync(wsUrl, timeoutMs: 5000).ConfigureAwait(false);
                var page = new Page(new CdpSession(conn));
                json = await page.CaptureScreenshotAsync(timeoutMs).ConfigureAwait(false);
            }
            // Extract result.data (base64-encoded PNG) — keep regex flat
            // since CdpConnection only does minimal root-level parsing.
            var m = System.Text.RegularExpressions.Regex.Match(
                json, "\"data\"\\s*:\\s*\"([A-Za-z0-9+/=]+)\"");
            if (!m.Success) return null;
            try { return System.Convert.FromBase64String(m.Groups[1].Value); }
            catch { return null; }
        }

        /// <summary>
        /// Discover the page-target WebSocket URL by GETting
        /// <c>http://127.0.0.1:&lt;port&gt;/json/list</c> and extracting the
        /// first <c>webSocketDebuggerUrl</c>. Cached after first success
        /// for the lifetime of the Chrome process; reset on
        /// <see cref="Start"/> and <see cref="DisposeProcess"/>.
        /// </summary>
        private async System.Threading.Tasks.Task<string> GetPageWebSocketUrlAsync()
        {
            if (!string.IsNullOrEmpty(_cachedPageWsUrl)) return _cachedPageWsUrl;
            int port = _discoveredDevToolsPort;
            if (port <= 0) port = WaitForDevToolsPort();
            if (port <= 0) return null;

            // /json/list is a small, fast endpoint — sync HTTP is fine.
            // Run it on a worker so we never block the calling thread
            // (typically already a worker, but be defensive).
            return await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(
                        "http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/json/list");
                    req.Timeout = 1500;
                    string listJson;
                    using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                    using (var sr = new System.IO.StreamReader(resp.GetResponseStream()))
                    {
                        listJson = sr.ReadToEnd();
                    }
                    var m = System.Text.RegularExpressions.Regex.Match(
                        listJson, "\"webSocketDebuggerUrl\"\\s*:\\s*\"(ws://[^\"]+)\"");
                    if (!m.Success) return (string)null;
                    _cachedPageWsUrl = m.Groups[1].Value;
                    return _cachedPageWsUrl;
                }
                catch { return (string)null; }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Wait briefly for Chrome to write its DevTools port to
        /// <c>&lt;user-data-dir&gt;/DevToolsActivePort</c>. Chrome creates
        /// this file once the remote-debugging listener is bound; with
        /// <c>--remote-debugging-port=0</c> the first line is the OS-
        /// assigned port (the second line is the browser WS endpoint prefix,
        /// which we don't need since we ask <c>/json/list</c> for the page
        /// target's URL).
        /// <para>Caches into <see cref="_discoveredDevToolsPort"/> on first
        /// success. Polls up to ~2s on the calling (background) thread.</para>
        /// </summary>
        private int WaitForDevToolsPort()
        {
            if (_discoveredDevToolsPort > 0) return _discoveredDevToolsPort;
            var portFile = Path.Combine(UserDataDirRoot, "DevToolsActivePort");
            for (int i = 0; i < 20; i++)
            {
                int p = ReadDevToolsActivePortFile(portFile);
                if (p > 0)
                {
                    _discoveredDevToolsPort = p;
                    return p;
                }
                System.Threading.Thread.Sleep(100);
            }
            return 0;
        }

        /// <summary>
        /// Single non-blocking read of the DevToolsActivePort file. Returns
        /// 0 if the file is missing, locked, or malformed.
        /// </summary>
        private static int ReadDevToolsActivePortFile(string portFile)
        {
            if (!File.Exists(portFile)) return 0;
            try
            {
                // Chrome rewrites this file atomically, but it can briefly
                // be locked between truncation and rewrite — hence the
                // catch-all. Open with FileShare.ReadWrite so we don't
                // collide with Chrome's writer.
                using (var fs = new FileStream(portFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    var firstLine = sr.ReadLine();
                    if (!string.IsNullOrWhiteSpace(firstLine)
                        && int.TryParse(firstLine.Trim(), System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var port)
                        && port > 0)
                    {
                        return port;
                    }
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Safe to call every frame. Receives the body's absolute screen
        /// rect (pixels) and re-applies <c>SetWindowPos</c>. A zero/negative
        /// size hides the window.
        /// </summary>
        public void SyncBoundsAbsoluteScreen(int absX, int absY, int absW, int absH)
        {
            // IsAlive check intentionally skipped: TryAttach handles the
            // alive/dead distinction internally and may need a few ticks
            // before the real Chrome PID is resolved.
            if (!_attached && !TryAttach()) return;
            if (!Win32.IsWindow(_browserHwnd))
            {
                _attached = false;
                return;
            }

            if (absW <= 0 || absH <= 0)
            {
                Hide();
                return;
            }

            // Drift gate: if the rect matches the last applied one, skip
            // the heavy SetWindowPos but still bump z-order on each tick.
            // The browser is a sibling top-level, so dragging the Unity
            // Tab can briefly bring its floating HWND above the browser;
            // a no-op HWND_TOP call keeps it on top without disturbing the
            // paint cycle (Chrome paint glitches if SetWindowPos with a
            // real rect runs every 16ms).
            if (_visible && absX == _lastX && absY == _lastY && absW == _lastW && absH == _lastH)
            {
                Win32.SetWindowPos(_browserHwnd, Win32.HWND_TOP, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                return;
            }

            _lastX = absX; _lastY = absY; _lastW = absW; _lastH = absH;

            // The Chrome window rect matches the body exactly. Two
            // alternative strategies (expand down + cut-out top, expand up
            // + cut-out top) were tried and both produced visible
            // artifacts: owner-popup OS-level occupation of the cut-out
            // region + Unity's D3D swap-chain compositing leaks visuals
            // outside the visible region, so the EditorWindow's toolbar or
            // statusBar ended up covered. Keeping window == body leaves
            // Chrome's ~32px PWA mini titlebar visible at the top of the
            // body, but that looks like part of the page and both toolbars
            // stay intact.
            var winX = absX;
            var winY = absY;
            var winW = absW;
            var winH = absH;

            s_enforceX = winX; s_enforceY = winY; s_enforceW = winW; s_enforceH = winH;
            s_enforceEnabled = true;

            Win32.SetWindowPos(
                _browserHwnd, Win32.HWND_TOP,
                winX, winY, winW, winH,
                Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);

            // Some Chrome builds skip relayout unless WM_SIZE is posted.
            var sizeLParam = new IntPtr(((winH & 0xFFFF) << 16) | (winW & 0xFFFF));
            Win32.PostMessage(_browserHwnd, Win32.WM_SIZE,
                new IntPtr((int)Win32.SIZE_RESTORED), sizeLParam);

            if (!_visible)
            {
                Win32.ShowWindow(_browserHwnd, Win32.SW_SHOWNOACTIVATE);
                _visible = true;
            }

            Win32.RedrawWindow(_browserHwnd, IntPtr.Zero, IntPtr.Zero,
                Win32.RDW_INVALIDATE | Win32.RDW_ERASE | Win32.RDW_FRAME
                | Win32.RDW_ALLCHILDREN | Win32.RDW_UPDATENOW);
        }

        public void Hide()
        {
            if (!IsAlive || !_visible || _browserHwnd == IntPtr.Zero) return;
            Win32.ShowWindow(_browserHwnd, Win32.SW_HIDE);
            _visible = false;
            // Stop the watchdog from re-showing the browser via enforce.
            s_enforceEnabled = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DisposeProcess();
        }

        private static bool IsRunningOnWindows()
        {
            return Application.platform == RuntimePlatform.WindowsEditor;
        }

        private void Start(string url, int bodyX, int bodyY, int bodyW, int bodyH)
        {
            var info = BrowserDetector.Detect();
            if (!info.IsAvailable) return;

            try { Directory.CreateDirectory(UserDataDirRoot); }
            catch { }

            // --app=url enables the PWA "app" mode (no tabs/address/menu).
            // --user-data-dir isolates from the user's regular profile.
            // --window-position/-size spawns at the exact body rect so the
            // user never sees the window appear in the upper-left first
            // and jump into place.
            //
            // NEVER add any of these flags — each one stops Chrome from
            // painting and produces a permanently white page:
            //   --in-process-gpu
            //   --disable-gpu, --disable-gpu-compositing
            //   --disable-features=CalculateNativeWinOcclusion
            //   --disable-backgrounding-occluded-windows
            //   --disable-renderer-backgrounding
            var spawnX = bodyX;
            var spawnY = bodyY;
            var spawnW = Math.Max(bodyW, 200);
            var spawnH = Math.Max(bodyH, 200);
            // --remote-debugging-port=0 lets the OS pick a free port to
            // avoid conflicts with any other Chrome instance the user has
            // running. The chosen port is discovered post-launch via the
            // DevToolsActivePort file Chrome writes inside user-data-dir.
            var args =
                $"--app={url} " +
                $"--user-data-dir=\"{UserDataDirRoot}\" " +
                "--no-first-run --no-default-browser-check --disable-popup-blocking " +
                "--remote-debugging-port=0 " +
                $"--window-position={spawnX},{spawnY} --window-size={spawnW},{spawnH}";

            // Unity puts child processes into a Job Object whose limits
            // (paint suppression etc.) break Chrome's compositor. Use
            // CREATE_BREAKAWAY_FROM_JOB so Chrome runs outside the Job,
            // and DETACHED_PROCESS so it doesn't inherit Unity's console.
            //
            // On Unity 2022.3 the Job Object refuses BREAKAWAY (the
            // CreateProcess call returns ERROR_ACCESS_DENIED). Fall back
            // to spawning without breakaway in that case; Chrome lives
            // inside the Job but the Job's SILENT_BREAKAWAY_OK bit
            // (usually set) still ends up detaching it. Unity 6 accepts
            // BREAKAWAY on the first try.
            var quotedExe = "\"" + info.ExecutablePath + "\"";
            var commandLine = quotedExe + " " + args;
            var startupInfo = new Native.Win32.STARTUPINFO();
            startupInfo.cb = Marshal.SizeOf(startupInfo);

            const uint baseFlags = Native.Win32.DETACHED_PROCESS
                                 | Native.Win32.CREATE_NEW_PROCESS_GROUP;
            const uint breakawayFlags = baseFlags | Native.Win32.CREATE_BREAKAWAY_FROM_JOB;

            var ok = Native.Win32.CreateProcess(
                lpApplicationName: null,
                lpCommandLine: commandLine,
                lpProcessAttributes: IntPtr.Zero,
                lpThreadAttributes: IntPtr.Zero,
                bInheritHandles: false,
                dwCreationFlags: breakawayFlags,
                lpEnvironment: IntPtr.Zero,
                lpCurrentDirectory: System.IO.Path.GetDirectoryName(info.ExecutablePath) ?? string.Empty,
                lpStartupInfo: ref startupInfo,
                lpProcessInformation: out var procInfo);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 5) // ERROR_ACCESS_DENIED — Job rejects breakaway.
                {
                    // Expected fallback path on Unity 2022.3. Retry without
                    // CREATE_BREAKAWAY_FROM_JOB; both attempts now fail silently
                    // if the second one also fails.
                    ok = Native.Win32.CreateProcess(
                        lpApplicationName: null,
                        lpCommandLine: commandLine,
                        lpProcessAttributes: IntPtr.Zero,
                        lpThreadAttributes: IntPtr.Zero,
                        bInheritHandles: false,
                        dwCreationFlags: baseFlags,
                        lpEnvironment: IntPtr.Zero,
                        lpCurrentDirectory: System.IO.Path.GetDirectoryName(info.ExecutablePath) ?? string.Empty,
                        lpStartupInfo: ref startupInfo,
                        lpProcessInformation: out procInfo);
                    if (!ok) return;
                }
                else
                {
                    return;
                }
            }

            // The detached process keeps running; we don't need these handles.
            Native.Win32.CloseHandle(procInfo.hThread);
            Native.Win32.CloseHandle(procInfo.hProcess);
            _chromePid = procInfo.dwProcessId;
            _process = null;

            _unityHwnd = Process.GetCurrentProcess().MainWindowHandle;
            _browserHwnd = IntPtr.Zero;
            _attached = false;
            _visible = false;
            _processStartUtc = DateTime.UtcNow;
            _lastX = _lastY = _lastW = _lastH = int.MinValue;
            _discoveredDevToolsPort = 0;
            _cachedPageWsUrl = null;
        }

        /// <summary>
        /// Locate the spawned Chrome's main app window by PID + class name,
        /// then attach using the verified pattern HIDE → style/owner change
        /// → SHOW + RedrawWindow. Reparenting before the rendering surface
        /// is up turns the page black, so wait at least <see cref="AttachMinDelay"/>
        /// after spawn before trying.
        /// </summary>
        private bool TryAttach()
        {
            if (DateTime.UtcNow - _processStartUtc < AttachMinDelay) return false;

            // We spawn via CreateProcess directly, but historically used
            // `cmd /c start chrome.exe ...`, which left _process pointing
            // at the short-lived cmd. The WMI lookup by command line still
            // works either way and lets us find the real chrome.exe PID.
            if (_chromePid <= 0)
            {
                _chromePid = FindChromePidByProfilePath();
                if (_chromePid <= 0) return false;
            }

            var found = FindChromeAppWindowByPid((uint)_chromePid);
            if (found == IntPtr.Zero) return false;

            if (_unityHwnd == IntPtr.Zero || !Win32.IsWindow(_unityHwnd))
                _unityHwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (_unityHwnd == IntPtr.Zero) return false;

            // 1) Hide while we mutate styles, to avoid a one-frame flash.
            Win32.ShowWindow(found, Win32.SW_HIDE);

            // 2) Strip captions/borders and switch to WS_POPUP.
            var style = (uint)Win32.GetWindowLongPtr(found, Win32.GWL_STYLE).ToInt64();
            const uint Strip = Win32.WS_OVERLAPPED | Win32.WS_CAPTION | Win32.WS_THICKFRAME
                               | Win32.WS_SYSMENU | Win32.WS_MINIMIZEBOX | Win32.WS_MAXIMIZEBOX
                               | Win32.WS_BORDER | Win32.WS_DLGFRAME | Win32.WS_CHILD;
            style &= ~Strip;
            style |= Win32.WS_POPUP | Win32.WS_CLIPSIBLINGS;
            Win32.SetWindowLongPtr(found, Win32.GWL_STYLE, new IntPtr(unchecked((int)style)));

            // 3) Drop the taskbar entry.
            var ex = (uint)Win32.GetWindowLongPtr(found, Win32.GWL_EXSTYLE).ToInt64();
            ex &= ~Win32.WS_EX_APPWINDOW;
            ex |= Win32.WS_EX_TOOLWINDOW;
            Win32.SetWindowLongPtr(found, Win32.GWL_EXSTYLE, new IntPtr(unchecked((int)ex)));

            // 4) Make Chrome an owned popup of Unity's main window. When
            //    Unity goes to the foreground Chrome rides with it; when
            //    another app takes focus Chrome goes behind too. This
            //    obviates HWND_TOPMOST tricks that would otherwise cover
            //    unrelated apps.
            Win32.SetWindowLongPtr(found, Win32.GWLP_HWNDPARENT, _unityHwnd);

            // 5) Apply style/frame changes and bump z-order within the
            //    owner group.
            Win32.SetWindowPos(found, Win32.HWND_TOP, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE
                | Win32.SWP_FRAMECHANGED);

            _browserHwnd = found;
            _attached = true;
            _visible = false;

            // Opportunistic single-shot read. By AttachMinDelay (1.2s)
            // Chrome has typically written DevToolsActivePort. If it
            // hasn't, WaitForDevToolsPort on the next Navigate's worker
            // thread will retry.
            if (_discoveredDevToolsPort == 0)
            {
                var portFile = Path.Combine(UserDataDirRoot, "DevToolsActivePort");
                var p = ReadDevToolsActivePortFile(portFile);
                if (p > 0) _discoveredDevToolsPort = p;
            }
            return true;
        }

        /// <summary>
        /// Find a chrome.exe whose command line includes our user-data-dir.
        /// Uses an external wmic call to avoid the System.Management
        /// assembly reference, which is awkward in the Unity Editor.
        /// </summary>
        private static int FindChromePidByProfilePath()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "wmic",
                    Arguments = "process where \"name='chrome.exe'\" get processid,commandline /format:csv",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return 0;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);

                foreach (var line in output.Split('\n'))
                {
                    if (line.IndexOf("EditorBrowser\\BrowserProfile", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (line.IndexOf("--app=", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    // Skip child/utility processes — only the parent app process.
                    if (line.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    // CSV layout (/format:csv): Node,CommandLine,ProcessId
                    var lastComma = line.LastIndexOf(',');
                    if (lastComma < 0) continue;
                    var pidStr = line.Substring(lastComma + 1).Trim();
                    if (int.TryParse(pidStr, out var pid) && pid > 0) return pid;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Return the main app HWND owned by the given PID — i.e. the
        /// visible top-level Chrome window with WS_CAPTION.
        ///
        /// Chrome --app= creates two Chrome_WidgetWin_* windows in sequence:
        ///   - Chrome_WidgetWin_0 : the controller (no rendering, no caption)
        ///   - Chrome_WidgetWin_1 : the actual page host (with caption)
        /// _1 appears after _0, so reject caption-less candidates and poll
        /// until the captioned one shows up.
        /// </summary>
        private static IntPtr FindChromeAppWindowByPid(uint pid)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;
            var classBuf = new StringBuilder(64);

            Win32.EnumWindows((h, _) =>
            {
                Win32.GetWindowThreadProcessId(h, out var wpid);
                if (wpid != pid) return true;

                classBuf.Length = 0;
                Win32.GetClassName(h, classBuf, classBuf.Capacity);
                if (classBuf.Length == 0) return true;
                var cn = classBuf.ToString();
                if (!cn.StartsWith(ChromeWindowClassPrefix, StringComparison.Ordinal)) return true;

                var rawStyle = (uint)Win32.GetWindowLongPtr(h, Win32.GWL_STYLE).ToInt64();
                Win32.GetWindowRect(h, out var rc);
                var w = rc.Right - rc.Left;
                var hpx = rc.Bottom - rc.Top;
                var area = w * (long)hpx;
                bool hasCaption = (rawStyle & Win32.WS_CAPTION) != 0;
                bool isChild = (rawStyle & Win32.WS_CHILD) != 0;
                bool vis = Win32.IsWindowVisible(h);

                if (isChild) return true;
                if (!hasCaption) return true;
                // Reject invisible container windows (the _0 controller
                // can have a 1920x1023 ghost rect).
                if (!vis) return true;
                if (area < 200 * 100) return true;

                long score = area;
                // Chrome convention: the "_1" suffix marks the real page host.
                if (cn == ChromeWindowClassPrefix + "1") score += 1_000_000_000L;

                if (score > bestArea)
                {
                    bestArea = score;
                    best = h;
                }
                return true;
            }, IntPtr.Zero);

            return best;
        }

        private void DisposeProcess()
        {
            try { if (_process != null && !_process.HasExited) _process.Kill(); } catch { }
            try { _process?.Dispose(); } catch { }

            if (_chromePid > 0)
            {
                try
                {
                    var p = Process.GetProcessById(_chromePid);
                    if (!p.HasExited) p.Kill();
                    p.Dispose();
                }
                catch { }
            }

            _process = null;
            _chromePid = 0;
            _browserHwnd = IntPtr.Zero;
            _attached = false;
            _visible = false;
            _lastX = _lastY = _lastW = _lastH = int.MinValue;
            _discoveredDevToolsPort = 0;
            _cachedPageWsUrl = null;
        }
    }
}
