# Session progress - 2026-10-18 (RELEASE-READY - all automatable checks green)

## State

- BUG-82 (startup stack overflow) + BUG-83 (MetricsChart frozen-Pen crash)
  fixed and committed (7e24b97); BUG-84 (doctor false-warned MTU/MSS
  hardening) found during release testing, fixed, committed (45e293f).
- Release-tested against the live relay (140.238.99.143, Hetzner): current
  config DEPLOYED (server was pre-phase-two + unhardened before this),
  doctor all green, config hash matches, 1935 reachable, metrics parse,
  phase-two generator output (test 720p rung + loudnorm) accepted by real
  nginx and verified in nginx -T, then the user's real config redeployed.
- App soak: 76 min through every timer tick, zero crash records.
- Twitch API: prefill + category search verified with the stored login
  (no re-login needed).
- Kat confirmed live: Server host set, GO-LIVE WORKS, stream info works.
- Harness (C:\Users\david\AppData\Local\Temp\kilo\ladder-test): 30/30.
- Relay-verify harness (Temp\kilo\relay-verify): 10/10 - reusable for any
  future release check (read-only). relay-deploy harness exists too.

## Remaining for the release session (all small, Kat-timed)

- One-click BACKUP RESTORE test (only ship-feature never clicked end-to-end;
  crypto round-trip is harness-tested). Keep a copy of config.json first.
- Version stamp decision (exe still 0.2.0.0 - bump for first release?).
- Push main if origin should carry the release.
- Deliberately NOT in this release: poll widget (parked, non-affiliate
  channel), Kick/YouTube OAuth (in flight separately), HLS self-view + SRT
  ingest (staged, needs their own tested deploy), music ducking (parked).

Lesson for the next session: after fixing a startup crash, CHECK THE EVENT
LOG for the next crash in line - the bisect's "still crashes" data points
after the ignore list landed were BUG-83, not a failed BUG-82 fix.

## State

- BisectLog instrumentation fully stripped (MainViewModel + Models.cs); the
  ignore-list fix and both BUG-83 edits remain. No bypasses left: 
  SetResourceSummary / HandleVariantHealth are active.
- Clean rebuild: 0 warnings, 0 errors. Launch: HEALTHY, build stamp
  2026-10-17 15:55.
- Committed with updated BUGS.md (BUG-82 + BUG-83 entries).

## Still open (unchanged from the previous handoff)

- Kat's full manual verification checklist (see the 2026-10-11 section
  below), starting with the backup restore test (BUG-76 fix) and the server
  doctor.
- Ladder/round-2 items and the staged HLS self-view + SRT ingest work.

## Earlier 2026-10-11 handoff (phase-two batch details, test checklist)



## Where we are

Phase-two queue cleared of everything that does NOT depend on the in-flight
Kick/YouTube OAuth work OR on relay-bundle changes needing a tested deploy.
Shipped this session (all building clean, details in docs/ROADMAP.md's Shipped
section): **per-destination quality ladder**, **audio processing (normalize +
trim)**, **title/category sync (Twitch first)**, **key rotation reminders**,
**per-platform status board** (with viewer counts), **server resource view**,
**relay metrics history + bandwidth watchdog**, **config backups** (encrypted,
scheduled, one-click restore), **server doctor** (with provider detection,
the firewall checklist and the ufw auto-fix). Generator + metrics + backup
round-trip are assertion-tested by a scratch harness (29/29). The
Kick/YouTube OAuth work stays in flight separately; BUG-49..75 fixes are
already committed (c80e884).

STAGED deliberately (see ROADMAP): HLS self-view and SRT ingest both change
the SERVER BUNDLE (Dockerfile/nginx.conf) - they should land as their own
tested deploy after this batch is verified, not stacked on top of eight
unverified features. OBS portrait-scene handling needs Kat's scene-set
decisions first.

## Shipped this session (commit pending)

