# TV → NinjaTrader Webhook Bridge

Automate any TradingView strategy into NinjaTrader 8 — no paid middleman
service, no separate bridge app. A small add-on gives NinjaTrader a webhook
listener; a companion strategy executes the signals with an on/off toggle per
strategy in Control Center.

```
TradingView (your strategy runs here — entries, trailing stops, exits)
      │  webhook POST (JSON) on every order fill
      ▼
ngrok tunnel (free public HTTPS URL → your PC)
      │
      ▼
TV Webhook Bridge  (add-on inside NinjaTrader: authenticates + routes)
      │
      ▼
TV Signal Executor (NT strategy: places orders — your on/off switch)
      │
      ▼
Your account (Sim101 first. Always Sim101 first.)
```

**Division of labor:** TradingView is the brain — it runs your exit logic and
sends exit webhooks. NinjaTrader holds a *resting* stop/target bracket from
the entry payload as a **disaster backstop** in case your internet,
TradingView, or the tunnel dies mid-trade. The executor also force-flattens
at a configurable NY time as a final safety net.

## Requirements

- NinjaTrader 8 (Windows) with a data connection
- A TradingView plan with webhook alerts (Essential or higher)
- A free [ngrok](https://ngrok.com) account (one static domain included)

## Install (10 minutes)

### 1. The two NinjaScript files

1. NT8 Control Center → New → **NinjaScript Editor**
2. Right-click **AddOns** → New AddOn → name it `TvWebhookBridge` → replace
   ALL generated code with `TvWebhookBridge.cs`
3. Edit the CONFIG block: set `SecretToken` to a long random string
   (40+ characters — this is the password protecting your account; generate
   one with a password manager or `openssl rand -hex 32`)
4. Right-click **Strategies** → New Strategy → name it `TvSignalExecutor` →
   click through the wizard → replace ALL generated code with
   `TvSignalExecutor.cs`
5. Press **F5** to compile, then **restart NinjaTrader**
6. New → NinjaScript Output should show:
   `[TvBridge] Bridge listening on http://localhost:8787/webhook`

### 2. The executor strategy

Control Center → **Strategies** tab → right-click → New Strategy… →
**"TV Signal Executor"**:

- **Instrument**: the contract to trade (e.g. `MNQ 09-26`), 1-minute series
- **Account**: `Sim101` until everything is verified
- **Signal tag**: a short name, e.g. `MYSTRAT` — must match your Pine JSON
- **TradingView symbol**: your TV chart's ticker, e.g. `MNQ1!`
- Enable it. Output shows `Executor ONLINE: MYSTRAT|MNQ1!`

The Enabled checkbox is your kill switch. One executor instance per
TradingView strategy; give each its own tag.

### 3. The tunnel

```powershell
ngrok config add-authtoken <your token from the ngrok dashboard>
ngrok http 8787 --url=YOUR-DOMAIN.ngrok-free.app --host-header=rewrite
```

Claim your free static domain in the ngrok dashboard first. The
`--host-header=rewrite` flag is REQUIRED — Windows' HTTP listener rejects
requests whose Host header isn't "localhost" (you'll see
`400 Invalid Hostname` without it). Leave the window running; your webhook
URL is `https://YOUR-DOMAIN.ngrok-free.app/webhook?token=YOUR_SECRET_TOKEN`.

### 4. Test WITHOUT TradingView (on Sim101!)

```powershell
# health check — lists which executors are registered
Invoke-RestMethod "https://YOUR-DOMAIN.ngrok-free.app/health" -Headers @{"ngrok-skip-browser-warning"="1"}

# fake entry (use sane sl/tp near the current market price!)
Invoke-RestMethod -Method Post -ContentType "application/json" -Headers @{"ngrok-skip-browser-warning"="1"} -Uri "https://YOUR-DOMAIN.ngrok-free.app/webhook?token=YOUR_SECRET_TOKEN" -Body '{"action":"buy","symbol":"MNQ1!","qty":1,"sl":20000,"tp":22000,"strategy":"MYSTRAT"}'

# fake exit — cancels the bracket and flattens
Invoke-RestMethod -Method Post -ContentType "application/json" -Headers @{"ngrok-skip-browser-warning"="1"} -Uri "https://YOUR-DOMAIN.ngrok-free.app/webhook?token=YOUR_SECRET_TOKEN" -Body '{"action":"exit","symbol":"MNQ1!","strategy":"MYSTRAT"}'
```

Pause between commands and verify each step in NT (Positions and Orders
tabs). Everything is logged to the NinjaScript Output window and
`Documents\TvBridge.log`.

### 5. Wire your strategy for webhooks

Your Pine strategy already decides *when* to trade — it just needs to announce
every decision in JSON the bridge understands. That means attaching an
`alert_message` to **every** order command (`strategy.entry`, `strategy.close`,
`strategy.exit`, `strategy.close_all`). Miss one and that fill sends an empty
webhook that errors out. See `examples/pine_alert_example.pine` for a finished
strategy.

You don't have to write this yourself. Paste the strategy you found into any AI
assistant (Claude, ChatGPT) with the prompt below, then verify the result with
**The Check** that follows.

**The prompt** — paste your whole strategy where it says to:

