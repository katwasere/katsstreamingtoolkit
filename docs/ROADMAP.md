# Roadmap

Ordered by value-per-effort. Everything stays free/self-hosted.

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

## Next

- **TikTok chat connector** - TikTok requires signed websockets for chat reads
  (Euler Stream or similar); video output to TikTok already works via the relay.
- **Full-motion server preview** - higher-frame-rate pull of each output from the
  relay (RTMP/HLS) if the ~2 fps snapshots are not enough.
- **Per-destination live checks** - query Kick/YouTube/etc. for "actually live on the
  platform" (the relay can't see what happens after each push; needs platform APIs/scrapes).
- **Streaming SSH output** - stream deploy logs live instead of per-step updates.

## Later

- **BTTV / 7TV / FFZ emotes** in overlays (currently emotes render as text).
- **Output Studio test buttons** - validate configs locally, dry-run each destination's
  ffmpeg command on the server against a test source, and an opt-in short test push.
- **Local relay mode** - same nginx/ffmpeg stack via Docker Desktop or WSL2 for
  people with strong upload; no VPS needed.
- **OBS auto-switching** - obs-websocket integration: scene changes on chat
  events (raids, follows), automatic portrait-scene handling for vertical renditions.
- **Audio ducking** on the portrait renditions (music-only segments for Shorts).
- **Overlay themes** - bubble style, per-platform filters, keyword highlights.
- **Bandwidth watchdog** - warn when the configured destinations exceed the VPS
  traffic allowance.