- **Per-destination quality ladder** (`Models.QualityVariantConfig`,
  `RelayConfigGenerator` variant paths, `DeployService` variant watchdog,
  Output Studio ladder editor): extra downscaled rungs pushed beside the main
  output under the stream key + suffix. Main output untouched (landscape mains
  stay nginx's plain push - no double-encode). Watchdog watches every rung with
  an aggregated per-destination alarm, separate from the main light.
- **Audio processing per destination**: -16 LUFS loudnorm toggle + volume trim
  (-30..+15 dB) per destination, inherited by variants. Music DUCKING was
  deliberately not shipped (needs an unverified two-track OBS feed; parked on
  the roadmap with the reasoning).
- **Title/category sync (Twitch first)** (`Services/StreamInfoService.cs`,
  Go-live section on the Relay tab): prefilled from the channel's current
  state, debounced category search, Apply via Helix PATCH. Needs the Twitch
  login (scope `channel:manage:broadcast` - logins older than that scope need
  one re-login).
- **Key rotation reminders** (`Config.KeyRotationReminderDays`,
  `DestinationConfig.KeyRotatedAtUtc`): "Key rotated today" button in the
  Output Studio, key age on every destination card, amber nag line on the
  Relay tab with a configurable interval (0 = off).
- **Status board tab**: one grid - on/off, platform, orientation, relay push
  state, platform live state, VIEWERS (new: Twitch GraphQL/Kick/TikTok;
  YouTube deliberately blank - no free API), variant count, key age. Live
  checks now use `CheckDetailedAsync` (`LiveCheckResult`).
- **Server resource view**: the 10s watchdog SSH pass gained a RES section
  (CPU/RAM/disk/net from two /proc samples); green summary line on the Status
  board, "n/a" per unreadable value.
- **Relay metrics history + bandwidth watchdog**
  (`Services/RelayMetricsService.cs`, `Views/MetricsChart.cs`): one /stat
  poll per minute through the persistent SSH session -> 24h ring buffer ->
  line chart on the Status board (in green / out blue). Bandwidth watchdog:
  monthly GB allowance (Relay tab, 0 = off) vs the configured outputs'
  24/7 projection -> amber warning in the bottom status bar.
- **Config backups** (`Services/BackupService.cs`, Relay tab "Toolkit
  backups" section): hourly-interval scheduler (checked every 15 min),
  .katbak = PBKDF2(210k) + AES-GCM of a zip of config.json + the active keys
  file, newest 15 kept, folder of choice. Restore verifies the passphrase,
  writes config + keys (restored config decides where the keys file lands),
  restarts the toolkit. Round-trip + wrong-passphrase tested by the harness.
- **Server doctor** (`Services/ServerDoctor.cs`, Deploy tab): one SSH pass
  checks Docker, kat-relay container, disk, RAM, MTU/MSS hardening, ufw,
  relay-config freshness, and probes TCP 1935 FROM the toolkit machine;
  checklist with dots + fix commands under the deploy log. Provider detection
  (Oracle/AWS/Google/Hetzner metadata) calls out Oracle's console-side
  security lists. "Apply ufw fix" button (only when ufw blocks 1935) allows
  OpenSSH first, then 1935/tcp, and re-runs the checklist.

## Not yet verified (needs Kat)

- Ladder: add 2 variants to a destination -> nginx preview shows the rung
  lines -> deploy -> the rung pushes appear in `docker exec kat-relay nginx -T`
  (or the deploy log). Kill a rung's encoder on the server -> red
  "LADDER VARIANT DOWN" banner names it and the MAIN push light stays green;
  "restart encoder" on that alarm respawns the destination's encoders.
- Audio: loudnorm + trim on one destination -> redeploy -> per-destination
  command preview shows `-af adelay...,loudnorm=I=-16:TP=-1.5:LRA=11,volume=...`
  -> the platform audio is audibly leveled.
- Stream info: title/category boxes prefill from the live Twitch channel;
  category search pops suggestions; Apply to Twitch changes the dashboard.
  If Apply says the login lacks the scope -> one re-login.
- Key rotation: "Key rotated today" -> card shows "key rotated X min ago";
  set the interval to 0 -> the amber warning lists the destination; set it
  back to 90 -> warning clears.
- Status board: viewer counts appear on Twitch/Kick/TikTok while live
  (YouTube row stays blank by design); resource line shows CPU/RAM/disk/net
  once the relay answers.
- Metrics chart: after a minute of relay uptime the Status board chart draws
  in/out lines and the summary line grows samples; set a monthly allowance
  below the projected usage -> the bottom-bar amber BANDWIDTH warning appears.
- Backups: set a passphrase + interval -> "Back up now" writes a .katbak to
  the folder; "Restore from backup..." with the passphrase shows the confirm
  dialog, with a WRONG passphrase shows the plain error (and writes nothing);
  restore replaces config+keys and restarts the toolkit. Keep a copy of the
  pre-restore config.json when first testing restore.
- Server doctor: "Server doctor" on the Deploy tab -> checklist appears under
  the log with the summary in the expander header; the 1935 probe must say
  OK (else the provider-firewall text names the fix). If ufw blocks 1935, an
  "Apply ufw fix" button appears next to the doctor button (passwordless sudo
  required) and the checklist re-runs green afterwards.
- Deliberate behavior change to re-check: the server test encoder and the
  server-side live preview of a PLAIN LANDSCAPE destination now show the full
  frame instead of an accidental 9:16 center-crop (side effect of the ladder's
  graph branch - the production push was always the untouched copy).

## Known next steps (docs/ROADMAP.md)

- STAGED, in order: **HLS self-view + SRT ingest** (both change the relay
  bundle - implement + deploy as their own verified step), then **OBS
  portrait-scene handling** (needs Kat's scene-set decisions).
- Blocked on the OAuth work (in flight): Kick/YouTube logins, then their
  moderation/alert/title-sync equivalents.
- Deferred inside audio processing: music ducking (two-track OBS feed through
  nginx-rtmp needs proving first).

## Environment gotchas (carried over)

- **Close the running toolkit before `dotnet build`** - exe lock (build stamp
  in the title bar tells stale binaries apart).
- Exclusive-fullscreen games swallow mouse input - borderless for overlay use.

---

# Session progress - 2026-10-11 (handoff note)

## Where we are

Phase-two push: **OBS scene auto-switching on alerts** and the
**poll/prediction widget** are implemented and building clean. Verification
below is still pending. The Kick/YouTube OAuth work is in flight separately
(driven by Kat).

## Shipped this session

- **OBS auto-switching on alerts** (`Services/ObsAutoSwitchService.cs`,
  `ObsWebSocketClient.GetCurrentProgramScene`, Alerts sub-tab UI,
  `Config.ObsAutoSwitch`): per event type (raid/sub/follow) switch OBS to a
  configured scene, hold N seconds (0 = stay), return to the scene that was
  on program before. Bursts refresh the hold; manual scene changes are
  respected and disarm the return. Test buttons ride the same pipeline.
- **Poll/prediction widget** (`Services/PollService.cs`,
  `Views/PollOverlayWindow.xaml(.cs)`, new Polls sub-tab,
  `Config.PollWidget`): click-through window showing the broadcaster's active
  Twitch poll or prediction as text bars; hides when nothing runs; 6s Helix
  polling; "Create a 30s test poll" button creates a REAL poll for end-to-end
  verification.
- Twitch login scopes grew `channel:read:polls channel:manage:polls
  channel:read:predictions` - **one re-login grants them** (follows/subs rule).
- README: new "Why C#/WPF?" section (records why the app is C#/WPF).

## Not yet verified (needs Kat)

- Re-login once on the Chat Overlays tab (grants the poll scopes), then:
  Polls sub-tab -> enable the widget -> "Create a 30s test poll" - the widget
  must APPEAR (it stays fully hidden while nothing runs - an empty box was
  BUG-46, fixed) with cats/dogs and live votes, then hide again after 30s.
- First verification round found and fixed: BUG-46 (empty widget box never
  hid - the initial "nothing running" publish was deduped away before the
  window subscribed; the window is now only ever shown WITH content) and
  BUG-47 (the test-poll 403 printed the moderation-specific "not a moderator"
  text; now says the login lacks poll permissions - re-login once).
- Second round: a fresh re-login STILL 403s the test poll -> most likely the
  channel is not a Twitch affiliate/partner (polls exist only there). BUG-48:
  the toolkit now checks `broadcaster_type` and states it plainly. NOTE: if
  that is the verdict, the Twitch poll widget is N/A for this channel - decide
  whether to keep it (for when the channel affiliates) or park it; Kick and
  YouTube have no comparable public poll APIs. The widget staying hidden while
  idle is BY DESIGN (BUG-46) - it only appears with live content.
- Start a prediction from the Twitch dashboard with no poll running - the
  widget shows users/points and "to lock" (or "locked").
- Alerts tab: set a raid scene + back-after (e.g. 30s), enable OBS control
  (Relay & Routing tab), "Send test raid" - OBS switches and returns. During
  the hold, switch scenes by hand - the toolkit must not override you.
- Ctrl+Alt+C locks/unlocks the widget with the overlays; drag positions it;
  with the widget visible, X on the main window minimizes (close guard).

## Known next steps (docs/ROADMAP.md)

- **docs/BUGS.md fourth-pass findings (2026-10-11): 27 [OPEN] items
  (BUG-49..75) recorded for a future fix session - two High (Output Studio UI
  freeze via the shared SSH Gate; author-color parse crash), then hidden-
  overlay restart, window-leak, two security items (SETUP.md key plaintext,
  ingest-host shell interpolation) and the rest Low.** Work through them
  top-down; severities and fix directions are in each entry.
- Title/category sync, key rotation reminders, per-platform status board.
- Kick/YouTube OAuth logins (in flight); Kick/YouTube alert/poll equivalents
  wait on those logins.
- Poll widget: N/A for now - the channel is not a Twitch affiliate (BUG-48
  verdict); polls/predictions only exist on affiliate/partner channels. Leave
  the widget dormant; it stays hidden and explains itself in the status line.

---

# Session progress - 2026-10-10 (handoff note)

## Where we are

Phase one + go-live are shipped and working for Kat (relay, deploy, watchdog,
start/end stream). Today was the **moderation + command runner foundation**
plus a long debugging session on chat delivery. **Moderation and commands are
fully verified by Kat, including timeout/ban/delete against a real second
account.** Everything below is committed and pushed on `main`.

## Shipped today (commit order)

| Commit | What |
|---|---|
| `3427ff4` | Third-pass bug sweep (BUG-33..39) |
| `0947208` | Moderation + command runner foundation: Twitch OAuth (PKCE, loopback :8770), authenticated chat with send, Helix timeout/ban/delete from overlay, `!commands` with replies + OBS scene switch, Commands tab |
| `1f49a13` | Key rotation: local "server runs an old config - redeploy" banner (last-deployed nginx hash in secrets.json); My Channels edits propagate to overlays that still carry the old value |
| `c3e843b` | "Sync overlays from My channels" + "Send test messages" buttons |
| `843fe00` / `940c0fc` | Overlay header counters (raw wire lines / parsed msgs / shown) |
| `0d9859c` | IChatStats declaration fix (counters actually render) |
| `9f26b5c` | Chat diagnostics: raw 15s IRC capture window (Chat Overlays tab) |
| `4bfcc56` | Single-instance mutex guard; deleted stale bin/Release |
| `f62e3ac` | Optional Client Secret (Confidential apps); "missing client secret" hint |
| `0c943ff` | Clients read auth per reconnect + auto-reconnect on login/refresh; Ctrl+Alt+X alias hotkey; click-through toggle button |
| `e5aff2a` | Broadcaster commands no longer swallowed by self-echo filter; per-command "On overlay" toggle; right-click menu explains hidden items |
| `880971f` | Moderation menu opened manually on right-click (ContextMenuService never fired on chromeless windows) |
| `da9d1df` | **BUG-40**: ChatEntry never invoked its subscriber lists - real chat NEVER reached overlays/runner in any build (test messages masked it). Fixed; recorded in BUGS.md |
| (this push) | **BUG-41**: right-click on chat TEXT hit-tests to a `Run` (FrameworkContentElement) - the `as FrameworkElement` cast returned null and the menu never opened; now walks content+visual trees, and right-clicking a real line always explains itself. **BUG-42/round 3**: Chat Overlays tab relaid out - big channels/account column (fills, min 420) + compact 500px "Settings for:" column on the right, both scroll. **BUG-43**: dark ContextMenu/MenuItem templates (the menu had default bright chrome + washed-out text) |
| (this push) | **Alerts (follows/subs/raids), text-only**: Twitch EventSub websocket (`Chat/TwitchEventSubClient.cs`) rides the existing OAuth; `Services/AlertService.cs` fans alerts to the overlays as PLAIN TEXT lines (no animations - deliberate) + optional rolling `alerts.txt` for OBS scroll banners (Text source -> Read from file -> Scroll filter). New Alerts sub-tab: master + per-type toggles, test follow/sub/raid buttons, recent-alerts list (delivery diagnostics), banner file config. **Chat Overlays tab split into sub-tabs** (Overlays / Channels & account / Alerts). Needs ONE re-login for the new scopes (`moderator:read:followers`, `channel:read:subscriptions`); raids need none. Likes have no free API - skipped. |
| (this push) | Verification round 1 (Kat): alerts pipeline **VERIFIED WORKING**. Fixes from that round: **BUG-44** - close behavior now deliberate per Kat: X **minimizes** while overlays are on screen or a stream is live (accidental click cannot kill overlays; minimizing hides the screen from a capture); closing again from the taskbar while minimized - or with nothing active - quits. **BUG-45** - overlay look is configurable: per-overlay Font (editable picker), Chat text color, Alert color (hex, invalid input falls back live). |
| (this push) | **Moderation leftovers, Twitch side**: (1) **Slow-mode toggle** - the overlay mod menu gained a channel section: "Slow mode: on (30s)" / "Slow mode: off", using the already-wired `ModerationService.SetSlowModeAsync` (the standard login already carries `moderator:manage:chat_settings`). The PATCH is idempotent; the result line reports what happened. (2) **Shared chat filter** - one word list for every overlay: `Services/ChatFilter.cs` + `Config.Moderation` (edited in the Channels & account tab: toggle + multiline word box, one entry per line, `#` lines ignored). Matching chat lines are dropped before they render - slurs never reach the stream capture. Case-insensitive whole-word match (lookarounds, so symbol-edged entries work); toolkit lines (alerts, !command replies, moderation results, test messages) bypass the filter by design. |

## Verification status (all done, Kat)

- `!test` reply in Twitch chat - **WORKING**.
- Right-click moderation menu on unlocked overlay - **WORKING**, including
  **timeout/ban/delete verified with a real second account's message**.
- `!commands` runner + per-command "On overlay" toggle - **WORKING**.
- Rearranged Chat Overlays tab (big left, compact settings right) - **APPROVED**.
- Separate bot account flow - delayed by Kat (no code needed anyway).

## Not yet verified (this push - needs Kat)

- Test follow/sub/raid buttons land in the overlay (text lines, `[test]`
  tag) and in the banner file when enabled - **alerts pipeline itself
  VERIFIED by Kat**; the review-fix build (dead-loop fix, 403 handling) is
  NOT yet re-verified against a real follow/sub/raid.
- One re-login (log out, log back in) to grant the alert scopes - if skipped,
  follows/subs are skipped with a status explanation; raids still work.
- Sub-tab layout of the Chat Overlays tab (restructures the round-3 layout
  that was approved: setup sections moved to their own sub-tab).
- BUG-44/45 fixes: with overlays on screen or a stream live, X must MINIMIZE
  (not quit); quitting works via a second close from the taskbar while
  minimized, or when nothing is active. Font + color settings render live on
  the overlay; invalid hex falls back to the default until the value is
  complete.
- Slow-mode ON/OFF in the overlay mod menu: flip one way, then the other -
  the overlay shows a "slow mode ON/OFF" toolkit line, and Twitch chat
  actually slows down / speeds up (needs the Twitch login; not a mod on the
  channel = the menu explains that in the error line).
- Shared chat filter: add a word in the Channels & account tab, send a chat
  line containing it - the line must not appear in ANY overlay, while a line
  without it still does. Whole-word check: "word" matches "that WORD there"
  but not "swordfish". Alerts and !command replies must still render with
  the filter on.

## Known next steps (docs/ROADMAP.md)

- Verify the alerts push and the moderation extras (above), then:
- **Kick/YouTube OAuth logins** - send/mod/alerts on those platforms (the
  capability interfaces are in place; only the per-platform logins and API
  calls are missing).
- Polls/predictions widget, OBS auto-switching on raids/follows (hook into the
  shipped AlertService feed).
- Test note: exclusive-fullscreen games swallow mouse input - borderless needed
  for overlay clicks.

## Environment gotchas

- **Close the running toolkit before `dotnet build`** - the exe lock caused
  three stale-binary incidents today (title bar now shows a build stamp).
- Single-instance guard is in (second launch shows a message).
- Twitch login needs: dev.twitch.tv console app, redirect `http://localhost:8770/`,
  Client ID (+ Client Secret if the app type is Confidential).
