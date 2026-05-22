using System;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace EditorBrowser
{
    /// <summary>
    /// 에디터 브라우저 Tab 창.
    ///
    /// 상단 툴바(뒤로/앞으로/새로고침/URL)는 항상 Unity IMGUI/UIElements가 그리고,
    /// 그 아래 body 영역에만 외부 브라우저 프로세스의 HWND를 WS_CHILD로 reparent하여
    /// 임베드한다. 따라서 브라우저 콘텐츠가 툴바를 가리지 않는다.
    ///
    /// 단축키: Shift + Alt + W (MenuItem 문법에서 #&amp;w).
    /// </summary>
    public sealed class BrowserWindow : EditorWindow
    {
        private const string MenuPath = "Window/Editor Browser #&w";
        private const string WindowTitle = "Browser";
        private const string LogPrefix = "[EditorBrowser]";

        private readonly BrowserHistory _history = new BrowserHistory();
        private ExternalBrowserHost _host;

        private ToolbarButton _backBtn;
        private ToolbarButton _forwardBtn;
        private ToolbarButton _refreshBtn;
        private TextField _urlField;
        private Label _statusLabel;
        private VisualElement _body;

        // 초기 spawn 은 body 가 layout 된 후에. CreateGUI 시점엔 worldBound 가 NaN/0 일 수 있음.
        private string _pendingInitialUrl;

        [MenuItem(MenuPath, priority = 2010)]
        public static void OpenWindow()
        {
            var win = GetWindow<BrowserWindow>();
            win.titleContent = new GUIContent(WindowTitle);
            win.minSize = new Vector2(360f, 220f);
            win.Show();
            win.Focus();
        }

        // Tab 이동 추종 검증용 — 현재 위치에서 (+200, +100) 이동 + 사이즈 -20 변경.
        // 사용자가 Tab을 잡고 옮기는 것을 시뮬레이트하여 Chrome 추종 동작 확인.
        [MenuItem("Window/Editor Browser Test Move", priority = 2013)]
        public static void TestMove()
        {
            var wins = Resources.FindObjectsOfTypeAll<BrowserWindow>();
            if (wins == null || wins.Length == 0) { Debug.Log($"{LogPrefix} TestMove: BrowserWindow 없음"); return; }
            var w = wins[0];
            var p = w.position;
            w.position = new Rect(p.x + 200f, p.y + 100f, Mathf.Max(p.width - 20f, 200f), Mathf.Max(p.height - 20f, 200f));
            Debug.Log($"{LogPrefix} TestMove: {p} → {w.position}");
        }

        [MenuItem("Window/Editor Browser Test Resize", priority = 2014)]
        public static void TestResize()
        {
            var wins = Resources.FindObjectsOfTypeAll<BrowserWindow>();
            if (wins == null || wins.Length == 0) return;
            var w = wins[0];
            var p = w.position;
            w.position = new Rect(p.x, p.y, p.width + 150f, p.height + 100f);
            Debug.Log($"{LogPrefix} TestResize: {p} → {w.position}");
        }

        // 사용자 drag 시뮬레이션: 100ms 간격으로 5번 position 변경 + Chrome 추종 검증
        [MenuItem("Window/Editor Browser Test Drag Sim", priority = 2015)]
        public static void TestDragSim()
        {
            var wins = Resources.FindObjectsOfTypeAll<BrowserWindow>();
            if (wins == null || wins.Length == 0) return;
            var w = wins[0];
            var start = w.position;
            Debug.Log($"{LogPrefix} TestDragSim 시작: {start}");
            int step = 0;
            EditorApplication.CallbackFunction tick = null;
            var last = EditorApplication.timeSinceStartup;
            tick = () => {
                if (EditorApplication.timeSinceStartup - last < 0.1) return;
                last = EditorApplication.timeSinceStartup;
                if (step++ >= 5) {
                    EditorApplication.update -= tick;
                    Debug.Log($"{LogPrefix} TestDragSim 종료: {w.position}");
                    return;
                }
                var p = w.position;
                w.position = new Rect(p.x + 30f, p.y + 20f, p.width, p.height);
                Debug.Log($"{LogPrefix} TestDragSim step {step}: → ({w.position.x},{w.position.y})");
            };
            EditorApplication.update += tick;
        }

        // sync 호출 빈도 진단 — 실시간 추종 안 될 때 원인이 호출 빈도인지 RECT 계산인지
        // 분리해서 보기 위한 토글. 켜면 1초마다 update/winEvent tick + 마지막 absRECT dump.
        [MenuItem("Window/Editor Browser Toggle Sync Trace", priority = 2016)]
        public static void ToggleSyncTrace()
        {
            s_traceEnabled = !s_traceEnabled;
            Debug.Log($"{LogPrefix} Sync Trace = {(s_traceEnabled ? "ON" : "OFF")}");
        }

        // 매 호출 ring buffer dump — drag 후 클릭하면 최근 300 호출의 winPos/bound/absRect/state
        // 시퀀스를 console 에 한 줄씩 출력. 점프 발생 frame 정확히 식별 가능.
        [MenuItem("Window/Editor Browser Dump Sync Ring", priority = 2017)]
        public static void DumpSyncRing()
        {
            Debug.Log(BuildSyncRingDump());
        }

        // mcp 자동 검증용 — DumpSyncRing 의 string 반환 버전.
        public static string BuildSyncRingDump()
        {
            int total = s_syncRing.Length;
            int n = Math.Min(s_syncRingIdx, total);
            int start = (s_syncRingIdx - n + total) % total;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{LogPrefix} === Sync Ring ({n} entries, idx={s_syncRingIdx}) ===");
            for (int i = 0; i < n; i++)
            {
                var e = s_syncRing[(start + i) % total];
                sb.AppendLine($"  [{e.time:F3}] winPos=({e.winPos.x:F0},{e.winPos.y:F0}) {e.winPos.width:F0}x{e.winPos.height:F0} " +
                              $"bound=({e.bound.x:F0},{e.bound.y:F0}) {e.bound.width:F0}x{e.bound.height:F0} " +
                              $"abs=({e.absX},{e.absY}) {e.absW}x{e.absH} state={e.state}");
            }
            return sb.ToString();
        }

        // 별도 경로로 분리 — Window/Editor Browser (leaf) 와 Window/Editor Browser/<sub> 가 같은 path에
        // 동시 존재하면 Unity 메뉴 트리가 leaf 항목을 숨길 수 있음.
        [MenuItem("Window/Editor Browser Diagnostics", priority = 2012)]
        public static void DumpDiagnostics()
        {
            var wins = Resources.FindObjectsOfTypeAll<BrowserWindow>();
            if (wins == null || wins.Length == 0)
            {
                Debug.Log($"{LogPrefix} Diagnostics: BrowserWindow가 열려 있지 않음 — 창을 자동으로 엽니다.");
                OpenWindow();
                EditorApplication.delayCall += () =>
                {
                    var w = Resources.FindObjectsOfTypeAll<BrowserWindow>();
                    foreach (var x in w) x._host?.DumpDiagnostics();
                };
                return;
            }
            foreach (var w in wins)
                w._host?.DumpDiagnostics();
        }

        private IVisualElementScheduledItem _syncSchedule;
        private IntPtr _winEventHook = IntPtr.Zero;
        private EditorBrowser.Native.Win32.WinEventDelegate _winEventDelegate; // GC root

        // 진단 — Toggle Sync Trace 메뉴로 활성화. 1초마다 호출 빈도 dump.
        private static bool s_traceEnabled;
        private int _updateTickCount;
        private int _winEventTickCount;
        private double _lastTraceDumpTime;

        // ===== Reflection cache (UnityEditor internal ContainerWindow API) =====
        // EditorWindow.position 은 dock system 의 frame timing race로 자주 false transient
        // (0,26) 값을 반환. Internal_GetTopleftScreenPosition() 은 항상 OS-level screen 좌표
        // 를 정확히 반환하므로 그것을 사용. 검증: 2026-05-21 Console docked + BrowserWindow
        // floating 두 케이스 모두에서 EditorWindow.position 과 비교, 공식 검증 완료.
        //
        //   hostScreen = ContainerWindow.Internal_GetTopleftScreenPosition()
        //              + Σ (DockArea → root) View.m_Position
        //   bodyScreen = hostScreen + body.worldBound.position
        private static System.Reflection.FieldInfo s_parentField;
        private static System.Reflection.FieldInfo s_viewWinField;
        private static System.Reflection.FieldInfo s_viewPosField;
        private static System.Reflection.FieldInfo s_viewParentField;
        private static System.Reflection.MethodInfo s_getTopLeftMethod;
        private static bool s_reflectionFailed;

        // 매 ComputeBodyAbsRect 호출 결과 ring buffer. drag 후 Dump Sync Ring 메뉴로 분석.
        private struct SyncEntry
        {
            public double time;
            public Rect winPos;
            public Rect bound;
            public int absX, absY, absW, absH;
            public BodyState state;
        }
        // ring 3000 = ~50초 (60Hz). drag 종료 후에도 충분히 그 시점 entries 보존.
        private static readonly SyncEntry[] s_syncRing = new SyncEntry[3000];
        private static int s_syncRingIdx;
        // freeze — 변화 감지 후 stable 3초 도달 시 자동 정지. mcp dump 가 drag 시퀀스 안전 캡처.
        private static bool s_ringFrozen;
        private static Rect s_changeDetectLastWinPos;
        private static double s_freezeAt;

        // === Chrome HWND watchdog — 백그라운드 thread 에서 OS 위치 추적. ===
        // 메인 thread 가 drag modal loop 으로 차단되어도 측정 가능.
        // 사용자가 본 "좌측 상단 점프" 가 어떤 시점에 OS-level 에서 Chrome HWND 위치 변경되는지
        // 핀포인트. ring buffer 와 같은 auto-freeze 메커니즘.
        private static System.Threading.Thread s_chromeWatchThread;
        private static volatile bool s_chromeWatchStop;
        private static readonly System.Collections.Generic.List<string> s_chromeRing = new System.Collections.Generic.List<string>();
        private static readonly object s_chromeRingLock = new object();
        private static volatile bool s_chromeRingFrozen;
        private static long s_chromeFreezeAtTicks;
        private static int s_chromeLastL, s_chromeLastT, s_chromeLastR, s_chromeLastB;
        // BrowserHwnd 를 정적으로 노출 — watch thread 가 access
        private static volatile IntPtr s_browserHwndForWatch;

        private void OnEnable()
        {
            _host = new ExternalBrowserHost();
            EditorApplication.update += OnEditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += DisposeHost;
            EditorApplication.quitting += DisposeHost;
            InstallWinEventHook();
            StartChromeWatchdog();
        }

        private void OnDisable()
        {
            StopChromeWatchdog();
            UninstallWinEventHook();
            EditorApplication.update -= OnEditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= DisposeHost;
            EditorApplication.quitting -= DisposeHost;
            _syncSchedule?.Pause();
            _syncSchedule = null;
            DisposeHost();
        }

        private void StartChromeWatchdog()
        {
            if (s_chromeWatchThread != null && s_chromeWatchThread.IsAlive) return;
            s_chromeWatchStop = false;
            s_chromeWatchThread = new System.Threading.Thread(() =>
            {
                while (!s_chromeWatchStop)
                {
                    try
                    {
                        var hwnd = s_browserHwndForWatch;
                        if (hwnd != IntPtr.Zero && EditorBrowser.Native.Win32.IsWindow(hwnd))
                        {
                            if (EditorBrowser.Native.Win32.GetWindowRect(hwnd, out var rc))
                            {
                                bool vis = EditorBrowser.Native.Win32.IsWindowVisible(hwnd);

                                // === 강제 위치 유지 (drag modal loop 우회 핵심) ===
                                // 메인 thread 차단 중 Unity dock system / OS 가 Chrome HWND 옮겨도
                                // 즉시 마지막 valid 위치로 복귀. SetWindowPos 는 thread-safe.
                                if (ExternalBrowserHost.s_enforceEnabled && vis)
                                {
                                    int ex = ExternalBrowserHost.s_enforceX;
                                    int ey = ExternalBrowserHost.s_enforceY;
                                    int ew = ExternalBrowserHost.s_enforceW;
                                    int eh = ExternalBrowserHost.s_enforceH;
                                    bool drifted = rc.Left != ex || rc.Top != ey
                                                || (rc.Right - rc.Left) != ew || (rc.Bottom - rc.Top) != eh;
                                    if (drifted)
                                    {
                                        EditorBrowser.Native.Win32.SetWindowPos(
                                            hwnd, EditorBrowser.Native.Win32.HWND_TOP,
                                            ex, ey, ew, eh,
                                            EditorBrowser.Native.Win32.SWP_NOACTIVATE);
                                    }
                                }

                                lock (s_chromeRingLock)
                                {
                                    if (!s_chromeRingFrozen)
                                    {
                                        s_chromeRing.Add($"{DateTime.UtcNow.Ticks} chrome=({rc.Left},{rc.Top})-({rc.Right},{rc.Bottom}) {rc.Right-rc.Left}x{rc.Bottom-rc.Top} vis={vis} enforce={(ExternalBrowserHost.s_enforceEnabled ? $"({ExternalBrowserHost.s_enforceX},{ExternalBrowserHost.s_enforceY}) {ExternalBrowserHost.s_enforceW}x{ExternalBrowserHost.s_enforceH}" : "OFF")}");
                                        if (s_chromeRing.Count > 3000) s_chromeRing.RemoveAt(0);
                                        bool changed = rc.Left != s_chromeLastL || rc.Top != s_chromeLastT
                                                    || rc.Right != s_chromeLastR || rc.Bottom != s_chromeLastB;
                                        if (changed)
                                        {
                                            s_chromeLastL = rc.Left; s_chromeLastT = rc.Top;
                                            s_chromeLastR = rc.Right; s_chromeLastB = rc.Bottom;
                                            s_chromeFreezeAtTicks = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(3).Ticks;
                                        }
                                        if (s_chromeFreezeAtTicks > 0 && DateTime.UtcNow.Ticks >= s_chromeFreezeAtTicks)
                                            s_chromeRingFrozen = true;
                                    }
                                }
                            }
                        }
                    }
                    catch { /* OS race condition 무시 */ }
                    System.Threading.Thread.Sleep(20); // 50Hz
                }
            });
            s_chromeWatchThread.IsBackground = true;
            s_chromeWatchThread.Start();
        }

        private void StopChromeWatchdog()
        {
            s_chromeWatchStop = true;
            try { s_chromeWatchThread?.Join(100); } catch { }
            s_chromeWatchThread = null;
        }

        public static string BuildChromeRingDump()
        {
            lock (s_chromeRingLock)
            {
                return $"frozen={s_chromeRingFrozen} count={s_chromeRing.Count}\n" + string.Join("\n", s_chromeRing);
            }
        }

        [MenuItem("Window/Editor Browser Reset Chrome Ring", priority = 2019)]
        public static void ResetChromeRing()
        {
            lock (s_chromeRingLock)
            {
                s_chromeRing.Clear();
                s_chromeRingFrozen = false;
                s_chromeFreezeAtTicks = 0;
                s_chromeLastL = s_chromeLastT = s_chromeLastR = s_chromeLastB = 0;
            }
            Debug.Log($"{LogPrefix} Chrome Ring reset");
        }

        /// <summary>
        /// Unity 프로세스의 윈도우 위치 변화(LOCATIONCHANGE) 와 drag 시작/종료 이벤트를
        /// native callback 으로 받는다. EditorApplication.update 가 drag modal loop 중
        /// stall 되어도 이 callback 은 호출됨.
        /// </summary>
        private void InstallWinEventHook()
        {
            if (_winEventHook != IntPtr.Zero) return;
            try
            {
                var unityPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                _winEventDelegate = OnWinEvent; // GC 방지를 위해 필드로 보관
                _winEventHook = EditorBrowser.Native.Win32.SetWinEventHook(
                    EditorBrowser.Native.Win32.EVENT_SYSTEM_MOVESIZESTART,
                    EditorBrowser.Native.Win32.EVENT_OBJECT_LOCATIONCHANGE,
                    IntPtr.Zero,
                    _winEventDelegate,
                    unityPid,
                    0,
                    EditorBrowser.Native.Win32.WINEVENT_OUTOFCONTEXT);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} WinEventHook 등록 실패: {ex.Message}");
            }
        }

        private void UninstallWinEventHook()
        {
            if (_winEventHook != IntPtr.Zero)
            {
                try { EditorBrowser.Native.Win32.UnhookWinEvent(_winEventHook); } catch { }
                _winEventHook = IntPtr.Zero;
            }
            _winEventDelegate = null;
        }

        private void OnWinEvent(IntPtr hHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwThread, uint dwTime)
        {
            // **drag modal loop 우회 핵심 경로.**
            // Unity main window 의 OS drag (SC_MOVE/SC_SIZE) modal loop 중에는
            // EditorApplication.update 와 UI Toolkit schedule.Execute 가 같은 메인 thread
            // 메시지 펌프에 묶여 차단된다. WinEventHook(WINEVENT_OUTOFCONTEXT) 만이 그
            // modal loop 안에서도 callback 받을 수 있는 유일한 경로 — 여기서 OnEditorUpdate
            // 호출해야 drag 중 Chrome 실시간 추종이 보장됨.
            //
            // idObject != OBJID_WINDOW 노이즈(스크롤바·메뉴·클라이언트 영역 변화) 제외.
            // pid 필터는 SetWinEventHook 등록 시 unityPid 로 이미 적용됨.
            // drift gate 가 같은 RECT 호출은 거의 no-op 이므로 호출 빈도 부담 적음.
            if (idObject != EditorBrowser.Native.Win32.OBJID_WINDOW) return;
            _winEventTickCount++;
            try { OnEditorUpdate(); } catch { /* callback 중 예외가 hook 자체를 죽이지 않게 */ }
        }

        private void DisposeHost()
        {
            _host?.Dispose();
            _host = null;
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;

            // 도메인 리로드/창 재구성 시 CreateGUI가 여러 번 호출되어 element가 쌓이는 것을 방지
            root.Clear();

            // ---- 툴바 ----
            var toolbar = new Toolbar();

            _backBtn = new ToolbarButton(OnBackClicked) { text = "◀", tooltip = "뒤로 가기" };
            _forwardBtn = new ToolbarButton(OnForwardClicked) { text = "▶", tooltip = "앞으로 가기" };
            _refreshBtn = new ToolbarButton(OnRefreshClicked) { text = "↻", tooltip = "새로고침" };

            foreach (var b in new[] { _backBtn, _forwardBtn, _refreshBtn })
            {
                b.style.minWidth = 28f;
                b.style.unityTextAlign = TextAnchor.MiddleCenter;
            }

            _urlField = new TextField
            {
                value = UrlResolver.DefaultHomepage,
                tooltip = "URL 또는 검색어 입력 후 Enter",
            };
            _urlField.style.flexGrow = 1f;
            _urlField.style.marginLeft = 4f;
            _urlField.style.marginRight = 4f;
            // **이중 등록 + TrickleDown**:
            // - KeyDownEvent (TrickleDown=capture): TextField 내부 텍스트 에디터가 Enter
            //   를 소비/IME commit 으로 묻기 전에 가로챔. bubble 단계 등록 시 첫 Enter 가
            //   "한글 조합 확정" 으로 처리되어 우리 callback 에 도달 안 함 → 두 번째 Enter
            //   필요 했던 원인.
            // - NavigationSubmitEvent: UI Toolkit 의 abstract submit 신호. IME / 입력기
            //   상태와 무관하게 사용자 의도된 submit 시 발생. KeyDownEvent 가 못 잡는
            //   경우의 안전망.
            _urlField.RegisterCallback<KeyDownEvent>(OnUrlFieldKeyDown, TrickleDown.TrickleDown);
            _urlField.RegisterCallback<NavigationSubmitEvent>(OnUrlFieldSubmit);

            toolbar.Add(_backBtn);
            toolbar.Add(_forwardBtn);
            toolbar.Add(_refreshBtn);
            toolbar.Add(_urlField);

            // ---- 본문 (외부 브라우저 HWND가 이 영역에 reparent됨) ----
            _body = new VisualElement { name = "browser-body" };
            _body.style.flexGrow = 1f;
            _body.style.backgroundColor = new StyleColor(new Color(0.12f, 0.12f, 0.12f));

            // 임베드 전/실패 시 보일 안내 라벨 (브라우저가 위로 올라오면 가려짐)
            var placeholder = new Label("브라우저 로딩 중...");
            placeholder.style.flexGrow = 1f;
            placeholder.style.unityTextAlign = TextAnchor.MiddleCenter;
            placeholder.style.color = new StyleColor(new Color(0.55f, 0.55f, 0.55f));
            placeholder.style.whiteSpace = WhiteSpace.Normal;
            _body.Add(placeholder);

            // ---- 상태바 ----
            var statusBar = new Toolbar();
            _statusLabel = new Label("Ready");
            _statusLabel.style.flexGrow = 1f;
            _statusLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            _statusLabel.style.paddingLeft = 6f;
            statusBar.Add(_statusLabel);

            root.Add(toolbar);
            root.Add(_body);
            root.Add(statusBar);

            // **드래그 추종 강화**: EditorApplication.update 가 Tab drag modal loop 중 stall 되는 경우
            // 대비. UI Toolkit scheduler 로 16ms 빈도 동기화 + body GeometryChangedEvent 로 layout
            // 변화 즉시 반응. drag 중에도 Chrome 이 Tab 영역을 떠나지 않게 보장.
            _syncSchedule = root.schedule.Execute(OnEditorUpdate).Every(16);
            _body.RegisterCallback<GeometryChangedEvent>(OnBodyGeometryChanged);

            // 초기 진입 — body 가 layout 된 후 (첫 OnEditorUpdate) 에 spawn 한다.
            // CreateGUI 시점엔 worldBound 가 아직 NaN 일 수 있어 spawn 위치 결정 불가.
            _pendingInitialUrl = UrlResolver.DefaultHomepage;
            _history.Push(_pendingInitialUrl);
            _urlField.SetValueWithoutNotify(_pendingInitialUrl);
            _statusLabel.text = $"Loading: {_pendingInitialUrl}";
            UpdateNavButtonsState();
        }

        private void OnBodyGeometryChanged(GeometryChangedEvent _)
        {
            // body 의 layout 이 변경되면 즉시 동기화 (Tab 이동/리사이즈/dock 변경 시 발생)
            OnEditorUpdate();
        }

        private void DumpTraceIfDue()
        {
            if (!s_traceEnabled) return;
            var now = EditorApplication.timeSinceStartup;
            if (now - _lastTraceDumpTime < 1.0) return;
            _lastTraceDumpTime = now;

            var winPos = position;
            var bound = _body != null ? _body.worldBound : default;
            var hs = GetHostScreenTopLeft();
            Debug.Log($"{LogPrefix} TRACE update={_updateTickCount}/s winEvent={_winEventTickCount}/s " +
                      $"ew.pos=({winPos.x:F0},{winPos.y:F0}) {winPos.width:F0}x{winPos.height:F0} " +
                      $"hostScreen=({hs.x:F0},{hs.y:F0}) " +
                      $"bound=({bound.x:F0},{bound.y:F0}) {bound.width:F0}x{bound.height:F0} " +
                      $"focused={(focusedWindow == this)} mouseOver={(mouseOverWindow == this)}");
            _updateTickCount = 0;
            _winEventTickCount = 0;
        }

        private double _lastSubmitTime;

        private void OnUrlFieldKeyDown(KeyDownEvent evt)
        {
            // keyCode 외에 character 도 체크 — 일부 환경 / IME 에서 keyCode 가 None 으로
            // 오고 character 만 '\r' / '\n' 로 들어오는 경우 대응.
            bool isEnter = evt.keyCode == KeyCode.Return
                        || evt.keyCode == KeyCode.KeypadEnter
                        || evt.character == '\n'
                        || evt.character == '\r';
            if (!isEnter) return;

            SubmitUrl();
            // PreventDefault: TextField 내부 텍스트 에디터가 Enter 로 newline 삽입 /
            // selection 처리 같은 부작용 일으키지 않게 차단.
            evt.StopPropagation();
            evt.PreventDefault();
        }

        private void OnUrlFieldSubmit(NavigationSubmitEvent evt)
        {
            SubmitUrl();
            evt.StopPropagation();
        }

        private void SubmitUrl()
        {
            // KeyDownEvent + NavigationSubmitEvent 가 동시에 발생할 수 있어 짧은 시간 내
            // 중복 호출 차단 (300ms — 사용자가 의도적으로 두 번 빠르게 누르는 경우는 드묾).
            var now = EditorApplication.timeSinceStartup;
            if (now - _lastSubmitTime < 0.3) return;
            _lastSubmitTime = now;

            var raw = _urlField?.value;
            if (string.IsNullOrWhiteSpace(raw)) return;
            var resolved = UrlResolver.Resolve(raw);
            if (string.IsNullOrEmpty(resolved)) return;
            Navigate(resolved, pushHistory: true);
        }

        private void OnBackClicked()
        {
            var url = _history.GoBack();
            if (!string.IsNullOrEmpty(url)) Navigate(url, pushHistory: false);
        }

        private void OnForwardClicked()
        {
            var url = _history.GoForward();
            if (!string.IsNullOrEmpty(url)) Navigate(url, pushHistory: false);
        }

        private void OnRefreshClicked()
        {
            var current = _history.Current;
            if (!string.IsNullOrEmpty(current)) Navigate(current, pushHistory: false);
        }

        private void Navigate(string url, bool pushHistory)
        {
            if (string.IsNullOrEmpty(url)) return;

            if (pushHistory) _history.Push(url);
            _urlField.SetValueWithoutNotify(url);
            _statusLabel.text = $"Navigated: {url}";
            UpdateNavButtonsState();

            if (_host == null) return;

            // 현재 body 영역에서 spawn — 좌측 상단 점프 회피
            var (x, y, w, h, state) = ComputeBodyAbsRect();
            if (state == BodyState.Valid) _host.Navigate(url, x, y, w, h);
            else _pendingInitialUrl = url; // layout/jump 안정화 안 됐으면 다음 update 에서 spawn
        }

        // body 가시성/위치 신뢰도 상태.
        // Valid  — 새 RECT 로 sync
        // Hidden — Tab 자체 안 보임 → Chrome hide
        // Skip   — winPos 가 transient 임시값 (main client 밖) → Chrome 현 위치 유지
        private enum BodyState { Valid, Hidden, Skip }

        private (int x, int y, int w, int h, BodyState state) ComputeBodyAbsRect()
        {
            if (_body == null) return (0, 0, 0, 0, BodyState.Hidden);

            // Tab inactive 감지 — 같은 dock 안 다른 Tab(Console 등) 활성화 시 panel 분리/display:None.
            if (_body.panel == null) return (0, 0, 0, 0, BodyState.Hidden);
            if (_body.resolvedStyle.display == DisplayStyle.None) return (0, 0, 0, 0, BodyState.Hidden);
            if (_body.resolvedStyle.visibility == Visibility.Hidden) return (0, 0, 0, 0, BodyState.Hidden);

            var bound = _body.worldBound;
            if (float.IsNaN(bound.width) || float.IsNaN(bound.height)
                || bound.width <= 1f || bound.height <= 1f)
            {
                // drag/dock 변경 후 UI Toolkit panel layout 이 invalidate 되어 worldBound 가
                // NaN/zero. Hidden 으로 처리하면 Chrome 깜빡임 → Skip 으로 직전 위치 유지.
                _body.MarkDirtyRepaint();
                RecordSyncEntry(default, bound, 0, 0, 0, 0, BodyState.Skip);
                return (0, 0, 0, 0, BodyState.Skip);
            }

            // **본질적 해결책 (2026-05-21):**
            // `EditorWindow.position` getter 는 dock system frame timing race 로 자주
            // false transient (예: (0,26)) 값을 반환 → Chrome 좌측 상단 점프의 원인.
            // 대신 `ContainerWindow.Internal_GetTopleftScreenPosition()` + View tree
            // m_Position 누적으로 OS-level screen 좌표 직접 획득 (reflection).
            // floating 으로 떼어낸 Tab 도 ContainerWindow 가 진짜 위치를 보존하므로 동일 공식.
            var hostScreen = GetHostScreenTopLeft();
            if (float.IsNaN(hostScreen.x))
            {
                RecordSyncEntry(default, bound, 0, 0, 0, 0, BodyState.Skip);
                return (0, 0, 0, 0, BodyState.Skip);
            }

            var scale = EditorGUIUtility.pixelsPerPoint;
            int rAbsX = Mathf.RoundToInt((hostScreen.x + bound.x) * scale);
            int rAbsY = Mathf.RoundToInt((hostScreen.y + bound.y) * scale);
            int rAbsW = Mathf.RoundToInt(bound.width * scale);
            int rAbsH = Mathf.RoundToInt(bound.height * scale);

            // ring buffer 의 winPos 슬롯에는 진단 편의를 위해 hostScreen 을 기록.
            var winPosForLog = new Rect(hostScreen.x, hostScreen.y, bound.width, bound.height);
            RecordSyncEntry(winPosForLog, bound, rAbsX, rAbsY, rAbsW, rAbsH, BodyState.Valid);
            return (rAbsX, rAbsY, rAbsW, rAbsH, BodyState.Valid);
        }

        private static void EnsureReflectionCache()
        {
            if (s_getTopLeftMethod != null || s_reflectionFailed) return;
            try
            {
                var ewType = typeof(UnityEditor.EditorWindow);
                s_parentField = ewType.GetField("m_Parent",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (s_parentField == null) { s_reflectionFailed = true; Debug.LogError($"{LogPrefix} EditorWindow.m_Parent not found"); return; }

                var hostT = s_parentField.FieldType; // HostView
                var viewT = hostT;
                while (viewT != null && viewT.Name != "View") viewT = viewT.BaseType;
                if (viewT == null) { s_reflectionFailed = true; Debug.LogError($"{LogPrefix} UnityEditor.View not found"); return; }

                s_viewWinField = viewT.GetField("m_Window",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                s_viewPosField = viewT.GetField("m_Position",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                s_viewParentField = viewT.GetField("m_Parent",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (s_viewWinField == null || s_viewPosField == null || s_viewParentField == null)
                {
                    s_reflectionFailed = true;
                    Debug.LogError($"{LogPrefix} View.{{m_Window,m_Position,m_Parent}} not found");
                    return;
                }

                var cwType = s_viewWinField.FieldType; // ContainerWindow
                s_getTopLeftMethod = cwType.GetMethod("Internal_GetTopleftScreenPosition",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (s_getTopLeftMethod == null)
                {
                    s_reflectionFailed = true;
                    Debug.LogError($"{LogPrefix} ContainerWindow.Internal_GetTopleftScreenPosition not found");
                }
            }
            catch (Exception ex)
            {
                s_reflectionFailed = true;
                Debug.LogError($"{LogPrefix} EnsureReflectionCache: {ex.Message}");
            }
        }

        /// <summary>
        /// 본 EditorWindow 의 host(DockArea/HostView) 좌상단 screen 좌표 (points, DPI 미적용).
        /// 실패 시 NaN 반환. EditorWindow.position 보다 신뢰성 높음 — dock system frame
        /// timing race 에 면역.
        /// </summary>
        private Vector2 GetHostScreenTopLeft()
        {
            EnsureReflectionCache();
            if (s_reflectionFailed) return new Vector2(float.NaN, float.NaN);

            try
            {
                var host = s_parentField.GetValue(this);
                if (host == null) return new Vector2(float.NaN, float.NaN);
                var container = s_viewWinField.GetValue(host);
                if (container == null) return new Vector2(float.NaN, float.NaN);

                var topLeft = (Vector2)s_getTopLeftMethod.Invoke(container, null);

                // DockArea 부터 root(MainView) 까지 m_Position.position 누적.
                // floating: DockArea m_Position=(0,0) → 누적 (0,0)
                // docked:   DockArea + parent SplitView + ... + MainView 의 offset 합산
                Vector2 acc = Vector2.zero;
                object cur = host;
                // 깊이 제한: pathological cycle 방어
                for (int depth = 0; cur != null && depth < 16; depth++)
                {
                    var pos = (Rect)s_viewPosField.GetValue(cur);
                    acc += pos.position;
                    cur = s_viewParentField.GetValue(cur);
                }
                return topLeft + acc;
            }
            catch (Exception ex)
            {
                // 도메인 리로드 race / Unity 버전 차이 등 — 한 번만 로깅
                if (!s_reflectionFailed)
                {
                    s_reflectionFailed = true;
                    Debug.LogError($"{LogPrefix} GetHostScreenTopLeft: {ex.Message}");
                }
                return new Vector2(float.NaN, float.NaN);
            }
        }

        private static void RecordSyncEntry(Rect winPos, Rect bound, int absX, int absY, int absW, int absH, BodyState state)
        {
            if (s_ringFrozen) return;
            var slot = s_syncRingIdx % s_syncRing.Length;
            s_syncRing[slot] = new SyncEntry
            {
                time = EditorApplication.timeSinceStartup,
                winPos = winPos,
                bound = bound,
                absX = absX, absY = absY, absW = absW, absH = absH,
                state = state,
            };
            s_syncRingIdx++;

            // 자동 freeze: winPos 변화(=drag 시작) 감지 → 3초 후 정지
            // → ring 이 drag 시퀀스 + 종료 후 1-2초 안정 entries 포함한 채 보존
            bool changed = Mathf.Abs(winPos.x - s_changeDetectLastWinPos.x) > 1f
                        || Mathf.Abs(winPos.y - s_changeDetectLastWinPos.y) > 1f
                        || Mathf.Abs(winPos.width - s_changeDetectLastWinPos.width) > 1f
                        || Mathf.Abs(winPos.height - s_changeDetectLastWinPos.height) > 1f;
            if (changed)
            {
                s_changeDetectLastWinPos = winPos;
                s_freezeAt = EditorApplication.timeSinceStartup + 3.0;
            }
            if (s_freezeAt > 0 && EditorApplication.timeSinceStartup >= s_freezeAt)
                s_ringFrozen = true;
        }

        [MenuItem("Window/Editor Browser Reset Sync Ring", priority = 2018)]
        public static void ResetSyncRing()
        {
            s_syncRingIdx = 0;
            s_ringFrozen = false;
            s_freezeAt = 0;
            s_changeDetectLastWinPos = default;
            Debug.Log($"{LogPrefix} Sync Ring reset — ready for next drag");
        }

        private void UpdateNavButtonsState()
        {
            _backBtn?.SetEnabled(_history.CanGoBack);
            _forwardBtn?.SetEnabled(_history.CanGoForward);
        }

        /// <summary>
        /// 매 에디터 틱마다 body 영역의 스크린 픽셀 RECT를 계산해 외부 브라우저 HWND를
        /// 동기화한다. body가 안 보이는 상태(0 사이즈, 탭 비활성 등)면 hide.
        /// </summary>
        private void OnEditorUpdate()
        {
            if (_host == null || _body == null) return;
            _updateTickCount++;
            DumpTraceIfDue();
            // watchdog thread 가 access 할 정적 hwnd 갱신
            s_browserHwndForWatch = _host.BrowserHwnd;

            var (absX, absY, absW, absH, state) = ComputeBodyAbsRect();
            if (state == BodyState.Hidden)
            {
                // Tab 자체가 안 보임 (panel detached / display:None / 0 사이즈) → Chrome hide.
                _host.Hide();
                return;
            }
            if (state == BodyState.Skip)
            {
                // body bound NaN/zero 또는 reflection 실패 — Chrome 현 위치 유지. hide 하면 깜빡임.
                if (s_traceEnabled)
                {
                    var hs = GetHostScreenTopLeft();
                    Debug.LogWarning($"{LogPrefix} SKIPPED hostScreen=({hs.x:F0},{hs.y:F0}) — bound NaN/zero or reflection unavailable");
                }
                return;
            }

            if (_pendingInitialUrl != null)
            {
                var url = _pendingInitialUrl;
                _pendingInitialUrl = null;
                _host.Navigate(url, absX, absY, absW, absH);
                return;
            }

            _host.SyncBoundsAbsoluteScreen(absX, absY, absW, absH);
        }
    }
}
