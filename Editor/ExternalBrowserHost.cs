using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using EditorBrowser.Native;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace EditorBrowser
{
    /// <summary>
    /// 외부 브라우저(Chrome/Edge) 프로세스를 별도로 띄우고 그 메인 윈도우를
    /// Unity 메인 HWND를 owner로 하는 owner-popup 으로 부착. EditorWindow의 body 영역에
    /// 맞춰 매 프레임 절대 스크린 픽셀로 위치/크기를 동기화한다.
    ///
    /// **모델 결정:** WS_CHILD reparent는 Unity 6 DirectX swap chain present에 의해
    /// 매 프레임 덮어 그려져 보이지 않음. owner-popup 은 별도 top-level 이라 DWM 합성으로
    /// Unity 위에 올라옴. 트레이드오프로 4가지 증상(Tab 밖/메뉴 가림/탭 전환 깜빡임/
    /// Unity 비활성 시 튀어나옴)이 활성화되며 별도 미티게이션 필요.
    ///
    /// Caption(타이틀바·메뉴) 제거 2단:
    ///   1) chrome.exe --app=&lt;url&gt; 플래그로 브라우저 자체의 chrome(탭/주소창/메뉴) 제거
    ///   2) Win32 SetWindowLong 로 WS_CAPTION/WS_THICKFRAME 등 잔존 비트 strip + WS_POPUP 부여
    ///
    /// 본 구현은 Windows 전용. 다른 플랫폼에서는 Start()가 no-op 한다.
    /// </summary>
    internal sealed class ExternalBrowserHost : IDisposable
    {
        private const string LogPrefix = "[EditorBrowser]";

        // 오프스크린 spawn 위치 — splash flash 방지
        private const int OffscreenX = -32000;
        private const int OffscreenY = -32000;

        // Chrome/Edge 메인 앱 윈도우의 클래스명 접두 (Chrome_WidgetWin_0 또는 _1)
        private const string ChromeWindowClassPrefix = "Chrome_WidgetWin_";

        private static readonly string UserDataDirRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EditorBrowser", "BrowserProfile");

        private Process _process;   // cmd 프로세스 (곧 종료됨)
        private int _chromePid;      // 진짜 Chrome 메인 프로세스 PID (BrowserProfile path 매칭으로 찾음)
        private IntPtr _browserHwnd;
        private IntPtr _unityHwnd;
        private bool _attached;
        private bool _visible;
        private bool _disposed;
        private DateTime _processStartUtc;

        // Chrome init + 페이지 첫 페인트 완료 보장 — spawn 직후 즉시 reparent 하면 surface가 깨지고
        // 페이지 로드 전에 옮기면 빈 화면이 됨. 최소 이만큼 경과 후에만 attach 시도.
        // 1.2초: 정확한 위치에 spawn 하므로 위치 이동 필요 없음. style strip + region cut-out 만 충분.
        // (이전 3초는 좌측 상단 spawn 후 사용자에게 보이는 시간을 최대한 줄이려던 것 — 이제 처음부터
        //  body 위치에 spawn 하므로 단축 가능.)
        private static readonly TimeSpan AttachMinDelay = TimeSpan.FromMilliseconds(1200);

        // Chrome --app= 모드의 client area 내부 fake titlebar 높이 (실측).
        // 윈도우 사이즈를 이만큼 확장하고 SetWindowRgn으로 titlebar 영역을 cut-out 해서
        // 페이지 컨텐츠 영역만 EditorWindow body에 표시되게 한다.
        private const int FakeTitlebarHeight = 32;

        private int _lastX, _lastY, _lastW, _lastH;

        // === Background thread enforce — drag modal loop 우회 ===
        // 메인 thread 가 차단되어도 background watchdog 가 매 20ms 이 위치 강제 유지.
        // Unity dock system 또는 OS 가 Chrome HWND 옮겨도 즉시 SetWindowPos 복귀.
        // SyncBoundsAbsoluteScreen 의 winX/Y/W/H (FakeTitlebarHeight 적용된 실제 OS rect).
        public static volatile int s_enforceX;
        public static volatile int s_enforceY;
        public static volatile int s_enforceW;
        public static volatile int s_enforceH;
        public static volatile bool s_enforceEnabled;

        // z-order 강제 갱신 빈도 제한 — 매 sync(16ms) 마다 호출하면 Chrome paint 사이클이 망가져
        // 페이지가 안 그려진다. 500ms 마다 한 번씩만 강제.
        private DateTime _lastZOrderForceUtc = DateTime.MinValue;
        private static readonly TimeSpan ZOrderForceInterval = TimeSpan.FromMilliseconds(500);

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
        /// 새 URL 로 네비게이트.
        /// - Chrome 가 이미 살아 있으면 CDP (`Page.navigate`) 로 같은 프로세스에서
        ///   페이지만 교체 → 프로세스 restart 없이 즉시 이동. URL 입력 후 빈 화면
        ///   깜빡임 / 1-2초 지연 제거.
        /// - 처음 호출이거나 Chrome 가 죽었으면 새 프로세스 spawn (좌표 정확).
        /// </summary>
        public void Navigate(string url, int bodyX, int bodyY, int bodyW, int bodyH)
        {
            if (string.IsNullOrEmpty(url)) return;

            if (!IsRunningOnWindows())
            {
                Debug.LogWarning($"{LogPrefix} 외부 브라우저 임베드는 현재 Windows 전용입니다.");
                return;
            }

            if (IsAlive)
            {
                // CDP 호출은 HTTP + WebSocket sync wait — main thread 차단 회피 위해
                // background Task. 결과 미수신 fire-and-forget (Chrome 응답은 ~50-200ms).
                var captured = url;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { CdpNavigate(captured); }
                    catch { /* main thread 외 unity log unsafe — 조용히 무시 */ }
                });
                return;
            }

            Start(url, bodyX, bodyY, bodyW, bodyH);
        }

        /// <summary>
        /// Chrome DevTools Protocol `Page.navigate` 로 현재 페이지 URL 교체.
        /// `--remote-debugging-port=9222` 가 spawn args 에 포함되어 있음을 전제.
        /// background thread 에서 호출 — Unity API 호출 금지.
        /// </summary>
        private static bool CdpNavigate(string url)
        {
            // 1) page targets 조회 — webSocketDebuggerUrl 추출
            // **중요**: localhost 사용 시 .NET 의 DNS resolver 가 IPv6(::1) 를 먼저 시도하고
            // 2 초 후 IPv4 로 fallback 하는 케이스가 있어 ConnectAsync 가 매번 ~2030ms 지연.
            // 127.0.0.1 직접 사용으로 3-4ms 로 단축 (500-1000 배). Chrome /json/list 응답의
            // webSocketDebuggerUrl 도 Host header 따라 127.0.0.1 로 반환되므로 별도 replace
            // 불필요하지만 방어적으로 한 번 더 치환.
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
            var wsUrl = m.Groups[1].Value.Replace("ws://localhost:", "ws://127.0.0.1:");

            // 2) WebSocket 연결 + Page.navigate 전송 + close
            // ConnectAsync 가 종종 2 초 이상 걸림 (특히 첫 호출 / Chrome busy). 5 초 timeout
            // + Wait 반환 후 State.Open 명시 확인. background Task 에서 호출되므로 main
            // thread 영향 없음.
            using (var ws = new System.Net.WebSockets.ClientWebSocket())
            {
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

                try { ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                          "", System.Threading.CancellationToken.None).Wait(500); }
                catch { /* close 실패 무시 */ }
            }
            return true;
        }

        private static string EscapeJsonString(string s)
        {
            // URL 은 보통 quote/backslash 없지만 방어
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>
        /// 본 메서드는 매 프레임 호출하기에 안전. body의 절대 스크린 픽셀 RECT를 받아
        /// Unity 메인 HWND 클라이언트 좌표로 환산 후 SetWindowPos 동기화.
        /// width/height &lt;= 0 이면 hide.
        /// </summary>
        public void SyncBoundsAbsoluteScreen(int absX, int absY, int absW, int absH)
        {
            // IsAlive 체크 제거 — cmd 가 종료된 직후엔 _chromePid 가 아직 안 채워져 false 반환.
            // TryAttach 내부에서 alive 체크 한다.
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

            // owner-popup 모델 — 좌표는 절대 스크린 픽셀 그대로 사용 (client 변환 X)
            //
            // drift gate: 위치/사이즈가 직전과 동일하면 무거운 SetWindowPos 는 생략하되,
            // **HWND_TOPMOST 만은 매 sync 유지** — 사용자가 Tab 을 잡고 움직이면 BrowserWindow
            // floating HWND 가 활성화되어 일반 top-level 인 Chrome HWND 를 가린다. TOPMOST 로
            // 설정하면 활성화 무관하게 항상 모든 윈도우 위.
            // SetWindowPos(HWND_TOPMOST, NOMOVE+NOSIZE+NOACTIVATE) 는 이미 topmost 인 윈도우엔
            // no-op 이므로 paint 사이클을 깨뜨리지 않는다.
            if (_visible && absX == _lastX && absY == _lastY && absW == _lastW && absH == _lastH)
            {
                // z-order TOP 유지만 (WS_EX_TOPMOST 비트는 BrowserWindow 가 foreground 추적으로 관리).
                // HWND_TOPMOST 매번 호출하면 Unity 비활성 시에도 강제 topmost 가 되어 다른 프로그램을 가린다.
                Win32.SetWindowPos(_browserHwnd, Win32.HWND_TOP, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                return;
            }

            _lastX = absX; _lastY = absY; _lastW = absW; _lastH = absH;

            // === Chrome window 영역을 body 와 정확히 일치 (확장/cut-out 없음) ===
            // 시도했던 두 가지 cut-out 전략:
            //   (1) 아래로 확장 + 위 cut-out → Chrome 페이지가 body 아래로 32px 튀어나와
            //       statusBar 가림.
            //   (2) 위로 확장 + 위 cut-out → cut-out 영역(=toolbar 영역) 이 transparent 여야
            //       toolbar 가 보여야 하나, Chrome owner-popup 의 OS-level 점유 + Unity D3D
            //       swap chain + DWM 합성 관계로 실제로는 toolbar 가 시각적으로 가려짐.
            // 두 케이스 모두 사용자가 "한쪽이 가려짐" 으로 인식.
            //
            // **현재 전략**: window 영역을 body 와 정확 일치 시키고 SetWindowRgn 호출 안 함.
            // Chrome --app= 모드의 PWA mini titlebar (client area 상단 ~32px) 가 body 상단
            // 에 그대로 보임 — 그러나 회색의 작은 titlebar 라 페이지의 일부처럼 자연스럽고,
            // toolbar / statusBar 둘 다 보존됨. (검증 2026-05-21)
            var winX = absX;
            var winY = absY;
            var winW = absW;
            var winH = absH;

            // background enforce thread 가 읽을 마지막 valid 위치 저장.
            s_enforceX = winX; s_enforceY = winY; s_enforceW = winW; s_enforceH = winH;
            s_enforceEnabled = true;

            // HWND_TOP + SWP_FRAMECHANGED — 위치/사이즈 변경. WS_EX_TOPMOST 비트는 BrowserWindow 의
            // foreground 추적이 별도로 관리(Unity active 시 TOPMOST, inactive 시 NOTOPMOST).
            Win32.SetWindowPos(
                _browserHwnd, Win32.HWND_TOP,
                winX, winY, winW, winH,
                Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);

            // Chrome에 WM_SIZE 명시 전송
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
            // enforce 중지 — Hide 동안 background thread 가 강제 sync 시 다시 보이게 함
            s_enforceEnabled = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DisposeProcess();
        }

        /// <summary>
        /// 진단용 — 현재 host 상태와 실제 HWND 속성을 콘솔에 덤프.
        /// read_console이 multi-line을 첫 줄만 보존하므로 각 항목을 개별 Log로 분리한다.
        /// </summary>
        public void DumpDiagnostics()
        {
            Debug.Log($"{LogPrefix} DIAG-01 IsAlive={IsAlive} IsAttached={IsAttached} _visible={_visible}");
            Debug.Log($"{LogPrefix} DIAG-02 process pid={ProcessId} hasExited={_process?.HasExited.ToString() ?? "(null)"}");
            Debug.Log($"{LogPrefix} DIAG-03 _browserHwnd=0x{_browserHwnd.ToInt64():X} _unityHwnd=0x{_unityHwnd.ToInt64():X}");
            Debug.Log($"{LogPrefix} DIAG-04 lastRect=({_lastX},{_lastY}) {_lastW}x{_lastH}");

            if (_browserHwnd != IntPtr.Zero && Win32.IsWindow(_browserHwnd))
            {
                var style = (uint)Win32.GetWindowLongPtr(_browserHwnd, Win32.GWL_STYLE).ToInt64();
                var ex = (uint)Win32.GetWindowLongPtr(_browserHwnd, Win32.GWL_EXSTYLE).ToInt64();
                Win32.GetWindowRect(_browserHwnd, out var rect);
                var cn = new StringBuilder(256);
                Win32.GetClassName(_browserHwnd, cn, cn.Capacity);
                Debug.Log($"{LogPrefix} DIAG-05 style=0x{style:X} WS_CHILD={(style & Win32.WS_CHILD) != 0} WS_CAPTION={(style & Win32.WS_CAPTION) != 0} WS_VISIBLE={(style & Win32.WS_VISIBLE) != 0}");
                Debug.Log($"{LogPrefix} DIAG-06 exstyle=0x{ex:X}");
                Debug.Log($"{LogPrefix} DIAG-07 screenRect=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) size={rect.Right - rect.Left}x{rect.Bottom - rect.Top}");
                Debug.Log($"{LogPrefix} DIAG-08 class='{cn}' IsWindowVisible={Win32.IsWindowVisible(_browserHwnd)}");
            }
            else
            {
                Debug.Log($"{LogPrefix} DIAG-05 (no valid browser hwnd to introspect)");
            }
        }

        // ----- internal helpers -----

        private static bool IsRunningOnWindows()
        {
            return Application.platform == RuntimePlatform.WindowsEditor;
        }

        private void Start(string url, int bodyX, int bodyY, int bodyW, int bodyH)
        {
            var info = BrowserDetector.Detect();
            if (!info.IsAvailable)
            {
                Debug.LogError($"{LogPrefix} Chrome·Edge 둘 다 감지되지 않음. 둘 중 하나 설치 필요.");
                return;
            }

            try { Directory.CreateDirectory(UserDataDirRoot); }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} user-data-dir 생성 실패({ex.Message}) — 기본 프로필로 진행");
            }

            // --app=url : 탭/주소창/메뉴 없는 PWA 앱 모드
            // --user-data-dir : 호스트 일반 프로필과 격리
            // **--window-position/size**: 정확한 Tab body 위치/사이즈로 spawn — 좌측 상단 spawn 후
            //   점프 현상 제거. FakeTitlebarHeight 만큼 height 확장(아래로) → SetWindowRgn cut-out 시
            //   페이지가 body 안에서 자연스럽게 보임.
            //
            // **중요**: 다음 플래그는 페이지 렌더링 자체를 막아서 흰 화면을 유발하므로 절대 추가 금지
            //   - --in-process-gpu
            //   - --disable-gpu, --disable-gpu-compositing
            //   - --disable-features=CalculateNativeWinOcclusion
            //   - --disable-backgrounding-occluded-windows, --disable-renderer-backgrounding
            // SyncBoundsAbsoluteScreen 과 동일 — window 영역 = body 영역. cut-out 없음.
            // PWA mini titlebar 가 body 상단에 보이지만 toolbar / statusBar 가 가려지지 않음.
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

            // **핵심**: Unity 에디터는 자식 프로세스들을 Job Object 에 묶는다(2026-05-21 IsProcessInJob 검증).
            // Job Object 의 limit 가 Chrome paint 사이클을 차단해 흰 화면 유발. CreateProcess 의
            // CREATE_BREAKAWAY_FROM_JOB 플래그로 Job Object 탈출 + DETACHED_PROCESS 로 콘솔 분리.
            var quotedExe = "\"" + info.ExecutablePath + "\"";
            var commandLine = quotedExe + " " + args;
            var startupInfo = new Native.Win32.STARTUPINFO();
            startupInfo.cb = Marshal.SizeOf(startupInfo);

            // CREATE_BREAKAWAY_FROM_JOB 의 의도: Unity 가 자식 프로세스를 Job Object 에
            // 묶어 paint 사이클을 차단할 수 있어서 Chrome 가 Job 밖으로 분리되도록 함
            // (Unity 6 에서 검증). 그러나 Unity 2022.3 의 Job Object 는 BREAKAWAY 비트
            // 가 꺼져 있어 이 플래그 자체가 ERROR_ACCESS_DENIED (5) 로 거부됨.
            //   → 1차: BREAKAWAY 포함 시도 (Unity 6 경로)
            //   → ERROR_ACCESS_DENIED 시 2차: BREAKAWAY 없이 재시도 (Unity 2022.3 경로)
            //     이 경우 Job 의 SILENT_BREAKAWAY_OK 비트가 켜져 있으면 자동 분리되고,
            //     아니면 Chrome 가 Job 안에서 실행 (paint 이슈 가능 — 그때만 추가 진단).
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
                if (err == 5) // ERROR_ACCESS_DENIED — Job Object breakaway 거부
                {
                    Debug.LogWarning($"{LogPrefix} CREATE_BREAKAWAY_FROM_JOB 거부 (Job Object 제한). breakaway 없이 fallback 재시도.");
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
                    if (!ok)
                    {
                        Debug.LogError($"{LogPrefix} CreateProcess fallback 도 실패 (lastError={Marshal.GetLastWin32Error()})");
                        return;
                    }
                }
                else
                {
                    Debug.LogError($"{LogPrefix} CreateProcess 실패 (lastError={err})");
                    return;
                }
            }

            // 핸들 즉시 close — process 는 detached로 계속 살아 있음
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
        /// PID로 EnumWindows + 클래스명 매칭으로 진짜 앱 윈도우를 찾고,
        /// 메모리 검증 패턴(HIDE → 스타일/owner 변경 → SHOW + RedrawWindow) 으로 attach.
        /// 즉시 reparent는 Chrome rendering surface를 깨뜨려 검은 화면을 유발하므로
        /// spawn 후 최소 AttachMinDelay 경과를 보장한다.
        /// </summary>
        private bool TryAttach()
        {
            // surface init 안정화 대기
            if (DateTime.UtcNow - _processStartUtc < AttachMinDelay) return false;

            // 진짜 Chrome PID 찾기 — cmd /c start 로 spawn 했기 때문에 _process 는 곧 종료되는 cmd.
            // BrowserProfile path 를 명령행에 포함하는 chrome.exe 프로세스를 WMI 로 찾는다.
            if (_chromePid <= 0)
            {
                _chromePid = FindChromePidByProfilePath();
                if (_chromePid <= 0) return false;
            }

            var found = FindChromeAppWindowByPid((uint)_chromePid);
            if (found == IntPtr.Zero) return false;

            // 부모 결정 — Unity 메인 HWND
            if (_unityHwnd == IntPtr.Zero || !Win32.IsWindow(_unityHwnd))
                _unityHwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (_unityHwnd == IntPtr.Zero)
            {
                Debug.LogWarning($"{LogPrefix} Unity 메인 HWND를 얻지 못함 — 다음 틱 재시도");
                return false;
            }

            // === HIDE → 스타일 변경 → SHOW 순서 (검증된 패턴) ===

            // 1) HIDE — 스타일 변경 동안 깜빡임 방지
            Win32.ShowWindow(found, Win32.SW_HIDE);

            // 2) 캡션·테두리 strip + WS_POPUP (owner-popup용)
            var style = (uint)Win32.GetWindowLongPtr(found, Win32.GWL_STYLE).ToInt64();
            const uint Strip = Win32.WS_OVERLAPPED | Win32.WS_CAPTION | Win32.WS_THICKFRAME
                               | Win32.WS_SYSMENU | Win32.WS_MINIMIZEBOX | Win32.WS_MAXIMIZEBOX
                               | Win32.WS_BORDER | Win32.WS_DLGFRAME | Win32.WS_CHILD;
            style &= ~Strip;
            style |= Win32.WS_POPUP | Win32.WS_CLIPSIBLINGS;
            Win32.SetWindowLongPtr(found, Win32.GWL_STYLE, new IntPtr(unchecked((int)style)));

            // 3) EX 스타일: 작업표시줄에서 제거
            var ex = (uint)Win32.GetWindowLongPtr(found, Win32.GWL_EXSTYLE).ToInt64();
            ex &= ~Win32.WS_EX_APPWINDOW;
            ex |= Win32.WS_EX_TOOLWINDOW;
            Win32.SetWindowLongPtr(found, Win32.GWL_EXSTYLE, new IntPtr(unchecked((int)ex)));

            // 4) **owner-popup 관계 설정** — Chrome 을 Unity main 의 owned popup 으로.
            // 효과: Unity 활성 시 Chrome 함께 위로 (Tab 영역에 보임), 다른 프로그램 활성 시
            // Unity 와 Chrome 둘 다 뒤로 (다른 프로그램에 자연스럽게 가려짐). HWND_TOPMOST 불필요.
            Win32.SetWindowLongPtr(found, Win32.GWLP_HWNDPARENT, _unityHwnd);

            // 5) 스타일 변경 적용 + z-order top (Unity owned popup 그룹 안에서 최상단)
            Win32.SetWindowPos(found, Win32.HWND_TOP, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE
                | Win32.SWP_FRAMECHANGED);

            _browserHwnd = found;
            _attached = true;
            _visible = false; // 다음 SyncBoundsAbsoluteScreen에서 SHOW
            return true;
        }

        /// <summary>
        /// BrowserProfile 디렉토리를 명령행에 포함하는 chrome.exe 프로세스를 찾아 PID 반환.
        /// Unity 환경에서 System.Management 어셈블리 참조가 까다로워 외부 wmic 명령어로 우회.
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
                    if (line.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    // CSV /format:csv → Node,CommandLine,ProcessId — 마지막 컬럼이 PID
                    var lastComma = line.LastIndexOf(',');
                    if (lastComma < 0) continue;
                    var pidStr = line.Substring(lastComma + 1).Trim();
                    if (int.TryParse(pidStr, out var pid) && pid > 0) return pid;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} wmic Chrome PID 검색 실패: {ex.Message}");
            }
            return 0;
        }

        /// <summary>
        /// 특정 PID 트리에 속한 Chrome 메인 앱 윈도우(WS_CAPTION 보유)를 반환.
        ///
        /// Chrome --app= 모드는 두 개의 Chrome_WidgetWin_* 윈도우를 순차적으로 만든다.
        ///   - Chrome_WidgetWin_0 : controller (페이지 렌더링 X, 캡션 없음)
        ///   - Chrome_WidgetWin_1 : 실제 페이지 메인 앱 (캡션 있음) ← 우리가 어태치해야 할 것
        ///
        /// _1 은 _0 보다 늦게 생성된다. 따라서 캡션이 없는 후보(=_0)는 무시하고
        /// **캡션 있는 메인 앱 윈도우가 나타날 때까지 폴링**해야 한다. 없으면 IntPtr.Zero 반환
        /// → SyncBoundsAbsoluteScreen 가 다음 틱에 재시도.
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
                if (!vis) return true; // **vis=False 컨테이너 윈도우(_0 1920x1023 등) 거부**
                if (area < 200 * 100) return true;

                // _1 (Chrome --app= 메인 앱 윈도우 컨벤션) 우선
                long score = area;
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
            // cmd 프로세스는 보통 이미 종료된 상태
            try { if (_process != null && !_process.HasExited) _process.Kill(); } catch { }
            try { _process?.Dispose(); } catch { }

            // 진짜 Chrome 프로세스 종료
            if (_chromePid > 0)
            {
                try
                {
                    var p = Process.GetProcessById(_chromePid);
                    if (!p.HasExited) p.Kill();
                    p.Dispose();
                }
                catch { /* 이미 종료된 경우 등 */ }
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
