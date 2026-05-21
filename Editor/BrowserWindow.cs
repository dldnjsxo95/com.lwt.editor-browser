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

        private void OnEnable()
        {
            _host = new ExternalBrowserHost();
            EditorApplication.update += OnEditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += DisposeHost;
            EditorApplication.quitting += DisposeHost;
            InstallWinEventHook();
        }

        private void OnDisable()
        {
            UninstallWinEventHook();
            EditorApplication.update -= OnEditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= DisposeHost;
            EditorApplication.quitting -= DisposeHost;
            _syncSchedule?.Pause();
            _syncSchedule = null;
            DisposeHost();
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
                Debug.Log($"{LogPrefix} WinEventHook 등록 hook=0x{_winEventHook.ToInt64():X} pid={unityPid}");
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
            // LOCATIONCHANGE 는 너무 자주 발생하므로 idObject == 0 (윈도우 자체) 만.
            if (idObject != 0) return;
            // **thread 안전성**: callback 은 OUTOFCONTEXT 라 별도 thread 가능 — Unity API 호출 금지.
            // 단지 Win32 SetWindowPos 로 Chrome HWND 의 z-order(TOPMOST) 만 유지 → drag modal loop
            // 중에도 Chrome 이 다른 윈도우에 가려지지 않음. 위치/사이즈 추종은 메인 thread sync 에서.
            var browserHwnd = _host?.BrowserHwnd ?? IntPtr.Zero;
            if (browserHwnd == IntPtr.Zero) return;
            try
            {
                EditorBrowser.Native.Win32.SetWindowPos(
                    browserHwnd, EditorBrowser.Native.Win32.HWND_TOPMOST, 0, 0, 0, 0,
                    EditorBrowser.Native.Win32.SWP_NOMOVE
                    | EditorBrowser.Native.Win32.SWP_NOSIZE
                    | EditorBrowser.Native.Win32.SWP_NOACTIVATE);
            }
            catch { /* native callback 에선 예외 무시 */ }
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
            _urlField.RegisterCallback<KeyDownEvent>(OnUrlFieldKeyDown);

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

        private void OnUrlFieldKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter)
                return;

            var resolved = UrlResolver.Resolve(_urlField.value);
            Navigate(resolved, pushHistory: true);
            evt.StopPropagation();
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

            Debug.Log($"{LogPrefix} Navigate -> {url}");
            if (_host == null) return;

            // 현재 body 영역에서 spawn — 좌측 상단 점프 회피
            var (x, y, w, h, valid) = ComputeBodyAbsRect();
            if (valid) _host.Navigate(url, x, y, w, h);
            else _pendingInitialUrl = url; // layout 안 됐으면 다음 update 에서 spawn
        }

        private (int x, int y, int w, int h, bool valid) ComputeBodyAbsRect()
        {
            if (_body == null) return (0, 0, 0, 0, false);
            var bound = _body.worldBound;
            if (float.IsNaN(bound.width) || float.IsNaN(bound.height)
                || bound.width <= 1f || bound.height <= 1f)
                return (0, 0, 0, 0, false);
            var scale = EditorGUIUtility.pixelsPerPoint;
            var winPos = position;
            return (
                Mathf.RoundToInt((winPos.x + bound.x) * scale),
                Mathf.RoundToInt((winPos.y + bound.y) * scale),
                Mathf.RoundToInt(bound.width * scale),
                Mathf.RoundToInt(bound.height * scale),
                true);
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

            var (absX, absY, absW, absH, valid) = ComputeBodyAbsRect();
            if (!valid)
            {
                _host.Hide();
                return;
            }

            // 초기 spawn 대기 중이면 body 위치 확보된 지금 spawn
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
