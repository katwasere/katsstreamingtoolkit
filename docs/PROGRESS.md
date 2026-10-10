# Session progress - 2026-10-10 (handoff note)

## Where we are

Phase one + go-live are shipped and working for Kat (relay, deploy, watchdog,
start/end stream). Today was the **moderation + command runner foundation**
plus a long debugging session on chat delivery. Everything below is committed
on `main` (latest: `880971f`).

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

## Chat pipeline status (verified end-to-end)

Twitch IRC -> parse (`in`) -> ChatEntry subscribers -> overlay render (`shown`)
all confirmed working with live counters. Anonymous reads work; OAuth login
works (Kat's app is Confidential -> Client Secret field, or switch app to
Public). Replies send via the logged-in account; broadcaster-typed commands
trigger correctly.

## Awaiting Kat's verification (build 2026-10-10 19:43+)

1. `!test` reply actually appears in Twitch chat (was blocked by BUG-40 then
   the self-echo filter - both fixed).
2. Right-click moderation menu on an unlocked overlay (new manual open).
   Full timeout/ban menu needs a message from a second account - own lines
   show explainer + Delete only (broadcaster can't be banned).
3. "On overlay" checkbox per command (bot line in overlay when fired).
4. Optional: separate bot account flow (create + /mod + log in with it) -
   no code needed.

## Known next steps (docs/ROADMAP.md)

- Chat: alert overlays (next big item), moderation leftovers (filter lists,
  slow-mode UI toggle, Kick/YouTube OAuth), server-side kat-bot (Nightbot-style).
- Test note: exclusive-fullscreen games swallow mouse input - borderless needed
  for overlay clicks.

## Environment gotchas

- **Close the running toolkit before `dotnet build`** - the exe lock caused
  three stale-binary incidents today (title bar now shows a build stamp).
- Single-instance guard is in (second launch shows a message).
- Twitch login needs: dev.twitch.tv console app, redirect `http://localhost:8770/`,
  Client ID (+ Client Secret if the app type is Confidential).
