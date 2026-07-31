# pi-harness capability inventory — what must the live environment provide?

Research for issue #3 (map issue #1: live-first reboot). Question: **could pi-harness be
built and evolved inside the new live environment, with no restarts and no browser
refresh?** This is a local-code inventory of the pi-harness repo
(`C:/Users/Filip/Documents/pi-harness`); every claim cites a file there.

pi-harness today: ASP.NET Core (.NET 10) Host + SignalR, Blazor WASM + MudBlazor client,
Pi extension/WebSocket live bridge, herdr for session placement, TUnit + Playwright tests
(`README.md` "Stack"). The old Host-parented Pi RPC path is retired — `src/PiHarness.PiRpc/`
now contains only `obj/`, and invariant I3/I4 says production control is live bridge +
herdr with `NullPiSessionFactory` (`docs/protocol.md` §0).

## 1. Outbound-dialed WebSocket bridge (server socket the environment must host)

- The Host runs a **raw WebSocket listener** (`HttpListener`-based) that external Pi
  extension processes **dial into**: `ws://127.0.0.1:17777/bridge`
  (`docs/live-bridge.md` "Topology", "Host config";
  `src/PiHarness.Host/Bridge/LiveBridgeListener.cs`).
- Bind rules: loopback only — non-loopback refuses to start the listener
  (`docs/live-bridge.md` "Bind rules"; `LiveBridgeListener.cs` `IsLoopbackBind`).
  This is a **second port**, separate from the web UI port.
- Auth: shared local token (`LiveBridge:Token` / `PI_HARNESS_BRIDGE_TOKEN`); bad token →
  reject and close (`docs/live-bridge.md` "Auth").
- Wire protocol `pi-harness-bridge/1`: versioned JSON text frames. Pi→Host:
  `register` (first message, requires Pi session UUID), `event`, `goodbye`. Host→Pi:
  `hello`, `error`, `ack`, `control`; Pi→Host `control_result`
  (`docs/live-bridge.md` "Wire protocol").
- Control actions Host sends over the bridge: `prompt`, `steer`, `follow_up`, `abort`,
  `set_model`, `set_thinking_level`, `set_session_name`, `new_session`, `reload`,
  `compact`, `list_models`, `list_commands`, `get_usage`
  (`docs/live-bridge.md` "Control actions").
- **Reconnect is client-side with exponential backoff**: the extension retries with
  `delay = min(30s, 500ms × 2^min(attempt, 6))`
  (`extensions/pi-harness-bridge/index.ts:775-786`). Reconnecting the same bridge
  identity restores the same harness `sessionId` in the Live band
  (`docs/protocol.md` "ListPiSessionPresence").

**Live-env requirement:** host long-lived inbound WS server sockets on a configurable
extra port, with token auth, and — critically — survive a **live update of the handler
code without dropping the port binding**, or at minimum rely on the peers' backoff
reconnect and re-`register` to heal. Session identity must survive the reconnect
(identity = Pi UUID, not the socket).

## 2. Spawning sessions via herdr (external process orchestration)

- Product "New" never parents Pi: mint `pi-{guid}` → optimistic presence row
  `state=starting` → background `herdr tab create --cwd … --label … --no-focus` →
  `pane send-text` the `pi.cmd --session-id {uuid}` command → `pane send-keys` →
  poll `herdr agent list` until an agent appears on the pane, then wait for bridge
  `register` on the same `sessionId`; failure → `state=failed`
  (`docs/protocol.md` §2.1 "StartPiSession"; `src/PiHarness.Host/Sessions/HerdrClient.cs`
  — thin CLI wrapper, JSON-over-stdout parsing, with a Windows workaround: `herdr agent
  start --kind pi` fails on npm `pi.cmd`, so tab-create + send-text is used instead).
- Hard rule: harness must not be the direct process parent when a mux is available
  (`docs/fm-handoff.md` §2 "Harness spawn rule"). `CreateSession` RPC exists for FakePi
  tests only (`docs/protocol.md` §2.1; `docs/live-bridge.md` smoke item 7).
- Destructive Close terminates the reported local Pi **process tree**, drops the bridge
  socket, and removes registry entries atomically; it fails rather than claim success if
  termination can't be confirmed (`docs/protocol.md` "CloseSession").

**Live-env requirement:** shell out to external CLIs (spawn processes, capture stdout,
parse JSON), run background async workflows with polling/timeouts, and perform
**controlled termination of external process trees** — all from app code running inside
the environment.

## 3. Real-time streaming transcript (event model + rates)

