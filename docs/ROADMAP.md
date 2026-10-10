# Roadmap

Ordered by value-per-effort. Everything stays free/self-hosted.
Idea write-ups and Kats ratings (10 = add to roadmap) live in
[IDEAS](IDEAS.md); this file is the actionable queue.

## Shipped

- **One-click deploy** - the Deploy tab uploads the bundle over SSH/SFTP and runs
  `docker compose up -d --build` on your VPS. Test connection + deploy + logs, no terminal.
- **Relay health status** - the Deploy tab polls the relay's stats endpoint through the
  server's loopback (over SSH, nothing exposed publicly) and shows whether your OBS
  stream is arriving: "receiving your stream" / "waiting for OBS", with optional 10s auto-refresh.
- **Output Studio** - visual editor for the server's outputs: destination pipeline cards
  you can drag to reorder, per-destination encode tuning (preset, fps override, extra
  ffmpeg args, delay), OBS-style layer compositing for portrait outputs (any number of
  input cuts moved/resized anywhere, transparent PNG overlays, blur areas, background
  reveals, image or animated-GIF backgrounds), a local mirror preview, and a true
  server-output preview (~2 fps snapshots the portrait encoders write and the toolkit
  reads over SSH), plus a live preview of the nginx.conf lines each destination generates.
- **Per-destination delay** - hold the first frame N seconds per portrait destination
  (clip safety); configured in the Output Studio, shipped inside the generated ffmpeg command.
- **Live editing preview** - the Output Studio's server preview now streams the destination's
  exact composed output continuously (~10 fps, 432x768 / 768x432 MJPEG, ~1-2 Mbit/s over the
  existing SSH channel) instead of polling 2 fps snapshots, so while you edit you see exactly
  what the platform receives, live. The snapshot diagnostics remain as the
  fallback until the first frame lands, and stale frames are labelled with their age.
- **Streaming deploy log** - the Deploy tab shows server output the moment it arrives
  (docker compose build steps, hardening script, errors) instead of freezing until
  each step finishes; the long build is no longer a silent wait.
- **Per-destination live checks** - every destination card now shows a second light
  for the platform side: "live on platform" / "not live yet", polled every 30s from
  Kick's public channel API, Twitch's anonymous GraphQL, YouTube live-video
  resolution and TikTok's live page. Handled per destination (new Handle column) with
  fallback to the "My channels" entries; failures degrade to "unknown", never red
  noise. The relay only sees its own pushes - this light is what catches "the server
  sends but the platform shows nothing".
- **Watchdog with alerting (toolkit-first)** - the 10s monitor tick became a full
  health pass: one SSH command reads the relay's /stat, the container's ffmpeg
  process list (each routed encoder is identified by its kat-preview-<id> snapshot
  path), every preview snapshot's file age, and the relay error-log tail. The result
  drives a red WATCHDOG banner with per-condition alarms, each stating the reason
  from the relay log (keys redacted before display): relay stopped responding
  (debounced two ticks so one SSH hiccup stays silent), OBS feed dropped mid-stream
  (transition-detected), a routed destination's encoder down (missing process) or
  frozen (process alive, snapshot stale > 60s), and repeated push failures per
  landscape destination (log lines naming the ingest host). Frozen encoders are
  auto-killed (default on, toggle in the banner) so nginx respawns a fresh one;
  per-alarm "restart encoder" buttons and a "Restart relay" button sit right in the
  banner, with an optional alarm sound. Per-destination push lights are now honest
  (routed   destinations report their own encoder state instead of copying the global
  relay state). The "post a notice into the affected platform's chat" half can
  now build on the Twitch chat-send from the moderation work (Kick/YouTube
  wait for their OAuth logins).
- **Go-live orchestration (one-button Start/End stream)** - a new obs-websocket 5.x
  client (`Services/ObsWebSocketClient.cs`: challenge/salt auth, request/response
  RPC, StreamStateChanged events, scene requests ready for later hooks) powers a
  Start stream button that verifies the relay, tells OBS to start (when OBS control
  is enabled), then polls nginx stats until the stream actually arrives - and an
  End stream button that stops OBS and confirms the publisher dropped. OBS settings
  live on the Relay tab (enabled toggle, ws url, password in secrets.json); the
  connection shows its own status light. Without OBS control the buttons still
  verify relay + arrival, so they work for everyone. Scene changes on chat events
  remain in the queue under "OBS auto-switching"; second-PC OBS stays post-release.
