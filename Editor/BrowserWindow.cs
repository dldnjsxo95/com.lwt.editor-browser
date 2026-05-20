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

        [MenuItem(MenuPath, priority = 2010)]
        public static void OpenWindow()
        {
            var win = GetWindow<BrowserWindow>();
            win.titleContent = new GUIContent(WindowTitle);
            win.minSize = new Vector2(360f, 220f);
            win.Show();
            win.Focus();
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

        private void OnEnable()
        {
            _host = new ExternalBrowserHost();
            EditorApplication.update += OnEditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += DisposeHost;
            EditorApplication.quitting += DisposeHost;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= DisposeHost;
            EditorApplication.quitting -= DisposeHost;
            DisposeHost();
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

            // 초기 진입 — 홈페이지로 네비게이트
            Navigate(UrlResolver.DefaultHomepage, pushHistory: true);
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
            _host?.Navigate(url);
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

            var bound = _body.worldBound;
            if (float.IsNaN(bound.width) || float.IsNaN(bound.height)
                || bound.width <= 1f || bound.height <= 1f)
            {
                _host.Hide();
                return;
            }

            // EditorWindow의 position은 에디터 논리 좌표(점) 단위, screen-relative.
            // body.worldBound는 EditorWindow 패널-로컬 좌표(점). 더하면 body의 절대 논리 스크린 위치.
            // pixelsPerPoint로 픽셀 환산 — Win32는 픽셀 단위.
            var scale = EditorGUIUtility.pixelsPerPoint;
            var winPos = position;

            var absX = Mathf.RoundToInt((winPos.x + bound.x) * scale);
            var absY = Mathf.RoundToInt((winPos.y + bound.y) * scale);
            var absW = Mathf.RoundToInt(bound.width * scale);
            var absH = Mathf.RoundToInt(bound.height * scale);

            _host.SyncBoundsAbsoluteScreen(absX, absY, absW, absH);
        }
    }
}