- Event types flowing bridge → Host → SignalR → browser: `assistant_text_delta`,
  `thinking_delta`, `tool_start` / `tool_update` / `tool_end`, `user` (finalized text,
  amends the optimistic bubble), `turn_end`, `agent_start` / `agent_settled`, `error`,
  `status`, plus meta events `usage_meta`, `context_meta`, `name_meta`
  (`docs/live-bridge.md` "Event types"; `docs/protocol.md` §2.2 "SessionEvent").
- **Rates:** deltas arrive per token. The client **coalesces UI notifies to ~33 ms** for
  `assistant_text_delta` / `thinking_delta` / `tool_update` so the UI is not re-rendered
  once per token; structural events (`user`, `turn_end`, `error`) notify immediately
  (`docs/perf-transcript.md` "UI notify coalescing";
  `src/PiHarness.Client/Services/HarnessHubClient.cs:36`
  `UiNotifyCoalesce = TimeSpan.FromMilliseconds(33)`).
- Ordering: monotonic `serverSeq` per session; user sends append + broadcast **before**
  Pi delivery; per-session serial outbound queue to Pi
  (`docs/protocol.md` §1, §2.1 "SendMessage"; `docs/decisions.md` §3).
- Soak scale the code is tested at: Host 20 turns × 50 deltas vs 60 × 50; client 40 × 50
  vs 120 × 50 synthetic events; the check is **scaling** (3× flood must cost < ~8×), not
  an absolute ms budget (`docs/perf-transcript.md` "Defaults", "FakePi flood knobs").

**Live-env requirement:** a server→browser push channel (SignalR-equivalent) that
delivers ordered, resumable event streams at token rate; client-side render coalescing
(or an equivalent scheduler) so a token stream does not force a re-render per event; and
in-place mutation of the last message rather than a node per delta
(`docs/ux-chat-scroll.md` "Stream tokens update the last assistant message in place").
For deenv this is the stress case for the reactive render path: **high-frequency partial
updates to one value must not re-run the whole view**.

## 4. Transcript projection, replay, and the 50-message window

- Events are transport only; `ChatTranscript` (shared Host+client) projects them into
  bubbles; raw deltas are not kept after apply; both sides trim to a **50 user/assistant
  message window** (`docs/perf-transcript.md` "Client model", "Host + client mirror";
  `src/PiHarness.Shared/Sessions/ChatTranscript.cs:11` `MessageWindow = 50`).
- Join with a stale cursor returns **Replace + synthetic wire snapshot**, not a delta
  log; deeper history comes on demand from Pi JSONL via `LoadSessionHistory`
  (default max 200 messages) (`docs/protocol.md` §2.1 "JoinSession",
  "LoadSessionHistory"; `src/PiHarness.Host/Bridge/SessionJsonlHistoryReader.cs`).
- Apply-once client: ignore `serverSeq <= lastServerSeq`; user bubbles keyed by
  `clientMsgId`, optimistic + confirmed must merge, never duplicate
  (`docs/protocol.md` §3 "Apply-once"; `docs/ux-chat-scroll.md` "Message identity").

**Live-env requirement:** bounded projections over unbounded streams, snapshot+cursor
resume semantics, and stable row identity across optimistic/confirmed states.

## 5. Markdown rendering

- Assistant text is markdown → sanitized HTML: Markdig with advanced extensions and
  `DisableHtml`, then an HtmlSanitizer allow-list (tags: p, code, pre, tables, headings,
  a, …; attrs: `href`, `class`; schemes: http/https/mailto; no data attributes)
  (`src/PiHarness.Client/Services/ChatMarkdownRenderer.cs`).
- Markdown reflow mid-stream is a known scroll risk — re-pin on resize handles it
  (`docs/ux-chat-scroll.md` "Risks to watch").

**Live-env requirement:** a markdown-to-safe-HTML capability usable from app code (or a
built-in rich-text component), re-rendered incrementally while the source text streams.

## 6. Slash commands and session controls

- `/` autocomplete is fed **only** by Pi `get_commands` via hub `ListCommands`; no
  client-invented built-ins; slash-like text forces effective mode=`prompt` server-side
  (`docs/protocol.md` §2.1 "ListCommands", "SendMessage" slash force-prompt;
  `docs/decisions.md` §5).
- First-class controls: `ListModels` / `SetModel` / `SetThinkingLevel` /
  `CompactSession` (rejects while busy) / `SetSessionName`; mutations refresh meta via
  `get_state` and broadcast `SessionStatus` so all clients update chips without a second
  stream (`docs/protocol.md` §2.1 "ListModels / SetModel / …").
