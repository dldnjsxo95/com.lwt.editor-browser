using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace EditorBrowser
{
    /// <summary>
    /// 에디터 브라우저 Tab 창.
    ///
    /// 본 단계에서는 UI 쉘만 구성한다 — 툴바(뒤로/앞으로/새로고침/URL),
    /// 본문 placeholder, 상태바. 실제 웹페이지 렌더링(외부 브라우저 임베드)은 후속 작업.
    ///
    /// 단축키: Shift + Alt + W (Unity MenuItem 단축키 문법에서 #&amp;w).
    /// </summary>
    public sealed class BrowserWindow : EditorWindow
    {
        private const string MenuPath = "Window/Editor Browser #&w";
        private const string WindowTitle = "Browser";
        private const string LogPrefix = "[EditorBrowser]";

        private readonly BrowserHistory _history = new BrowserHistory();

        private ToolbarButton _backBtn;
        private ToolbarButton _forwardBtn;
        private ToolbarButton _refreshBtn;
        private TextField _urlField;
        private Label _statusLabel;

        [MenuItem(MenuPath, priority = 2010)]
        public static void OpenWindow()
        {
            var win = GetWindow<BrowserWindow>();
            win.titleContent = new GUIContent(WindowTitle);
            win.minSize = new Vector2(360f, 220f);
            win.Show();
            win.Focus();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;

            // 도메인 리로드/창 재구성 시 CreateGUI가 여러 번 호출되는 경우의 element 누적 방지
            root.Clear();

            // ---- 툴바 ----
            var toolbar = new Toolbar();

            _backBtn = new ToolbarButton(OnBackClicked) { text = "◀", tooltip = "뒤로 가기" };
            _forwardBtn = new ToolbarButton(OnForwardClicked) { text = "▶", tooltip = "앞으로 가기" };
            _refreshBtn = new ToolbarButton(OnRefreshClicked) { text = "↻", tooltip = "새로고침" };

            // 툴바 버튼 폭을 약간 조정해 아이콘이 잘리지 않게
            foreach (var b in new[] { _backBtn, _forwardBtn, _refreshBtn })
            {
                b.style.minWidth = 28f;
                b.style.unityTextAlign = TextAnchor.MiddleCenter;
            }

            _urlField = new TextField
            {
                value = UrlResolver.DefaultHomepage,
                tooltip = "URL 또는 검색어 입력 후 Enter"
            };
            _urlField.style.flexGrow = 1f;
            _urlField.style.marginLeft = 4f;
            _urlField.style.marginRight = 4f;
            _urlField.RegisterCallback<KeyDownEvent>(OnUrlFieldKeyDown);

            toolbar.Add(_backBtn);
            toolbar.Add(_forwardBtn);
            toolbar.Add(_refreshBtn);
            toolbar.Add(_urlField);

            // ---- 본문 (placeholder) ----
            var body = new VisualElement { name = "browser-body" };
            body.style.flexGrow = 1f;
            body.style.backgroundColor = new StyleColor(new Color(0.18f, 0.18f, 0.18f));

            var placeholder = new Label("브라우저 렌더링 영역\n(다음 단계: 외부 브라우저 임베드)");
            placeholder.style.flexGrow = 1f;
            placeholder.style.unityTextAlign = TextAnchor.MiddleCenter;
            placeholder.style.color = new StyleColor(new Color(0.7f, 0.7f, 0.7f));
            placeholder.style.whiteSpace = WhiteSpace.Normal;
            body.Add(placeholder);

            // ---- 상태바 ----
            var statusBar = new Toolbar();
            _statusLabel = new Label("Ready");
            _statusLabel.style.flexGrow = 1f;
            _statusLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            _statusLabel.style.paddingLeft = 6f;
            statusBar.Add(_statusLabel);

            root.Add(toolbar);
            root.Add(body);
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

        /// <summary>
        /// 현재 URL 상태를 갱신하고 UI에 반영. 실제 페이지 로드는 외부 브라우저 호스트가 도입되면 위임.
        /// </summary>
        private void Navigate(string url, bool pushHistory)
        {
            if (string.IsNullOrEmpty(url)) return;

            if (pushHistory) _history.Push(url);
            _urlField.SetValueWithoutNotify(url);
            _statusLabel.text = $"Navigated: {url}";
            UpdateNavButtonsState();

            // TODO: 후속 단계에서 외부 브라우저 호스트(ExternalBrowserHost)에 URL 로드 위임
            Debug.Log($"{LogPrefix} Navigate -> {url}");
        }

        private void UpdateNavButtonsState()
        {
            _backBtn?.SetEnabled(_history.CanGoBack);
            _forwardBtn?.SetEnabled(_history.CanGoForward);
        }
    }
}
