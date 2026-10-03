# Roadmap

Ordered by value-per-effort. Everything stays free/self-hosted.

## Next

- **TikTok chat connector** - TikTok requires signed websockets for chat reads
  (Euler Stream or similar); video output to TikTok already works via the relay.
- **One-click deploy** - export + upload + restart over SSH from inside the app.
- **Live status indicators** - query each platform's API to show "LIVE / waiting"
  per destination card, plus relay health via nginx rtmp stats endpoint.

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