```text
You are helping me connect a TradingView Pine Script strategy to NinjaTrader
through a webhook bridge. I'll paste my strategy at the bottom. Modify it to
send webhook alerts, following these rules EXACTLY:

1. Do NOT change any trading logic, indicators, or settings. Only ADD the
   webhook messaging. The strategy must behave identically.

2. Near the top, after the inputs, add these three inputs:
      whTag    = input.string("MYSTRAT", "Strategy Tag")
      whSymbol = input.string("MNQ1!",   "Routing Symbol")
      qtyInput = input.int(1, "Order Quantity", minval=1)
   Then build three JSON message strings (use str.tostring() for numbers):
      BUY : {"action":"buy","symbol":"<whSymbol>","qty":<qtyInput>,"strategy":"<whTag>"}
      SELL: {"action":"sell","symbol":"<whSymbol>","qty":<qtyInput>,"strategy":"<whTag>"}
      EXIT: {"action":"exit","symbol":"<whSymbol>","strategy":"<whTag>"}

3. Attach an alert_message to EVERY order command — every strategy.entry,
   strategy.order, strategy.close, strategy.close_all, and strategy.exit.
   This is critical: the TradingView alert fires on every order fill, so any
   order left without a message sends a broken, empty webhook that errors out.
      - Long entries  -> BUY message
      - Short entries -> SELL message
      - Every close and every protective exit (stop, target, trailing) -> EXIT message

4. Only use these three actions: buy, sell, exit. Use "exit" for all closes.

5. If the strategy ever closes only PART of a position (a scale-out / partial
   exit), STOP and tell me — that needs special handling, don't just use exit.

6. Output the full modified script in one code block. Then list every order
   line you added a message to, so I can double-check nothing was missed.

Here is my strategy:
[PASTE THE WHOLE STRATEGY HERE]
```

**The Check** — before you trust the wired strategy:

1. **Names match.** The strategy's settings now show a **Strategy Tag** and
   **Routing Symbol** input. Set them to the pair your executor registered —
   the `/health` endpoint shows it (e.g. `MYSTRAT|MNQ1!`).
2. **Nothing missed.** Skim the AI's list of changed lines: every
   `strategy.entry`, `strategy.close`, and `strategy.exit` should have an
   `alert_message`.
3. **Watch it on Sim101.** Add the strategy to your chart, create the alert
   (step 6), and watch `TvBridge.log`: an entry logs `BUY`/`SELL`, an exit logs
   `FLATTEN`, all tagged `[MYSTRAT|MNQ1!]`.

If the log shows `missing action/symbol/strategy`, an order line got no message
(or the alert isn't sending `{{strategy.order.alert_message}}`) — re-run the
prompt. If it shows `no enabled strategy for …`, the tag/symbol don't match —
fix the two inputs.

### 6. The TradingView alert

Create ONE alert:

- **Condition**: your strategy → **"Order fills and alert() function calls"**
- **Message**: exactly `{{strategy.order.alert_message}}`
- **Webhook URL**: `https://YOUR-DOMAIN.ngrok-free.app/webhook?token=YOUR_SECRET_TOKEN`

## The JSON protocol

| field    | type   | required        | meaning                                        |
|----------|--------|-----------------|------------------------------------------------|
| action   | string | always          | `buy` `sell` `reduce` `exit` `flatten`          |
| symbol   | string | always          | TV ticker, matched against the executor         |
| strategy | string | always          | tag, matched against the executor               |
| qty      | int    | buy/sell/reduce | contracts                                       |
| sl       | price  | optional        | resting stop-loss backstop (0 = none)           |
| tp       | price  | optional        | resting profit target (0 = none)                |

`buy`/`sell` close any opposite position, enter at market, and attach the
bracket. `reduce` scales out part of the position. `exit`/`flatten` cancel
working orders and close.

## Operational rules (learn these before going live)

1. **Alerts snapshot your settings.** Any change to the strategy's inputs or
   code → DELETE and RE-CREATE the alert, or it silently trades the old
   configuration. This is the #1 silent failure.
2. **The stack must be running**: NinjaTrader (executor enabled, data
   connected), ngrok, and an awake PC. Use Task Scheduler to start ngrok at
   logon; set Windows to never sleep; mind Windows Update reboots.
3. **Quarterly rollover**: restart the executor instance on the new front
   month contract (Mar/Jun/Sep/Dec).
4. **Reconcile daily**: `TvBridge.log` vs TradingView's trade list.
5. **Sim101 until proven.** Then small size. Prop firm users: check your
   firm's automation rules first — many restrict or forbid it.

## Troubleshooting

| symptom                          | cause / fix                                                        |
|----------------------------------|--------------------------------------------------------------------|
| `400 Invalid Hostname`           | ngrok missing `--host-header=rewrite`                              |
| `401 unauthorized`               | token in URL ≠ `SecretToken` in the add-on                        |
| HTML page instead of JSON        | ngrok free-tier browser warning — add the skip header (tests only; TradingView is unaffected) |
| `ERR_NGROK_3200 endpoint offline`| ngrok agent not running                                            |
| "no enabled strategy for …"      | executor disabled, or tag/symbol don't match the payload           |
| `missing action/symbol/strategy` | a Pine order has no `alert_message`, or the alert isn't sending `{{strategy.order.alert_message}}` |
| orders fill but nothing on chart | chart instrument/account ≠ executor's, or prices off-screen        |
| signals stop after a settings change | you didn't re-create the alert (rule #1)                       |

## ⚠️ Disclaimer

This is trading software provided **as-is, without warranty of any kind**.
Futures trading involves substantial risk of loss. Automated systems can and
do fail — connectivity drops, software bugs, exchange issues, and
configuration mistakes can all produce unexpected orders and losses. You are
solely responsible for every order this software places. Test thoroughly on
a simulation account, supervise it in operation, and never risk money you
cannot afford to lose. Nothing here is financial advice.

## License

MIT — see [LICENSE](LICENSE).
