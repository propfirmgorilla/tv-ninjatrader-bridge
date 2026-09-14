// ═══════════════════════════════════════════════════════════════════════════
//  TV Webhook Bridge — NinjaTrader 8 Add-On  (LISTENER + ROUTER)
//
//  Receives TradingView strategy webhooks (JSON) on a local HTTP listener and
//  routes them to any RUNNING "TV Signal Executor" strategy (see
//  TvSignalExecutor.cs) that registered for the payload's strategy tag +
//  symbol. Enable/disable execution per strategy in Control Center →
//  Strategies. If no matching strategy is enabled, the signal is logged and
//  ignored. This add-on never places orders itself.
//
//  INSTALL: NinjaScript Editor → AddOns → New AddOn → name it TvWebhookBridge
//  → replace ALL generated code with this file → edit the CONFIG block →
//  F5 to compile → restart NinjaTrader. Watch New → NinjaScript Output for
//  "listening". Pair it with TvSignalExecutor.cs — the bridge alone does not
//  trade.
//
//  Expose it to TradingView with an ngrok tunnel (see README):
//    ngrok http 8787 --url=YOUR-DOMAIN.ngrok-free.app --host-header=rewrite
// ═══════════════════════════════════════════════════════════════════════════
#region Using declarations
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NinjaTrader.Cbi;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    // ── shared routing hub: executor strategies register here while enabled ─
    public static class TvSignalHub
    {
        private static readonly ConcurrentDictionary<string, Action<Dictionary<string, string>>> handlers
            = new ConcurrentDictionary<string, Action<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

        public static string Key(string tag, string symbol) { return tag + "|" + symbol; }
        public static void Register(string key, Action<Dictionary<string, string>> h) { handlers[key] = h; }
        public static void Unregister(string key)
        { Action<Dictionary<string, string>> _; handlers.TryRemove(key, out _); }

        public static bool Route(string key, Dictionary<string, string> payload)
        {
            Action<Dictionary<string, string>> h;
            if (!handlers.TryGetValue(key, out h)) return false;
            try { h(payload); } catch (Exception ex) { TvBridgeLog.Log("HANDLER ERROR (" + key + "): " + ex.Message); }
            return true;
        }
        public static string Registered { get { return handlers.IsEmpty ? "(none)" : string.Join(", ", handlers.Keys); } }
    }

    public static class TvBridgeLog
    {
        private static readonly object gate = new object();
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TvBridge.log");
        public static void Log(string msg)
        {
            NinjaTrader.Code.Output.Process("[TvBridge] " + msg, PrintTo.OutputTab1);
            try
            {
                lock (gate)
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
    }

    public class TvWebhookBridge : AddOnBase
    {
        // ═════════════════════ CONFIG — EDIT ME ═════════════════════
        private const int    Port        = 8787;
        // Your webhook password. Generate a long random string (e.g. run
        // `openssl rand -hex 32`, or use any password generator, 40+ chars).
        // The same value goes in your TradingView webhook URL as ?token=...
        // NEVER publish or commit your real token.
        private const string SecretToken = "CHANGE-ME-to-a-long-random-string";
        // ════════════════════════════════════════════════════════════

        private HttpListener listener;
        private Thread listenThread;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "TV Webhook Bridge";
                Description = "TradingView webhook listener; routes signals to TV Signal Executor strategies.";
            }
            else if (State == State.Configure) Start();
            else if (State == State.Terminated) Stop();
        }

        private void Start()
        {
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add("http://localhost:" + Port + "/");
                listener.Start();
                listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "TvBridgeHttp" };
                listenThread.Start();
                TvBridgeLog.Log("Bridge listening on http://localhost:" + Port + "/webhook (router mode)");
            }
            catch (Exception ex) { TvBridgeLog.Log("START ERROR: " + ex.Message); }
        }

        private void Stop()
        {
            try { if (listener != null) { listener.Stop(); listener.Close(); } } catch { }
        }

        private void ListenLoop()
        {
            while (listener != null && listener.IsListening)
            {
                HttpListenerContext ctx = null;
                try { ctx = listener.GetContext(); }
                catch { break; }
                try { Handle(ctx); }
                catch (Exception ex)
                {
                    TvBridgeLog.Log("HTTP ERROR: " + ex.Message);
                    Respond(ctx, 500, "{\"ok\":false,\"error\":\"internal\"}");
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath.ToLowerInvariant();

            if (ctx.Request.HttpMethod == "GET" && path == "/health")
            {
                Respond(ctx, 200, "{\"ok\":true,\"registered\":\"" + TvSignalHub.Registered + "\"}");
                return;
            }
            if (ctx.Request.HttpMethod != "POST" || path != "/webhook")
            { Respond(ctx, 404, "{\"ok\":false}"); return; }
            if (ctx.Request.QueryString["token"] != SecretToken)
            {
                TvBridgeLog.Log("Rejected request: bad token");
                Respond(ctx, 401, "{\"ok\":false,\"error\":\"unauthorized\"}");
                return;
            }

            string body;
            using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = r.ReadToEnd();
            TvBridgeLog.Log("RECEIVED: " + body);

            var j = ParseFlatJson(body);
            string action = Get(j, "action"), symbol = Get(j, "symbol"), tag = Get(j, "strategy");
            if (action == null || symbol == null || tag == null)
            { Respond(ctx, 400, "{\"ok\":false,\"error\":\"missing action/symbol/strategy\"}"); return; }

            string key = TvSignalHub.Key(tag, symbol);
            if (TvSignalHub.Route(key, j))
                Respond(ctx, 200, "{\"ok\":true,\"routed\":\"" + key + "\"}");
            else
            {
                TvBridgeLog.Log("IGNORED: no enabled strategy for '" + key + "'. Registered: " + TvSignalHub.Registered);
                Respond(ctx, 200, "{\"ok\":false,\"error\":\"no enabled strategy for " + key + "\"}");
            }
        }

        internal static Dictionary<string, string> ParseFlatJson(string s)
        {
            var d = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(s ?? "", "\"(\\w+)\"\\s*:\\s*(\"(?<sv>[^\"]*)\"|(?<nv>[-0-9.eE+]+))"))
                d[m.Groups[1].Value] = m.Groups["sv"].Success ? m.Groups["sv"].Value : m.Groups["nv"].Value;
            return d;
        }
        private static string Get(Dictionary<string, string> d, string k)
        { string v; return d.TryGetValue(k, out v) ? v : null; }

        private static void Respond(HttpListenerContext ctx, int code, string json)
        {
            try
            {
                byte[] b = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode = code;
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(b, 0, b.Length);
                ctx.Response.Close();
            }
            catch { }
        }
    }
}
