using System;
using System.Diagnostics;
using System.IO;
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

        private Process _process;
        private IntPtr _browserHwnd;
        private IntPtr _unityHwnd;
        private bool _attached;
        private bool _visible;
        private bool _disposed;

        private int _lastX, _lastY, _lastW, _lastH;

        public bool IsAlive => _process != null && !_process.HasExited;
        public bool IsAttached => _attached && _browserHwnd != IntPtr.Zero && Win32.IsWindow(_browserHwnd);
        public IntPtr BrowserHwnd => _browserHwnd;
        public IntPtr UnityHwnd => _unityHwnd;
        public int ProcessId => _process?.Id ?? 0;

        /// <summary>
        /// 새 URL로 네비게이트. 미실행 시 프로세스 시작, 실행 중이면 재시작.
        /// (V1: chrome --app= 모드는 in-place 네비게이트가 까다로워 restart 채택. V2에서 CDP로 개선 가능.)
        /// </summary>
        public void Navigate(string url)
        {
            if (string.IsNullOrEmpty(url)) return;

            if (!IsRunningOnWindows())
            {
                Debug.LogWarning($"{LogPrefix} 외부 브라우저 임베드는 현재 Windows 전용입니다.");
                return;
            }

            if (IsAlive) DisposeProcess();
            Start(url);
        }

        /// <summary>
        /// 본 메서드는 매 프레임 호출하기에 안전. body의 절대 스크린 픽셀 RECT를 받아
        /// Unity 메인 HWND 클라이언트 좌표로 환산 후 SetWindowPos 동기화.
        /// width/height &lt;= 0 이면 hide.
        /// </summary>
        public void SyncBoundsAbsoluteScreen(int absX, int absY, int absW, int absH)
        {
            if (!IsAlive) return;
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
            // drift gate: 직전과 동일하면 SetWindowPos 호출 생략
            if (_visible && absX == _lastX && absY == _lastY && absW == _lastW && absH == _lastH)
                return;

            _lastX = absX; _lastY = absY; _lastW = absW; _lastH = absH;

            // HWND_TOP + NOZORDER 미지정 → top-level z-order 최상단으로 끌어올림
            Win32.SetWindowPos(
                _browserHwnd, Win32.HWND_TOP,
                absX, absY, absW, absH,
                Win32.SWP_NOACTIVATE | Win32.SWP_ASYNCWINDOWPOS);

            if (!_visible)
            {
                Win32.ShowWindow(_browserHwnd, Win32.SW_SHOWNOACTIVATE);
                _visible = true;
            }
        }

        public void Hide()
        {
            if (!IsAlive || !_visible || _browserHwnd == IntPtr.Zero) return;
            Win32.ShowWindow(_browserHwnd, Win32.SW_HIDE);
            _visible = false;
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

        private void Start(string url)
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
            // --window-position : 오프스크린 spawn 후 reparent 완료되면 실제 위치로 이동 → splash flash 방지
            // --disable-gpu / --disable-gpu-compositing : owner-popup 으로 reparent된 후 Chrome
            //   GPU compositor가 surface DC를 잃어 검은 화면이 되는 문제 회피. CPU 렌더링 강제.
            //   (V2에서 surface 보존 가능한 패턴으로 개선 시 제거 가능)
            var args =
                $"--app={url} " +
                $"--user-data-dir=\"{UserDataDirRoot}\" " +
                "--no-first-run --no-default-browser-check --disable-popup-blocking " +
                "--disable-gpu --disable-gpu-compositing " +
                $"--window-position={OffscreenX},{OffscreenY} --window-size=800,600";

            var psi = new ProcessStartInfo
            {
                FileName = info.ExecutablePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = false,
            };

            try
            {
                _process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix} 브라우저 프로세스 시작 실패: {ex.Message}");
                _process = null;
                return;
            }

            _unityHwnd = Process.GetCurrentProcess().MainWindowHandle;
            _browserHwnd = IntPtr.Zero;
            _attached = false;
            _visible = false;
            _lastX = _lastY = _lastW = _lastH = int.MinValue;

            Debug.Log($"{LogPrefix} 브라우저 시작 kind={info.Kind} pid={_process?.Id} url={url} (오프스크린 spawn)");
        }

        /// <summary>
        /// PID로 EnumWindows + 클래스명 매칭으로 진짜 앱 윈도우를 찾는다.
        /// Process.MainWindowHandle은 Chrome --app= 모드에서 splash/dummy 윈도우를 잡을 수 있어 사용 안 함.
        /// </summary>
        private bool TryAttach()
        {
            if (_process == null || _process.HasExited) return false;

            var found = FindChromeAppWindowByPid((uint)_process.Id);
            if (found == IntPtr.Zero) return false;

            // 부모 결정 — Unity 메인 HWND
            if (_unityHwnd == IntPtr.Zero || !Win32.IsWindow(_unityHwnd))
                _unityHwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (_unityHwnd == IntPtr.Zero)
            {
                Debug.LogWarning($"{LogPrefix} Unity 메인 HWND를 얻지 못함 — 다음 틱 재시도");
                return false;
            }

            // 1) 캡션·테두리 등 데코레이션 strip + WS_POPUP (owner-popup용)
            //    WS_CHILD는 명시적으로 제거 — DirectX swap chain present에 가려서 안 보이는 문제
            var style = (uint)Win32.GetWindowLongPtr(found, Win32.GWL_STYLE).ToInt64();
            const uint Strip = Win32.WS_OVERLAPPED | Win32.WS_CAPTION | Win32.WS_THICKFRAME
                               | Win32.WS_SYSMENU | Win32.WS_MINIMIZEBOX | Win32.WS_MAXIMIZEBOX
                               | Win32.WS_BORDER | Win32.WS_DLGFRAME | Win32.WS_CHILD;
            style &= ~Strip;
            style |= Win32.WS_POPUP | Win32.WS_CLIPSIBLINGS;
            Win32.SetWindowLongPtr(found, Win32.GWL_STYLE, new IntPtr(unchecked((int)style)));

            // 2) 작업 표시줄에서 제거 (TOOLWINDOW), 활성화 시 포커스 빼앗지 않게
            var ex = (uint)Win32.GetWindowLongPtr(found, Win32.GWL_EXSTYLE).ToInt64();
            ex &= ~Win32.WS_EX_APPWINDOW;
            ex |= Win32.WS_EX_TOOLWINDOW;
            Win32.SetWindowLongPtr(found, Win32.GWL_EXSTYLE, new IntPtr(unchecked((int)ex)));

            // 3) owner 설정 — SetParent(child) 가 아니라 GWLP_HWNDPARENT 로 owner-popup 관계
            //    이렇게 하면 별도 top-level 윈도우로 유지되어 DWM 이 Unity 위에 합성한다.
            Win32.SetWindowLongPtr(found, Win32.GWLP_HWNDPARENT, _unityHwnd);

            // 4) 스타일 변경 적용 + z-order 최상단
            Win32.SetWindowPos(found, Win32.HWND_TOP, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE
                | Win32.SWP_FRAMECHANGED);

            _browserHwnd = found;
            _attached = true;

            Debug.Log($"{LogPrefix} HWND 부착 완료 hwnd=0x{found.ToInt64():X} parent=0x{_unityHwnd.ToInt64():X}");
            return true;
        }

        /// <summary>
        /// 특정 PID 트리에 속한 Chrome_WidgetWin_* 클래스의 top-level 윈도우 중
        /// 가장 큰 가시(또는 가시 후보) 윈도우를 반환. 없으면 IntPtr.Zero.
        /// </summary>
        private static IntPtr FindChromeAppWindowByPid(uint pid)
        {
            IntPtr best = IntPtr.Zero;
            int bestArea = 0;
            var classBuf = new StringBuilder(64);

            Win32.EnumWindows((h, _) =>
            {
                Win32.GetWindowThreadProcessId(h, out var wpid);
                if (wpid != pid) return true; // 다른 프로세스

                // top-level만 (이 시점엔 아직 reparent 전이라 GetParent가 0이어야 함)
                if (Win32.GetWindowLongPtr(h, Win32.GWL_STYLE).ToInt64() is var rawStyle
                    && (((uint)rawStyle) & Win32.WS_CHILD) != 0)
                    return true; // 이미 child인 윈도우는 우리가 이전에 처리한 것일 수 있음 — 건너뜀

                classBuf.Length = 0;
                Win32.GetClassName(h, classBuf, classBuf.Capacity);
                if (classBuf.Length == 0) return true;
                var cn = classBuf.ToString();
                if (!cn.StartsWith(ChromeWindowClassPrefix, StringComparison.Ordinal)) return true;

                if (!Win32.GetWindowRect(h, out var rc)) return true;
                var area = (rc.Right - rc.Left) * (rc.Bottom - rc.Top);
                if (area < 200 * 100) return true; // splash/popup 정도는 무시

                if (area > bestArea)
                {
                    bestArea = area;
                    best = h;
                }
                return true;
            }, IntPtr.Zero);

            return best;
        }

        private void DisposeProcess()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                    _process.Kill();
            }
            catch { /* 이미 종료된 경우 등 */ }

            try { _process?.Dispose(); } catch { }

            _process = null;
            _browserHwnd = IntPtr.Zero;
            _attached = false;
            _visible = false;
            _lastX = _lastY = _lastW = _lastH = int.MinValue;
        }
    }
}