- Send modes: `steer` (default; busy → mid-run inject), `followUp` (after settle),
  `prompt` (errors when busy); "busy" = until `agent_settled`, not `turn_end`
  (`docs/decisions.md` §4).

**Live-env requirement:** dynamic, data-driven UI (autocomplete list fetched at runtime
from an external process), toolbar/menu components, and server-authoritative validation
of client input modes.

## 7. Multi-session, multi-client

- One unified Live/Closed session list, **server-authoritative pushed snapshots**
  (`PiSessionListChanged` after register/disconnect/reconnect/close), clients
  snapshot-sync after reconnect, no polling (`docs/protocol.md`
  "ListPiSessionPresence"; `docs/decisions.md` §2).
- Many UI clients may Join and Send on one session; Join never acquires a lock (I6);
  sends serialized per session; exactly one Pi writer per session file (I5, I9)
  (`docs/protocol.md` §0; `docs/decisions.md` §3).
- Client identity: `clientId` in localStorage, sent on `Hello`; resume key =
  `(clientId, sessionId)` with `lastDeliveredSeq` (`docs/protocol.md` §1, §3).

**Live-env requirement:** N browser clients × M sessions with server-pushed list state,
per-client resume cursors, and per-session serialization — i.e. real multi-client
fan-out, not one-socket-one-view.

## 8. Mobile + Tailscale access

- Phone access = bind `0.0.0.0:5242` ("tailscale" launch profile,
  `src/PiHarness.Host/Properties/launchSettings.json:22-27`) and browse
  `http://<tailscale-ip>:5242` — plain http, auth is "private network / Tailscale" only
  (`README.md` "Run", "Non-goals").
- Responsive UI decisions: drawer overlay closed by default on mobile, full-width
  composer, coarse-pointer Enter≠send handling, connection chip instead of an app bar
  (`docs/decisions.md` §1, §5; `README.md` "Done": mobile full-width composer).

**Live-env requirement:** configurable bind address (not localhost-locked), usable over
plain http on a private network, and a UI layer capable of pointer-type/viewport
adaptive layout. A second device joining is just `Join` — same session, no new writer
(`README.md` "Run": "Prefer Join on a second device").

## 9. State: what survives restart today vs what is lost

Survives (source of truth outside the Host):

- **Pi JSONL session files** — transcripts; history backfill reads them
  (`README.md` "Non-goals" Slice B: "Pi JSONL is source of truth";
  `src/PiHarness.Host/Bridge/SessionJsonlHistoryReader.cs`).
- **Browser `clientId`** — localStorage (`docs/protocol.md` §1).
- Live Pi processes themselves — they redial the bridge and re-`register`, rebuilding
  the Live band (`README.md` Slice B: "Live/Attach rebuilds the Host layer").

Lost on Host restart (in-memory by design):

- **Harness registry** (`sessionId` ↔ Pi path, Live/Closed bands, append order) —
  `ConcurrentDictionary` state (`src/PiHarness.Host/Sessions/SessionRegistry.cs:16-26`);
  persisting it is explicitly **non-goal Slice B** (`README.md` "Non-goals").
- **Host event log / 50-message mirror and `serverSeq`** — "monotonic per `sessionId`
  for the Host lifetime" (`docs/protocol.md` §1).
- **Client outbox** — in-memory `ConcurrentDictionary`
  (`src/PiHarness.Shared/Client/ClientOutbox.cs:11`); "Outbox survive page refresh
  (today: in-memory)" is an open M6+ TODO (`README.md` "Todo").
- Closed-band retained transcripts, per-client `lastDeliveredSeq`, seen `clientMsgId`
  dedupe sets (`docs/protocol.md` §3 "Server state").

**Live-env requirement:** this is exactly the gap the live environment abolishes. deenv's
"always restores to its exact last state" claim means the registry, event log, seq
counters, and outbox would be **durable by default** — Slice B and the outbox TODO
disappear as categories, not as features someone must build.

## 10. Documented perf/UX pain points (numbers from the docs)

- **Virtualized list was abandoned**: previous harness work "fought a virtualized list
  (`followOutput`, manual `scrollToIndex`, DOM fallbacks, history prepend)"; streaming
  height changes made at-bottom detection unreliable. v1 = no virtualizer, plain
  overflow container (`docs/ux-chat-scroll.md` "Problem we are avoiding";
  invariants I15–I17 `docs/protocol.md` §0). "Virtualized message list — only if
  measured necessary" remains a TODO (`README.md`).
