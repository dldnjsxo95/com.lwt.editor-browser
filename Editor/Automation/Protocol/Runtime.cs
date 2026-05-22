using System.Threading.Tasks;

namespace EditorBrowser.Automation.Protocol
{
    /// <summary>
    /// Thin wrapper over a <see cref="CdpSession"/> exposing the CDP
    /// <c>Runtime</c> domain methods we care about. Returns the raw JSON
    /// response — caller parses the result envelope.
    ///
    /// <para>Reference:
    /// <c>https://chromedevtools.github.io/devtools-protocol/tot/Runtime/</c></para>
    /// </summary>
    public sealed class Runtime
    {
        private readonly CdpSession _session;

        public Runtime(CdpSession session)
        {
            if (session == null) throw new System.ArgumentNullException(nameof(session));
            _session = session;
        }

        /// <summary>The CDP session this domain wrapper is bound to.</summary>
        public CdpSession Session { get { return _session; } }

        /// <summary>
        /// Send <c>Runtime.enable</c>. Required before
        /// <c>Runtime.consoleAPICalled</c> and
        /// <c>Runtime.exceptionThrown</c> events will fire. Not required
        /// for plain <see cref="EvaluateAsync"/> calls. Idempotent.
        /// </summary>
        public Task<string> EnableAsync(int timeoutMs = 5000)
        {
            return _session.SendAsync("Runtime.enable", null, timeoutMs);
        }

        /// <summary>
        /// Send <c>Runtime.evaluate</c> with <c>returnByValue: true</c>
        /// so the result is serialized into JSON (rather than returned as
        /// a RemoteObject handle that would need a follow-up call).
        ///
        /// <para>Non-serializable values (functions, circular refs, etc.)
        /// cause Chrome to return an error envelope — the caller must
        /// inspect <c>result.exceptionDetails</c> to distinguish a
        /// successful <c>undefined</c> result from a failed eval.</para>
        /// </summary>
        public Task<string> EvaluateAsync(string expression, int timeoutMs = 5000)
        {
            if (expression == null) throw new System.ArgumentNullException(nameof(expression));
            var paramsJson =
                "{\"expression\":\"" + CdpConnection.EscapeJsonString(expression) + "\"," +
                "\"returnByValue\":true}";
            return _session.SendAsync("Runtime.evaluate", paramsJson, timeoutMs);
        }
    }
}
