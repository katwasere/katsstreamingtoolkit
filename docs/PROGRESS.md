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
