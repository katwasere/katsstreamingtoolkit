# Ideas

The idea pool behind the [ROADMAP](ROADMAP.md). Kats rating system: 10 means
add to the roadmap, 1 means its a bad idea. Everything rated 10/10 has
graduated to the roadmap's "phase two"; the 8-9/10 wave is queued in the
roadmap's "Later". This file keeps the full write-ups for everything not yet
(or never) on the roadmap, so the roadmap can stay a short actionable queue.

The long-term goal this list builds toward: **one app that runs the whole
multi-platform stream** - ingest, restream, chat, alerts, clips, and publishing -
with the VPS relay as its engine and zero terminal knowledge required.

## Decisions (from Kats, after rating)

- **Platform-agnostic first** - Twitch, Kick, YouTube and TikTok are all primary;
  build one connector layer (shared interface, per-platform adapters) so more
  platforms can be added later without rework. Don't deep-bind to any one API.
- **OAuth logins are accepted** - browser popup per platform, tokens stored in
  secrets.json. This unlocks moderation, event alerts and title sync.
- **Watchdog notifies in the toolkit first**, plus optional notices posted into the
  affected platform's chat. Discord/Telegram/ntfy pushes wait for "Later".
- **OBS is assumed on the same PC** (obs-websocket over localhost); second-PC
  support comes after release.
- **Single VPS until release** - the fallback server / failover option is
  post-release work.
- **Users bring their own server** - firewall setup detects the provider and offers
  either a guided checklist or automatic security-rule setup (or both).

---

## Graduated to the roadmap (rated 10/10)

Full descriptions now live in the roadmap's "Next - phase two" section:

- Chat & overlays: moderation + command runner, alert overlays, polls/predictions widget
- Relay engine: watchdog with alerting (toolkit + platform-chat notices), per-destination
  quality ladder, per-destination audio processing (incl. music ducking), SRT ingest,
  HLS self-view, relay metrics history (+ bandwidth watchdog)
- Platform: title/category sync, key rotation reminders, go-live orchestration +
  OBS auto-switching (one obs-websocket integration), per-platform status board
- Ops & security: server doctor, firewall automation + provider detection, config
  backups, server resource view

Deferred to the roadmap's "Post-release" despite the 10/10 rating (Kats):
multi-server failover (single VPS until release) and second-PC OBS support.

## Queued after the 10/10 wave (rated 8-9/10)

Full write-ups for the roadmap's compact "Later" bullets:

- **OBS & local tooling (9/10 - once the first pass is done)**
  - **OBS profile generator** - writes the OBS profile (server, key, output settings
    tuned for the relay) as importable files, so a new PC setup is "export from
    toolkit, import into OBS" instead of typing an RTMP URL.
  - **Layout profiles** - named Output Studio layouts (per game/show), switchable with
    one click or an OBS scene change; assets bundled with the profile.
  - **Profile import/export** - zip config + secrets + overlay assets into one file for
    backup or moving between machines (secrets encrypted with a passphrase).
  - **Tray integration** - minimize-to-tray with relay status icon, global hotkeys for
    clip-marking and overlay lock (the lock hotkey already exists).
  - **Auto-update channel** - the toolkit checks a GitHub release tag and offers a
    self-update (matters once other people run it).
  - **Portable single-file build** - self-contained single exe for the "just give me
    the app" crowd.
- **Output Studio polish (9/10 - feature-complete once the roadmap is done)**
  - **Direct manipulation** - drag/resize layers in the live preview instead of
    sliders; snap guides; multi-select. The preview already renders the true composed
    output, so hit-testing against layer rects is honest.
  - **All-outputs grid** - every enabled destination's live preview in one grid (the
    plumbing per destination already exists).
  - **Layer animations** - fade/slide-in on layer enable, scheduled layer changes
    ("show webcam cut at :30").
  - **Scene presets per destination** - saved layer stacks per destination (game vs.
    just-chatting layouts).
- **Chat extras (8/10)**
  - **TTS** - optional text-to-speech of chat messages through the overlay, with
    per-platform enable flags and a rate limiter.
  - **Keyword highlights** - chat lines matching keywords get a highlight treatment in
    the overlay (part of the "overlay themes" work: bubble style, per-platform filters).
- **Local relay mode (8/10 - once the roadmap is done)** - the same Docker bundle runs
  via Docker Desktop/WSL2 on the streamer PC; the toolkit detects a local engine and
  points OBS at 127.0.0.1. Deploy becomes "choose target: VPS or this PC". Unlocks the
  toolkit for people with strong upload and no VPS budget.

## Parked (rated below the later wave)

- **Chat recording** (6/10 - worried about file sizes) - raw per-platform chat logs
  saved per session (JSONL) for later analysis, highlight mining and replay in the
  overlay. Revisit if highlight mining becomes a real need; JSONL compresses well.
- **Instant replay / rewind buffer** (5/10) - ring buffer of the last N minutes on the
  server; "save buffer as clip". The local OBS replay buffer already covers most of it.

## Rejected

- **Clip factory** (rated 1/10) - one-click clips, auto-clipping triggers, clip review
  queue, Shorts template renderer, post-stream publishing helper. Reason: we are
  already recording the stream, so none of this earns its complexity.
- **Server-side recording** (rated 0/10) - same reason as the clip factory; the
  recording exists locally already.

---

## Not planned (explicitly)

- Multi-tenant / hosted service - this stays a self-hosted single-user tool.
- Viewer-facing web features (chat page, embedded player) - the relay is
  private infrastructure, not a viewing platform.
- Anything requiring paid third-party services as a hard dependency
  (Euler Stream for TikTok chat is accepted for that one connector only).
