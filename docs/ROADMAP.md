# Roadmap

Ordered by value-per-effort. Everything stays free/self-hosted.

## Shipped

- **One-click deploy** - the Deploy tab uploads the bundle over SSH/SFTP and runs
  `docker compose up -d --build` on your VPS. Test connection + deploy + logs, no terminal.
- **Relay health status** - the Deploy tab polls the relay's stats endpoint through the
  server's loopback (over SSH, nothing exposed publicly) and shows whether your OBS
  stream is arriving: "receiving your stream" / "waiting for OBS", with optional 10s auto-refresh.

## Next

- **TikTok chat connector** - TikTok requires signed websockets for chat reads
  (Euler Stream or similar); video output to TikTok already works via the relay.
- **Per-destination live checks** - query Kick/YouTube/etc. for "actually live on the
  platform" (the relay can't see what happens after each push; needs platform APIs/scrapes).
- **Streaming SSH output** - stream deploy logs live instead of per-step updates.

## Later

- **BTTV / 7TV / FFZ emotes** in overlays (currently emotes render as text).
- **Per-destination delay** - some platforms deserve a delay for clip safety.
- **Local relay mode** - same nginx/ffmpeg stack via Docker Desktop or WSL2 for
  people with strong upload; no VPS needed.
- **OBS auto-switching** - obs-websocket integration: scene changes on chat
  events (raids, follows), automatic portrait-scene handling for vertical renditions.
- **Audio ducking** on the portrait renditions (music-only segments for Shorts).
- **Overlay themes** - bubble style, per-platform filters, keyword highlights.
- **Bandwidth watchdog** - warn when the configured destinations exceed the VPS
  traffic allowance.