- **Window numbers**: 50 user/assistant messages pinned window (thoughts/status/errors
  don't consume quota); while scrolled up the window start freezes and grows; Latest
  trims back to 50 (`docs/ux-chat-scroll.md` "v1 approach").
- **Stick-to-bottom**: single `StickToBottom` bool, bottom sentinel, unstick threshold
  ~80 px scroll-up, one pin-to-end code path — "if any of these flake, fix the
  controller — do not add a second scroll strategy" (`docs/ux-chat-scroll.md`).
- **Notify coalescing**: ~33 ms for stream deltas (`docs/perf-transcript.md`;
  `HarnessHubClient.cs:36`).
- **Scaling gate**: 3× flood must not cost more than ~8× the small run — catches
  accidentally-quadratic work without claiming an FPS budget
  (`docs/perf-transcript.md` "Defaults").
- **Not yet measured**: full Blazor render / markdown cost in-browser, and the real
  bridge WebSocket path (`docs/perf-transcript.md` "What this does not cover yet").
- **Known limits**: dead providers hang on "Working…" until Pi emits `turn_end` (hang
  visibility is an M6+ TODO); compact does not clear harness chat history
  (`README.md` "Known limits", "Todo").

## 11. Miscellaneous host capabilities the app leans on

- **Config + secrets**: `appsettings.json` sections (`LiveBridge`, herdr options) and
  env-var overrides (`PI_HARNESS_BRIDGE_TOKEN`) (`docs/live-bridge.md` "Host config",
  "Auth"; `src/PiHarness.Host/Bridge/LiveBridgeOptions.cs`).
- **Outbound HTTPS to third-party APIs**: subscription usage polling reads local auth
  files (`~/.pi/agent/auth.json`, `~/.claude/.credentials.json`) and calls provider
  endpoints (Grok billing, Anthropic OAuth usage, ChatGPT wham usage), active session
  only, ≥5 minutes between fetches, fail-soft per provider
  (`docs/live-bridge.md` "Subscription usage";
  `extensions/pi-harness-bridge/subscription-usage.ts`).
- **Filesystem reads outside the app dir**: Pi JSONL under the Pi home, auth files under
  user profile (`docs/live-bridge.md`; `SessionJsonlHistoryReader.cs`).
- **Background services**: the bridge listener is a `BackgroundService` running for the
  Host's lifetime (`LiveBridgeListener.cs`).
- **E2E testability**: Playwright against `data-testid` / `data-stick` attributes;
  FakePi behind the same `IPiSession` DI seam, test-only (`docs/ux-chat-scroll.md`
  "Implementation notes"; `docs/protocol.md` I4; `README.md` "Stack").

## Capability checklist (condensed)

For pi-harness to be built and evolved **inside** the live environment without restarts
or browser refreshes, the environment must provide:

1. **Inbound WS server sockets** on a configurable second port with token auth, whose
   app-level handlers can be live-updated; peer reconnect (500 ms → 30 s exponential
   backoff) + identity re-`register` is the healing path (§1).
2. **Versioned JSON wire-protocol handling** — parse/dispatch frames, send control
   frames, correlate `control_result` by id (§1).
3. **External process orchestration** — spawn CLIs (herdr), parse JSON stdout, poll
   with timeouts, terminate process trees on command (§2).
4. **Token-rate server→client streaming** with ordered seq, resumable cursors, and
   render coalescing (~33 ms) so per-token updates don't re-render the view (§3).
5. **Bounded projections + snapshot/replay** — 50-message windows, Replace-snapshot
   joins, on-demand deep history from external files (§4).
6. **Markdown → sanitized HTML** rendering that updates in place mid-stream (§5).
7. **Runtime-data-driven UI** — slash autocomplete from a live external catalog,
   model/thinking/compact/rename controls, server-authoritative modes (§6).
8. **Multi-session, multi-client fan-out** — pushed list state, per-client resume,
   per-session serialization, one-writer invariants (§7).
9. **Non-localhost binding + responsive UI** — `0.0.0.0` bind, plain-http private
   network access, phone-grade layout (§8).
10. **Durable-by-default state** — registry, event log, seq counters, outbox all
    survive environment restarts; today's in-memory losses (§9) become impossible.
11. **The perf discipline** — no accidental quadratics as transcripts grow, a
    stick-to-bottom-grade scroll story, and known numbers (§10) as regression floors.
12. **Config/secrets, outbound HTTPS, out-of-tree file reads, background services,
    and E2E hooks** (§11).

The single hardest fit for a live-first deenv is #4 + #10 together: token-rate streaming
through a durable, reactive state layer without either re-rendering per token or turning
every delta into a persisted row. pi-harness solves it with transport-only events,
in-memory projection, and JSONL as cold truth — the live environment needs an equivalent
hot/cold split it can express natively.