- **Moderation + command runner (Twitch first)** - the toolkit now has the
  OAuth + chat-send foundation everything in phase two leans on. A Twitch
  account login (authorization code + PKCE, loopback redirect on port 8770,
  tokens in secrets.json, automatic refresh) turns the Twitch chat connection
  authenticated: it gains the `IChatSender` capability and richer message tags
  (msg id, user id, badges). On top of that: right-click a chat line in an
  unlocked overlay for timeout (10m/1h), ban and delete-message (Helix API,
  slow-mode endpoint wired but awaiting a UI toggle), modded/broadcaster lines
  are protected, and a `!commands` runner (new Commands tab) replies in chat
  and/or flips an OBS scene, with per-command cooldown, watching every
  configured channel through the shared ChatHub connections. Kick and YouTube
  stay read-only until their own OAuth work (their connectors report "cannot
  send"); TikTok chat is on hold. One-time setup: create a Twitch application
  in the dev console (redirect `http://localhost:8770/`) and paste its Client ID.

## On hold - TikTok chat connector (DELAYED UNTIL FURTHER NOTICE)

The connector code is written and wired in (`Chat/TikTokChatClient.cs`: Euler Stream
room-info pre-check + signed websocket, API key in secrets.json, TikTok chat mode and
handle fields in the overlay UI) - but it has never run against real Euler responses,
and it is now **delayed until further notice**: quirks in TikTok's streaming setup mean
Kats cannot test it for now. TikTok *video* works today and needs none of this - only
the chat connector is paused. When picking it back up: if the overlay status sticks on
"reconnecting" or "waiting for the live", the exact response shapes (`roomInfo.status`,
the `websocketUrl` sign reply, the chat frames' `event`/`data`/`comment` fields) need
adjusting in `TikTokChatClient` - all parsing there is defensive and fails toward
clear status text.

## Next - phase two (rated 10/10 in IDEAS - current queue)

Standing decisions for everything in this phase (from Kats):

- **Platform-agnostic first** - Twitch, Kick, YouTube and TikTok are all
  primary, built behind one connector layer (per-platform adapters implementing
  a shared interface) so additional platforms slot in later without rework.
- **OAuth logins are accepted** - a browser popup granting the toolkit
  permissions; tokens live in secrets.json. Stream keys alone stay for the
  relay path.

### Chat & overlays

- **Moderation leftovers** - shared filter lists, a slow-mode UI toggle (the
  Helix endpoint is already wired in `ModerationService`), and Kick/YouTube
  OAuth logins so timeout/ban/delete and chat replies work on those platforms
  too (the capability interfaces are in place; only the per-platform logins
  and API calls are missing). The Twitch first half is shipped (see Shipped).
- **Alert overlays** - follows / subs / raids / likes rendered as animated overlay
  elements next to the chat lines (Twitch EventSub + Kick + YouTube equivalents; the
  overlay window already exists, and the Twitch OAuth from the moderation work
  carries the EventSub socket).
- **Polls / predictions widget** - poll state fetched from the platform APIs and
  rendered as an overlay element.

### Relay / output engine

- **Per-destination quality ladder** - more than one variant per platform (e.g. 1080p60
  main + 720p fallback), selected by name suffix.
- **Audio processing per destination** - loudness normalization (-16 LUFS for platforms),
  per-destination volume trim, and music ducking on the portrait renditions (the old
  "Later" item, folded in here).
- **SRT ingest option** - SRT listener beside RTMP 1935 (loss-resilient, better for
  flaky uplinks); OBS supports it natively.
- **HLS self-view endpoint** - the relay serves your own composed output as HLS on a
  loopback-only port; check it on your phone over the SSH tunnel without burning a
  test stream.
- **Relay metrics history** - poll nginx stats once a minute into a small on-server
  store; per-destination bitrate/viewer graphs for the session. The bandwidth watchdog
  (warn when configured outputs exceed the VPS traffic allowance) rides on this.

### Platform integrations

- **Title/category sync** - set stream title + game/category once in the toolkit,
  pushed to every platform that supports it (Twitch/Kick/YouTube APIs).
- **Key rotation reminders** - destinations record when their stream key was last
  rotated; the toolkit nags on a configurable interval (keys leak via screenshots).
- **OBS auto-switching via the shipped obs-websocket link** - the transport is live
  (see go-live orchestration in Shipped), and `!commands` can already flip scenes
  (Commands tab); remaining work: automatic scene changes on chat events (raids,
  follows) and automatic portrait-scene handling, hooking the alert overlays work.
- **Per-platform status board** - the phase-one live checks extended into a grid:
  relay-side health + platform-side "actually live" + viewer counts where an API
  provides them, per destination, in one board.

### Ops & security

- **Server doctor** - one panel that checks everything a fresh VPS needs: Docker, disk
  space, kernel MTU state, firewall/security-list reachability of 1935 (and any viewer
  ports), certificate/keys - and prints the fix command for each failure. Turns the
  current scatter of diagnostics into a checklist.
- **Firewall automation + provider detection** - users bring any VPS, so the toolkit
  first detects what the server runs on, then recommends the right path: a guided
  checklist (exact console steps, no cloud credentials stored) where rules can't be
  edited over SSH (Oracle security lists), or automatic setup (UFW rules over SSH,
  provider CLI integration where an API key is provided). Both paths end at "1935 (and
  any viewer ports) reachable".
- **Config backups** - scheduled encrypted backup of config + secrets to a folder of
  choice (or S3-compatible store) with one-click restore.
- **Server resource view** - CPU/RAM/network/disk read through the existing persistent
  SSH session, shown next to the relay status (already polled anyway - just visualize it).

## Later (rated 8-9/10 - after the 10/10 wave)

- **BTTV / 7TV / FFZ emotes** in overlays (currently emotes render as text).
- **Output Studio test buttons** - validate configs locally, dry-run each destination's
  ffmpeg command on the server against a test source, and an opt-in short test push.
- **Output Studio polish** - direct manipulation (drag/resize layers in the live preview
  instead of sliders), all-outputs grid, layer animations + scheduled layer changes,
  scene presets per destination.
- **OBS & local tooling** - OBS profile generator (importable server/key/output
  settings), layout profiles, profile import/export (encrypted zip), tray integration,
  auto-update channel, portable single-file build.
- **Server-side !command bot (kat-bot, Nightbot-style)** - a small optional
  service added to the server bundle: a second container on the VPS that holds
  the Twitch IRC connection (using a bot OAuth token handed over at deploy,
  refreshed server-side), reads a commands JSON the toolkit generates and
  deploys, and replies straight from the server - millisecond latency, no home
  round trip, and it keeps working when your PC or home connection drops. The
  toolkit-side runner stays as the offline/testing fallback; moderation and
  OBS scene actions still run locally (the VPS has no obs-websocket).
- **Chat extras** - TTS of chat messages (per-platform flags, rate limiter); overlay
  themes + keyword highlights (bubble style, per-platform filters).
- **External watchdog notifications** - beyond the shipped toolkit watchdog's banner
  and auto-restart: Discord webhook, Telegram bot, ntfy push, and posting a notice
  into the affected destination's platform chat (Twitch send is live; Kick/YouTube
  need their OAuth logins).
