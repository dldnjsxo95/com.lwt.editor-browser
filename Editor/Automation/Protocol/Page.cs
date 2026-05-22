using System.Threading.Tasks;

namespace EditorBrowser.Automation.Protocol
{
    /// <summary>
    /// Thin wrapper over a <see cref="CdpSession"/> exposing the CDP
    /// <c>Page</c> domain methods we care about. Returns the raw JSON
    /// response — the caller (the eventual integration layer) is
    /// responsible for parsing the result envelope and any nested fields.
    ///
    /// <para>Reference:
    /// <c>https://chromedevtools.github.io/devtools-protocol/tot/Page/</c></para>
    /// </summary>
    public sealed class Page
    {
        private readonly CdpSession _session;

        public Page(CdpSession session)
        {
            if (session == null) throw new System.ArgumentNullException(nameof(session));
            _session = session;
        }

        /// <summary>The CDP session this domain wrapper is bound to.</summary>
        public CdpSession Session { get { return _session; } }

        /// <summary>
        /// Send <c>Page.enable</c>. Required before any page-level events
        /// (<c>Page.loadEventFired</c>, <c>Page.frameNavigated</c>, etc.)
        /// will fire on the underlying connection. Idempotent on the
        /// browser side.
        /// </summary>
        public Task<string> EnableAsync(int timeoutMs = 5000)
        {
            return _session.SendAsync("Page.enable", null, timeoutMs);
        }

        /// <summary>
        /// Send <c>Page.navigate</c> to swap the current page URL without
        /// restarting the browser process. Does NOT wait for the new page
        /// to finish loading — subscribe to <c>Page.loadEventFired</c> on
        /// the underlying <see cref="CdpConnection.OnEvent"/> if you need
        /// that signal.
        /// </summary>
        public Task<string> NavigateAsync(string url, int timeoutMs = 10000)
        {
            if (string.IsNullOrEmpty(url)) throw new System.ArgumentException("url is required", nameof(url));
            var paramsJson = "{\"url\":\"" + CdpConnection.EscapeJsonString(url) + "\"}";
            return _session.SendAsync("Page.navigate", paramsJson, timeoutMs);
        }

        /// <summary>
        /// Send <c>Page.captureScreenshot</c>. The PNG bytes come back
        /// base64-encoded inside the JSON response under
        /// <c>result.data</c> — decoding is the caller's job (typically
        /// <c>Convert.FromBase64String</c> then
        /// <c>Texture2D.LoadImage(bytes)</c> on Unity's main thread).
        /// </summary>
        public Task<string> CaptureScreenshotAsync(int timeoutMs = 10000)
        {
            return _session.SendAsync("Page.captureScreenshot", null, timeoutMs);
        }
    }
}
