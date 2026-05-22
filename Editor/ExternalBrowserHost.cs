using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
    internal sealed class ExternalBrowserHost : IDisposable
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
                // CDP call does sync HTTP + WebSocket waits; run on a
                // background Task so the main thread isn't blocked.
                // Fire-and-forget: response (~50-200ms) is uninteresting.
                var captured = url;
                System.Threading.Tasks.Task.Run(() =>
                {
                    // Unity API calls are unsafe off the main thread, so
                    // swallow exceptions silently rather than logging.
                    try { CdpNavigate(captured); }
                    catch { }
                });
                return;
            }

            Start(url, bodyX, bodyY, bodyW, bodyH);
        }

        /// <summary>
        /// Send Chrome DevTools Protocol <c>Page.navigate</c> to swap the
        /// current page URL without restarting Chrome. Requires
        /// <c>--remote-debugging-port=9222</c> in the spawn args.
        /// Called from a background thread — must not touch Unity APIs.
        /// </summary>
        private static bool CdpNavigate(string url)
        {
            // Use 127.0.0.1 explicitly. With "localhost" the .NET DNS
            // resolver tries IPv6 (::1) first and falls back to IPv4 only
            // after a ~2 second timeout, adding that flat 2s to every
            // ConnectAsync. Direct IPv4 brings it down to a few ms.
            string listJson;
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("http://127.0.0.1:9222/json/list");
            req.Timeout = 1500;
            using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
            using (var sr = new System.IO.StreamReader(resp.GetResponseStream()))
            {
                listJson = sr.ReadToEnd();
            }
            var m = System.Text.RegularExpressions.Regex.Match(
                listJson, "\"webSocketDebuggerUrl\"\\s*:\\s*\"(ws://[^\"]+)\"");
            if (!m.Success) return false;
            // Chrome echoes back whatever Host header it received, so the
            // URL here should already be 127.0.0.1, but replace defensively.
            var wsUrl = m.Groups[1].Value.Replace("ws://localhost:", "ws://127.0.0.1:");

            using (var ws = new System.Net.WebSockets.ClientWebSocket())
            {
                // ConnectAsync occasionally takes >2s under load; give it
                // room. Verify State.Open explicitly after the wait returns.
                var connectTask = ws.ConnectAsync(new System.Uri(wsUrl), System.Threading.CancellationToken.None);
                if (!connectTask.Wait(5000)) return false;
                if (ws.State != System.Net.WebSockets.WebSocketState.Open) return false;

                var msg = "{\"id\":1,\"method\":\"Page.navigate\",\"params\":{\"url\":\""
                          + EscapeJsonString(url) + "\"}}";
                var bytes = System.Text.Encoding.UTF8.GetBytes(msg);
                if (!ws.SendAsync(new System.ArraySegment<byte>(bytes),
                    System.Net.WebSockets.WebSocketMessageType.Text, true,
                    System.Threading.CancellationToken.None).Wait(3000))
                    return false;

                try
                {
                    ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                        "", System.Threading.CancellationToken.None).Wait(500);
                }
                catch { }
            }
            return true;
        }

        private static string EscapeJsonString(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
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
            var args =
                $"--app={url} " +
                $"--user-data-dir=\"{UserDataDirRoot}\" " +
                "--no-first-run --no-default-browser-check --disable-popup-blocking " +
                "--remote-debugging-port=9222 " +
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
        }
    }
}
