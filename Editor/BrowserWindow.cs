using System;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace EditorBrowser
{
    /// <summary>
    /// Editor browser Tab window.
    ///
    /// <para>The toolbar (back/forward/refresh/URL) and a small status bar
    /// are drawn by Unity's UI Toolkit. Between them is a body region that
    /// the external browser process (Chrome via <see cref="ExternalBrowserHost"/>)
    /// attaches its window over — so the browser content never covers the
    /// toolbar or the status bar.</para>
    ///
    /// <para>Shortcut: <b>Shift + Alt + W</b> (the <c>#&amp;w</c> in the
    /// MenuItem path).</para>
    /// </summary>
    public sealed class BrowserWindow : EditorWindow
    {
        private const string MenuPath = "Window/Editor Browser #&w";
        private const string WindowTitle = "Browser";

        private readonly BrowserHistory _history = new BrowserHistory();
        private ExternalBrowserHost _host;

        private ToolbarButton _backBtn;
        private ToolbarButton _forwardBtn;
        private ToolbarButton _refreshBtn;
        private TextField _urlField;
        private Label _statusLabel;
        private VisualElement _body;

        // At CreateGUI time worldBound is still NaN; defer the first spawn
        // until the body has been laid out.
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

        private IVisualElementScheduledItem _syncSchedule;
        private IntPtr _winEventHook = IntPtr.Zero;
        private EditorBrowser.Native.Win32.WinEventDelegate _winEventDelegate; // keep alive against GC

        // ----- Reflection cache for UnityEditor internal ContainerWindow API.
        //
        // EditorWindow.position can return a false transient value (e.g.
        // (0,26)) during the dock system's frame timing race, which makes
        // the external browser jump to the upper-left for a frame.
        // ContainerWindow.Internal_GetTopleftScreenPosition() always returns
        // the actual OS-level screen position, so we route through that
        // instead.
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

        // ----- Chrome HWND watchdog (background thread) --------------------
        // While the main thread is blocked inside the OS drag modal loop,
        // a background thread polls Chrome's OS-level RECT every 20 ms and
        // forces it back to the last known good position via SetWindowPos
        // (which is thread-safe).
        private static System.Threading.Thread s_chromeWatchThread;
        private static volatile bool s_chromeWatchStop;
        // Static handle so the background watch thread doesn't need to
        // touch any instance state.
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

                                // Re-enforce the last valid position. If
                                // Unity's dock system or the OS shifts the
                                // browser HWND while the main thread is
                                // blocked, snap it back immediately.
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
                            }
                        }
                    }
                    catch { /* swallow transient OS race conditions */ }
                    System.Threading.Thread.Sleep(20); // 50 Hz
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

        /// <summary>
        /// Subscribe to Unity's window location/drag events via a
        /// WINEVENT_OUTOFCONTEXT hook. The callback fires even while
        /// <see cref="EditorApplication.update"/> and UI Toolkit's
        /// scheduler are stalled inside the OS drag modal loop, which is
        /// the only way to keep the browser synchronized during a drag.
        /// </summary>
        private void InstallWinEventHook()
        {
            if (_winEventHook != IntPtr.Zero) return;
            try
            {
                var unityPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                _winEventDelegate = OnWinEvent;
                _winEventHook = EditorBrowser.Native.Win32.SetWinEventHook(
                    EditorBrowser.Native.Win32.EVENT_SYSTEM_MOVESIZESTART,
                    EditorBrowser.Native.Win32.EVENT_OBJECT_LOCATIONCHANGE,
                    IntPtr.Zero,
                    _winEventDelegate,
                    unityPid,
                    0,
                    EditorBrowser.Native.Win32.WINEVENT_OUTOFCONTEXT);
            }
            catch { }
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
            // Filter out non-window object changes (scroll bars, menus,
            // client-area updates) which are noise here. The PID filter
            // already applied at SetWinEventHook registration.
            if (idObject != EditorBrowser.Native.Win32.OBJID_WINDOW) return;
            // Don't let an exception from a callback kill the hook itself.
            try { OnEditorUpdate(); } catch { }
        }

        private void DisposeHost()
        {
            _host?.Dispose();
            _host = null;
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;

            // CreateGUI may be invoked multiple times across domain reloads;
            // clear the tree first to avoid duplicated children.
            root.Clear();

            var toolbar = new Toolbar();

            _backBtn = new ToolbarButton(OnBackClicked) { text = "◀", tooltip = "Back" };
            _forwardBtn = new ToolbarButton(OnForwardClicked) { text = "▶", tooltip = "Forward" };
            _refreshBtn = new ToolbarButton(OnRefreshClicked) { text = "↻", tooltip = "Refresh" };

            foreach (var b in new[] { _backBtn, _forwardBtn, _refreshBtn })
            {
                b.style.minWidth = 28f;
                b.style.unityTextAlign = TextAnchor.MiddleCenter;
            }

            _urlField = new TextField
            {
                value = UrlResolver.DefaultHomepage,
                tooltip = "Enter a URL or search query and press Enter",
            };
            _urlField.style.flexGrow = 1f;
            _urlField.style.marginLeft = 4f;
            _urlField.style.marginRight = 4f;
            // Register on the trickle-down (capture) phase so the field's
            // built-in text editor / IME doesn't consume the Enter key
            // first. With bubble registration the first Enter sometimes
            // looked like an IME composition commit, requiring a second
            // press to actually submit.
            // NavigationSubmitEvent is registered as a secondary route in
            // case KeyDownEvent is suppressed by something else.
            _urlField.RegisterCallback<KeyDownEvent>(OnUrlFieldKeyDown, TrickleDown.TrickleDown);
            _urlField.RegisterCallback<NavigationSubmitEvent>(OnUrlFieldSubmit);

            toolbar.Add(_backBtn);
            toolbar.Add(_forwardBtn);
            toolbar.Add(_refreshBtn);
            toolbar.Add(_urlField);

            // The external browser HWND attaches over this body element.
            _body = new VisualElement { name = "browser-body" };
            _body.style.flexGrow = 1f;
            _body.style.backgroundColor = new StyleColor(new Color(0.12f, 0.12f, 0.12f));

            // Placeholder visible only until the browser process attaches.
            var placeholder = new Label("Loading browser...");
            placeholder.style.flexGrow = 1f;
            placeholder.style.unityTextAlign = TextAnchor.MiddleCenter;
            placeholder.style.color = new StyleColor(new Color(0.55f, 0.55f, 0.55f));
            placeholder.style.whiteSpace = WhiteSpace.Normal;
            _body.Add(placeholder);

            var statusBar = new Toolbar();
            _statusLabel = new Label("Ready");
            _statusLabel.style.flexGrow = 1f;
            _statusLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            _statusLabel.style.paddingLeft = 6f;
            statusBar.Add(_statusLabel);

            root.Add(toolbar);
            root.Add(_body);
            root.Add(statusBar);

            // EditorApplication.update is blocked during the OS drag modal
            // loop. UI Toolkit's scheduler still ticks (the timer pump runs
            // even mid-drag for some Unity versions) and GeometryChangedEvent
            // fires whenever the body's layout changes, so subscribe to both
            // as secondary sync triggers in addition to the WinEventHook.
            _syncSchedule = root.schedule.Execute(OnEditorUpdate).Every(16);
            _body.RegisterCallback<GeometryChangedEvent>(OnBodyGeometryChanged);

            // worldBound may still be NaN at CreateGUI time; defer the
            // first browser spawn to the first OnEditorUpdate where the
            // body has a valid layout.
            _pendingInitialUrl = UrlResolver.DefaultHomepage;
            _history.Push(_pendingInitialUrl);
            _urlField.SetValueWithoutNotify(_pendingInitialUrl);
            _statusLabel.text = $"Loading: {_pendingInitialUrl}";
            UpdateNavButtonsState();
        }

        private void OnBodyGeometryChanged(GeometryChangedEvent _)
        {
            OnEditorUpdate();
        }

        private double _lastSubmitTime;

        private void OnUrlFieldKeyDown(KeyDownEvent evt)
        {
            // Some environments / IME states report Enter with keyCode=None
            // and only the character set; treat both as a submit.
            bool isEnter = evt.keyCode == KeyCode.Return
                        || evt.keyCode == KeyCode.KeypadEnter
                        || evt.character == '\n'
                        || evt.character == '\r';
            if (!isEnter) return;

            SubmitUrl();
            // PreventDefault so the TextField's editor doesn't react to
            // Enter (newline insertion, selection changes).
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
            // KeyDownEvent and NavigationSubmitEvent can fire together for
            // a single Enter; debounce within 300 ms so the URL only
            // navigates once.
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

            var (x, y, w, h, state) = ComputeBodyAbsRect();
            if (state == BodyState.Valid) _host.Navigate(url, x, y, w, h);
            // If the body rect isn't trustworthy yet, defer to the next
            // OnEditorUpdate where the layout has settled.
            else _pendingInitialUrl = url;
        }

        private enum BodyState
        {
            /// <summary>RECT is trustworthy; sync the browser.</summary>
            Valid,
            /// <summary>The Tab itself is not visible; hide the browser.</summary>
            Hidden,
            /// <summary>RECT is transiently unreliable; leave the browser where it is.</summary>
            Skip,
        }

        private (int x, int y, int w, int h, BodyState state) ComputeBodyAbsRect()
        {
            if (_body == null) return (0, 0, 0, 0, BodyState.Hidden);

            // When another tab in the same dock (Console, etc.) is active,
            // the body's panel detaches or its display becomes None.
            if (_body.panel == null) return (0, 0, 0, 0, BodyState.Hidden);
            if (_body.resolvedStyle.display == DisplayStyle.None) return (0, 0, 0, 0, BodyState.Hidden);
            if (_body.resolvedStyle.visibility == Visibility.Hidden) return (0, 0, 0, 0, BodyState.Hidden);

            var bound = _body.worldBound;
            if (float.IsNaN(bound.width) || float.IsNaN(bound.height)
                || bound.width <= 1f || bound.height <= 1f)
            {
                // After a drag/dock change, UI Toolkit may invalidate the
                // panel layout and worldBound becomes NaN/zero. Returning
                // Hidden would hide the browser and produce a visible
                // flicker; Skip leaves it in place and we request a relayout.
                _body.MarkDirtyRepaint();
                return (0, 0, 0, 0, BodyState.Skip);
            }

            // EditorWindow.position returns false transient values during
            // dock-system frame races. Read the host screen origin via the
            // ContainerWindow reflection cache instead.
            var hostScreen = GetHostScreenTopLeft();
            if (float.IsNaN(hostScreen.x))
            {
                return (0, 0, 0, 0, BodyState.Skip);
            }

            var scale = EditorGUIUtility.pixelsPerPoint;
            int rAbsX = Mathf.RoundToInt((hostScreen.x + bound.x) * scale);
            int rAbsY = Mathf.RoundToInt((hostScreen.y + bound.y) * scale);
            int rAbsW = Mathf.RoundToInt(bound.width * scale);
            int rAbsH = Mathf.RoundToInt(bound.height * scale);
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
                if (s_parentField == null) { s_reflectionFailed = true; return; }

                var hostT = s_parentField.FieldType; // HostView
                var viewT = hostT;
                while (viewT != null && viewT.Name != "View") viewT = viewT.BaseType;
                if (viewT == null) { s_reflectionFailed = true; return; }

                s_viewWinField = viewT.GetField("m_Window",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                s_viewPosField = viewT.GetField("m_Position",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                s_viewParentField = viewT.GetField("m_Parent",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (s_viewWinField == null || s_viewPosField == null || s_viewParentField == null)
                {
                    s_reflectionFailed = true;
                    return;
                }

                var cwType = s_viewWinField.FieldType; // ContainerWindow
                s_getTopLeftMethod = cwType.GetMethod("Internal_GetTopleftScreenPosition",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (s_getTopLeftMethod == null) s_reflectionFailed = true;
            }
            catch
            {
                s_reflectionFailed = true;
            }
        }

        /// <summary>
        /// Top-left of this EditorWindow's host (DockArea / HostView) in
        /// screen points (no DPI scaling). Returns NaN on failure.
        /// More reliable than <see cref="EditorWindow.position"/>, which is
        /// vulnerable to dock-system frame timing races.
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

                // Accumulate View.m_Position from DockArea up to MainView:
                //   floating: DockArea m_Position=(0,0) → accumulates to (0,0)
                //   docked:   DockArea + SplitView(s) + MainView offsets
                Vector2 acc = Vector2.zero;
                object cur = host;
                // Depth guard against pathological cycles.
                for (int depth = 0; cur != null && depth < 16; depth++)
                {
                    var pos = (Rect)s_viewPosField.GetValue(cur);
                    acc += pos.position;
                    cur = s_viewParentField.GetValue(cur);
                }
                return topLeft + acc;
            }
            catch
            {
                // Domain-reload race or Unity version mismatch.
                s_reflectionFailed = true;
                return new Vector2(float.NaN, float.NaN);
            }
        }

        private void UpdateNavButtonsState()
        {
            _backBtn?.SetEnabled(_history.CanGoBack);
            _forwardBtn?.SetEnabled(_history.CanGoForward);
        }

        /// <summary>
        /// Every editor tick: compute the body's absolute screen RECT and
        /// hand it off to the external browser host. When the body isn't
        /// visible (zero size, inactive tab, etc.) hide the browser.
        /// </summary>
        private void OnEditorUpdate()
        {
            if (_host == null || _body == null) return;
            // Keep the watchdog thread's static handle current.
            s_browserHwndForWatch = _host.BrowserHwnd;

            var (absX, absY, absW, absH, state) = ComputeBodyAbsRect();
            if (state == BodyState.Hidden)
            {
                _host.Hide();
                return;
            }
            if (state == BodyState.Skip)
            {
                // bound NaN/zero or reflection unavailable — leave the
                // browser at its current position to avoid flicker.
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
