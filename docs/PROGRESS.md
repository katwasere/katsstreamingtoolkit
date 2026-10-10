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

## Verification status (all done, Kat)

- `!test` reply in Twitch chat - **WORKING**.
- Right-click moderation menu on unlocked overlay - **WORKING**, including
  **timeout/ban/delete verified with a real second account's message**.
- `!commands` runner + per-command "On overlay" toggle - **WORKING**.
- Rearranged Chat Overlays tab (big left, compact settings right) - **APPROVED**.
- Separate bot account flow - delayed by Kat (no code needed anyway).

## Known next steps (docs/ROADMAP.md)

- **Alert overlays** - the agreed next big item: follows/subs/raids as animated
  overlay elements. Twitch EventSub rides the existing OAuth (socket needed);
  overlay windows + ChatHub already exist. Kick/YouTube equivalents later.
- Moderation leftovers: slow-mode UI toggle (Helix endpoint already wired in
  `ModerationService` - smallest quick win), shared filter lists, Kick/YouTube
  OAuth for send/mod on those platforms.
- Polls/predictions widget, OBS auto-switching on chat events (hook into the
  alert work).
- Test note: exclusive-fullscreen games swallow mouse input - borderless needed
  for overlay clicks.

## Environment gotchas

- **Close the running toolkit before `dotnet build`** - the exe lock caused
  three stale-binary incidents today (title bar now shows a build stamp).
- Single-instance guard is in (second launch shows a message).
- Twitch login needs: dev.twitch.tv console app, redirect `http://localhost:8770/`,
  Client ID (+ Client Secret if the app type is Confidential).