- **Local relay mode** - same nginx/ffmpeg stack via Docker Desktop or WSL2 for people
  with strong upload; deploy becomes "choose target: VPS or this PC".

## Post-release (explicitly deferred)

- **Fallback server / multi-server failover** - development targets a single VPS; a
  fallback-server option (one-click switch when the primary dies or runs out of
  bandwidth) is built after release.
- **Second-PC OBS support** - go-live orchestration and auto-switching ship same-PC
  (obs-websocket on localhost); reaching OBS on a network machine comes later.

## Parked (rated below the later wave)

- **Chat recording** (6/10 - worried about file sizes) - raw per-platform JSONL chat
  logs per session for analysis, highlight mining and overlay replay.
- **Instant replay / rewind buffer** (5/10) - server-side ring buffer of the last N
  minutes; the local OBS replay buffer already covers most of this.

## Not planned

- **Clip factory / server-side recording** - rated 1/10 and 0/10: the stream is already
  recorded locally, so server-side clips, recordings and auto-clipping add nothing.
- Multi-tenant / hosted service - this stays a self-hosted single-user tool.
- Viewer-facing web features (chat page, embedded player) - the relay is private
  infrastructure, not a viewing platform.
- Anything requiring paid third-party services as a hard dependency (Euler Stream is
  accepted for the TikTok chat connector only).
