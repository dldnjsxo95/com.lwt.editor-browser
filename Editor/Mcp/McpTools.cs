// Optional MCP integration. This entire assembly
// (EditorBrowser.Mcp.asmdef) only builds when the host project has
// com.coplaydev.unity-mcp installed — the asmdef's defineConstraints
// require EDITORBROWSER_HAS_UNITY_MCP, which is in turn defined via
// versionDefines on that package. Without UnityMCP, this DLL is not
// produced at all, and the rest of the browser package keeps working
// as a standalone Editor tool with zero MCP dependency.
using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace EditorBrowser
{
    /// <summary>
    /// MCP sub-tool exposing <see cref="BrowserWindow"/>'s automation
    /// surface (<see cref="ExternalBrowserHost.NavigateAsync"/>,
    /// <see cref="ExternalBrowserHost.EvaluateAsync"/>,
    /// <see cref="ExternalBrowserHost.CaptureScreenshotAsync"/>) to
    /// Claude / MCP clients via the UnityMCP <see cref="McpForUnityToolAttribute"/>
    /// attribute, which is auto-discovered by UnityMCP's
    /// <c>CommandRegistry</c>.
    ///
    /// <para>Action-dispatch contract matching UnityMCP's other built-in
    /// tools:
    /// <list type="bullet">
    ///   <item><c>action=navigate, url=&lt;url&gt;</c> — Page.navigate</item>
    ///   <item><c>action=evaluate, expression=&lt;js&gt;</c> — Runtime.evaluate (returnByValue:true)</item>
    ///   <item><c>action=screenshot</c> — Page.captureScreenshot, returns base64 PNG</item>
    /// </list></para>
    ///
    /// <para><c>HandleCommand</c> returns <see cref="Task{Object}"/> so the
    /// UnityMCP <c>CommandRegistry</c> registers it as an async handler
    /// (see <c>CommandRegistry.RegisterCommandType</c>:
    /// <c>if (typeof(Task).IsAssignableFrom(method.ReturnType))</c>). This
    /// means CDP roundtrips run on a worker without blocking Unity's main
    /// thread — a synchronous <c>.GetAwaiter().GetResult()</c> would freeze
    /// the editor for the duration of the WebSocket exchange.</para>
    ///
    /// <para>Responses use plain anonymous objects; UnityMCP's transport
    /// serializes them via Newtonsoft.Json. <c>ok=false</c> always carries
    /// an <c>error</c> string explaining why.</para>
    /// </summary>
    [McpForUnityTool("editor_browser",
        Group = "core",
        Description = "Drive the in-Editor browser via Chrome DevTools Protocol. " +
                      "Actions: navigate (url), evaluate (expression), screenshot.")]
    public static class EditorBrowserMcpTool
    {
        /// <summary>
        /// Schema declaration for UnityMCP's <c>ToolDiscoveryService</c>.
        /// The service looks specifically for a nested <c>Parameters</c>
        /// class and reads the <see cref="ToolParameterAttribute"/>s off
        /// its public instance properties — runtime values still arrive
        /// via the <c>@params</c> JObject in <see cref="HandleCommand"/>.
        /// </summary>
        public class Parameters
        {
            [ToolParameter("Action to perform on the browser: navigate / evaluate / screenshot.")]
            public string action { get; set; }

            [ToolParameter("URL to navigate to (required when action=navigate).", Required = false)]
            public string url { get; set; }

            [ToolParameter("JavaScript expression to evaluate (required when action=evaluate).", Required = false)]
            public string expression { get; set; }
        }

        public static async Task<object> HandleCommand(JObject @params)
        {
            if (@params == null)
                return Error("Parameters cannot be null.");

            var actionToken = @params["action"];
            var actionStr = actionToken != null ? actionToken.ToString() : null;
            if (string.IsNullOrEmpty(actionStr))
                return Error("Missing required 'action' parameter (navigate / evaluate / screenshot).");

            switch (actionStr.ToLowerInvariant())
            {
                case "navigate":   return await HandleNavigate(@params).ConfigureAwait(false);
                case "evaluate":   return await HandleEvaluate(@params).ConfigureAwait(false);
                case "screenshot": return await HandleScreenshot(@params).ConfigureAwait(false);
                default:           return Error("Unknown action '" + actionStr + "'. Valid: navigate / evaluate / screenshot.");
            }
        }

        private static async Task<object> HandleNavigate(JObject p)
        {
            var host = BrowserWindow.GetActiveHost();
            if (host == null) return Error("No BrowserWindow open. Run Window > Editor Browser first.");
            if (!host.IsAlive) return Error("Browser process is not alive.");

            var urlToken = p["url"];
            var url = urlToken != null ? urlToken.ToString() : null;
            if (string.IsNullOrEmpty(url)) return Error("Missing required 'url' parameter.");

            try
            {
                var ok = await host.NavigateAsync(url).ConfigureAwait(false);
                return new { ok = ok, url = url };
            }
            catch (Exception ex)
            {
                return Error("navigate failed: " + ex.Message);
            }
        }

        private static async Task<object> HandleEvaluate(JObject p)
        {
            var host = BrowserWindow.GetActiveHost();
            if (host == null) return Error("No BrowserWindow open.");
            if (!host.IsAlive) return Error("Browser process is not alive.");

            var exprToken = p["expression"];
            var expr = exprToken != null ? exprToken.ToString() : null;
            if (expr == null) return Error("Missing required 'expression' parameter.");

            try
            {
                var json = await host.EvaluateAsync(expr).ConfigureAwait(false);
                if (json == null) return Error("CDP endpoint not reachable.");
                // Re-parse to JToken so the response is serialized as
                // nested JSON rather than a string-escaped blob.
                JToken parsed;
                try { parsed = JToken.Parse(json); }
                catch (Exception parseEx) { return Error("Could not parse CDP response: " + parseEx.Message); }
                return new { ok = true, response = parsed };
            }
            catch (Exception ex)
            {
                return Error("evaluate failed: " + ex.Message);
            }
        }

        private static async Task<object> HandleScreenshot(JObject p)
        {
            var host = BrowserWindow.GetActiveHost();
            if (host == null) return Error("No BrowserWindow open.");
            if (!host.IsAlive) return Error("Browser process is not alive.");

            try
            {
                var bytes = await host.CaptureScreenshotAsync().ConfigureAwait(false);
                if (bytes == null || bytes.Length == 0)
                    return Error("CDP returned no screenshot data.");
                return new
                {
                    ok = true,
                    format = "png",
                    byteLength = bytes.Length,
                    base64 = Convert.ToBase64String(bytes),
                };
            }
            catch (Exception ex)
            {
                return Error("screenshot failed: " + ex.Message);
            }
        }

        private static object Error(string message)
        {
            return new { ok = false, error = message };
        }
    }
}
