// ═══════════════════════════════════════════════════════════════════════════
//  TV Signal Executor — NinjaTrader 8 STRATEGY
//
//  Executes TradingView webhook signals routed by the TV Webhook Bridge
//  add-on (TvWebhookBridge.cs). This is the on/off switch: enable or disable
//  this strategy in Control Center → Strategies and signals for its tag are
//  executed or ignored accordingly. Run one instance per TradingView
//  strategy, each with its own Signal tag.
//
//  INSTALL:
//    1. NinjaScript Editor → Strategies → New Strategy → name it
//       TvSignalExecutor → replace ALL generated code with this file → F5.
//    2. Control Center → Strategies tab → right-click → New Strategy…
//         · Strategy:   "TV Signal Executor"
//         · Instrument: the contract to trade (e.g. MNQ 09-26), any intraday
//                       data series (1-minute is fine)
//         · Account:    Sim101 FIRST. Always. Then your live/eval account.
//         · Signal tag / TV symbol: must match the "strategy" and "symbol"
//           fields in your TradingView alert JSON.
//         · Enable it. The bridge /health endpoint lists it as registered.
//    3. Toggle the Enabled checkbox any time to turn execution on/off.
//
//  Execution model (managed orders):
//    buy / sell → close any opposite position, market entry, then attached
//                 stop-loss and/or profit-target (OCO) at the payload's
//                 sl/tp prices. sl and tp are both optional (0 = none).
//    reduce     → partial exit of the payload's qty (scale-out).
//    exit /
//    flatten    → cancel bracket + close the position.
//    EOD        → the strategy force-flattens itself at the configured NY
//                 time, independent of TradingView (safety backstop).
//
//  NOTE: TradingView remains the "brain" — it runs your exit logic (trailing
//  stops, timeouts, targets) and sends exit webhooks. The resting bracket in
//  NinjaTrader is a DISASTER BACKSTOP for when your internet/TradingView/
//  tunnel dies mid-trade, not the primary exit mechanism.
// ═══════════════════════════════════════════════════════════════════════════
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.AddOns;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class TvSignalExecutor : Strategy
    {
        private string hubKey;

        // ───────────── user-configurable properties ─────────────
        [NinjaScriptProperty]
        [Display(Name = "Signal tag", Description = "Must match the \"strategy\" field in the TradingView alert JSON", Order = 1, GroupName = "Signal")]
        public string SignalTag { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "TradingView symbol", Description = "Must match the \"symbol\" field in the alert JSON (e.g. MNQ1! or NQ1!)", Order = 2, GroupName = "Signal")]
        public string TvSymbol { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Override quantity (0 = use payload qty)", Order = 3, GroupName = "Risk")]
        public int OverrideQty { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Max quantity (sanity cap)", Order = 4, GroupName = "Risk")]
        public int MaxQty { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EOD flatten enabled", Order = 5, GroupName = "Risk")]
        public bool EodFlatten { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EOD flatten time (NY, HH:mm)", Order = 6, GroupName = "Risk")]
        public string EodTime { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                         = "TV Signal Executor";
                Description                  = "Executes TradingView webhook signals from the TV Webhook Bridge.";
                Calculate                    = Calculate.OnBarClose;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = false;
                BarsRequiredToTrade          = 0;
                StartBehavior                = StartBehavior.WaitUntilFlat;

                SignalTag   = "MYSTRAT";
                TvSymbol    = "MNQ1!";
                OverrideQty = 0;
                MaxQty      = 5;
                EodFlatten  = true;
                EodTime     = "15:58";
            }
            else if (State == State.Realtime)
            {
                hubKey = TvSignalHub.Key(SignalTag, TvSymbol);
                TvSignalHub.Register(hubKey, OnWebhookSignal);
                TvBridgeLog.Log("Executor ONLINE: " + hubKey + " → " + Instrument.FullName + " / " + Account.Name);
            }
            else if (State == State.Terminated)
            {
                if (hubKey != null)
                {
                    TvSignalHub.Unregister(hubKey);
                    TvBridgeLog.Log("Executor OFFLINE: " + hubKey);
                    hubKey = null;
                }
            }
        }

        // signals arrive on the bridge's HTTP thread → marshal into the
        // strategy's own thread context before touching orders
        private void OnWebhookSignal(Dictionary<string, string> payload)
        {
            TriggerCustomEvent(o => Process((Dictionary<string, string>)o), payload);
        }

        private void Process(Dictionary<string, string> j)
        {
            if (State != State.Realtime) return;

            string action = Get(j, "action");
            switch (action)
            {
                case "buy":
                case "sell":
                {
                    int qty = OverrideQty > 0 ? OverrideQty : ParseInt(Get(j, "qty"), 1);
                    if (qty < 1 || qty > MaxQty)
                    { TvBridgeLog.Log("REJECTED qty " + qty + " (cap " + MaxQty + ")"); return; }

                    double sl = Round(ParseDouble(Get(j, "sl")));
                    double tp = Round(ParseDouble(Get(j, "tp")));
                    bool hasSl = sl > 0, hasTp = tp > 0;

                    bool isLong = action == "buy";
                    // one position at a time: clear opposite exposure first
                    if (Position.MarketPosition == MarketPosition.Long  && !isLong) ExitLong();
                    if (Position.MarketPosition == MarketPosition.Short &&  isLong) ExitShort();

                    // Bracketed entries use signal name "BRKT"; entries with no
                    // levels use "NAKED" so no stale Set* settings apply to them.
                    // A missing side is pushed absurdly far away (100000 ticks)
                    // because SetStopLoss/SetProfitTarget persist per signal name.
                    string entryName = (hasSl || hasTp) ? "BRKT" : "NAKED";
                    if (hasSl || hasTp)
                    {
                        if (hasSl) SetStopLoss("BRKT", CalculationMode.Price, sl, false);
                        else       SetStopLoss("BRKT", CalculationMode.Ticks, 100000, false);
                        if (hasTp) SetProfitTarget("BRKT", CalculationMode.Price, tp);
                        else       SetProfitTarget("BRKT", CalculationMode.Ticks, 100000);
                    }

                    if (isLong) EnterLong(qty, entryName);
                    else        EnterShort(qty, entryName);

                    TvBridgeLog.Log((isLong ? "BUY " : "SELL ") + qty + " " + Instrument.FullName
                        + (hasSl ? " SL " + sl : " no-SL") + (hasTp ? " / TP " + tp : " / no-TP")
                        + " [" + hubKey + "]");
                    break;
                }
                case "reduce":
                {
                    int rqty = ParseInt(Get(j, "qty"), 1);
                    if (rqty < 1) { TvBridgeLog.Log("REJECTED reduce qty " + rqty); return; }
                    if (Position.MarketPosition == MarketPosition.Long)
                        ExitLong(rqty, "scale", "");
                    else if (Position.MarketPosition == MarketPosition.Short)
                        ExitShort(rqty, "scale", "");
                    else
                    { TvBridgeLog.Log("reduce ignored: flat"); return; }
                    TvBridgeLog.Log("REDUCE " + rqty + " " + Instrument.FullName + " [" + hubKey + "]");
                    break;
                }
                case "exit":
                case "flatten":
                    Flatten("signal " + action);
                    break;
                default:
                    TvBridgeLog.Log("REJECTED unknown action '" + action + "'");
                    break;
            }
        }

        private void Flatten(string why)
        {
            if (Position.MarketPosition == MarketPosition.Long)  ExitLong();
            if (Position.MarketPosition == MarketPosition.Short) ExitShort();
            TvBridgeLog.Log("FLATTEN " + Instrument.FullName + " (" + why + ") [" + hubKey + "]");
        }

        // EOD backstop runs on the strategy's own bars, independent of TradingView
        private DateTime lastEod = DateTime.MinValue;
        protected override void OnBarUpdate()
        {
            if (State != State.Realtime || !EodFlatten) return;
            TimeSpan flat;
            if (!TimeSpan.TryParse(EodTime, out flat)) return;

            TimeZoneInfo ny = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            DateTime nowNy  = TimeZoneInfo.ConvertTime(DateTime.UtcNow, ny);
            if (nowNy.TimeOfDay >= flat && lastEod.Date != nowNy.Date
                && Position.MarketPosition != MarketPosition.Flat)
            {
                lastEod = nowNy;
                Flatten("EOD backstop " + EodTime);
            }
        }

        // ───────────── helpers ─────────────
        private double Round(double p) { return Instrument.MasterInstrument.RoundToTickSize(p); }
        private static string Get(Dictionary<string, string> d, string k)
        { string v; return d.TryGetValue(k, out v) ? v : null; }
        private static int ParseInt(string s, int dflt)
        { int v; return int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : dflt; }
        private static double ParseDouble(string s)
        { double v; return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0; }
    }
}
