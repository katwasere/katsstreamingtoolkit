# KAT's Streaming Toolkit - Bug List

Findings from the full code scan (first pass), plus the three server-side bugs
reported against the live relay (server 140.238.99.143). Line numbers refer to
the state of the code when the bug was found and may have shifted slightly.

Legend: **[FIXED]** = fixed in this pass - **[OPEN]** = confirmed, not yet fixed.

---

## Server relay (the three reported bugs)

### SR-1. OBS connects to the relay, then drops after a few seconds - [FIXED]
- **Cause**: Oracle Cloud VNICs advertise MTU 9000 while the internet path is
  1500, and Oracle security lists typically drop ICMP "fragmentation needed".
  TCP then black-holes every large segment: the RTMP handshake (small packets)
  succeeds, video data stalls, OBS gives up seconds in. Also killed
  server-to-platform pushes (same direction problem).
- **Files**: new `server-hardening.sh` (added to the exported bundle by
  `ServerExporter.Export`), `DeployService.Deploy` (runs it on every deploy).
- **Fix**: clamp advertised TCP MSS (`iptables -t mangle OUTPUT/FORWARD
  TCPMSS --clamp-mss-to-pmtu`), enable `net.ipv4.tcp_mtu_probing=1` via
  `/etc/sysctl.d/`, plus a systemd unit (`kat-relay-mss.service`) so the clamp
  is re-applied on reboot.

### SR-2. Output Studio / status persistently says "nginx sees no incoming stream" - [FIXED]
- **Cause**: generated config used `worker_processes auto`. nginx-rtmp keeps
  each published stream inside a single worker process, so the `/stat`
  endpoint only reports the worker the publisher landed on. The toolkit polls
  a (random) other worker and almost never sees `<publishing>`.
- **Files**: `RelayConfigGenerator.GenerateNginxConf`, `server/nginx.conf.example`.
- **Fix**: `worker_processes 1;` (with a comment explaining why).

### SR-3. Relay never sends the stream out to the platforms - [FIXED]
Four stacked causes:
1. MTU black-hole for outgoing pushes (see SR-1).
2. `worker_processes auto` starved the `exec_push` ffmpeg encoders - their
   pull connection to `rtmp://127.0.0.1:1935/live/$name` can land on a worker
   that does not have the stream (see SR-2).
3. **CenterCrop crop rect was taller than the source** (BUG-1 below) - ffmpeg
   rejected the crop filter instantly, so those outputs never streamed.
4. **One dead ingest hostname killed the whole container**: nginx resolves
   every `push` host *while parsing the config* (`ngx_parse_url` in
   `ngx_rtmp_relay_push_pull`), so a single outdated URL (e.g. TikTok's dead
   `push.tiktokcdn.com` default) made nginx exit at startup and the container
   restart-loop.
- **Files**: `RelayConfigGenerator.ComputeCropRect`, `DeployService.ValidateIngestHosts`.
- **Fix**: deploy now validates every *enabled* destination's ingest host on
  the server and fails with a clear message naming the dead one, before
  anything is uploaded.

---

## High impact

### BUG-1. CenterCrop produces crops taller than the source (ffmpeg rejects them) - [FIXED]
- 1920x1080 input produced a 608x1082 crop, 1280x720 produced 406x722.
  `MakeEven` rounds **up**, so `cropH = MakeEven(cropW * 16/9)` could exceed
  `srcH` by 1-2px; ffmpeg then rejects the whole crop filter and the
  destination never streams. The most common portrait layout was broken on
  every standard resolution.
- **File**: `RelayConfigGenerator.ComputeCropRect` (also fixed the same
  round-up overflow in `OutputPx`, `SourcePx` and `BuildLegacyCustomGraph`).
- **Fix**: new `FloorEven` helper; crop/size values are rounded down to even
  and clamped to the frame they are cut from.

### BUG-2. "Move right" button does nothing - [OPEN]
- `MainViewModel.MoveDestinationTo` does `if (index > old) index--;`
  (drag-drop semantics), but the `Move later` button calls
  `MoveDestinationTo(dest, i + 1)`, which the decrement cancels into
  `Move(i, i)` - a silent no-op. Move left works; drag-drop works; the right
  arrow on destination cards does nothing.
- **File**: `MainViewModel.MoveDestinationTo` / `MoveDestination`.

---

## Crash bugs

### BUG-3. Chat client dispose race can crash the whole app - [OPEN]
- `Dispose()` does `_cts?.Cancel(); _cts?.Dispose();`. If the client's `Run`
  loop is mid-reconnect (inside `Task.Delay(backoff, ct)`), the token's CTS is
  already disposed and `Task.Delay` throws `ObjectDisposedException`. The
  catch clauses only handle `OperationCanceledException`, so the exception
  escapes the `async void Run` and takes down the process. Race triggers on
  overlay restart / channel change / app close while a client is reconnecting.
- **Files**: `TwitchChatClient.Dispose` + `Run`, `KickChatClient`,
  `YouTubeChatClient` (same pattern in all three).

### BUG-4. Failed `ChatHub.Acquire` leaves a permanently broken entry - [OPEN]
- `ChatHub.Acquire` inserts the new `ChatEntry` into the dictionary *before*
  calling `Activate()`. If `Client.Start()`/connect throws, the dead entry
  stays registered with `RefCount 1`; every future acquire returns the broken
  entry and that platform never connects again until app restart.
- **File**: `ChatHub.Acquire` / `ChatEntry.Activate`.

---

## Logic / state bugs

### BUG-5. Legacy key migration silently never works - [OPEN]
- `ConfigStore.TryReadLegacySecrets` builds `new SecretsData()`, but
  `SecretsData.DestinationKeys` has no initializer (stays `null`), so
  `data.DestinationKeys![id] = key!` throws NRE, which is swallowed by the
  catch and returns null. Users upgrading from the old config format lose all
  their destination stream keys without an error message.
- **Files**: `ConfigStore.TryReadLegacySecrets`, `SecretsStore.SecretsData`.

### BUG-6. Destinations get double event subscriptions - [OPEN]
- `MainViewModel` constructor calls `AttachDestination(d)` for each loaded
  destination, then adds the same objects to the `Destinations` collection -
  whose `CollectionChanged` handler attaches them **again**. Every property
  change is processed twice (double `Refresh` -> double `Save`), and replaced
  `Layers` collections stack handlers.
- **File**: `MainViewModel` constructor (lines ~194 and ~236 pre-fix).

### BUG-7. `AddOverlay` duplicates the config entry - [OPEN]
- `Overlays.Add(overlay)` already re-syncs `Config.Overlays` via
  `CollectionChanged`; the explicit `Config.Overlays.Add(overlay)` right after
  adds it a second time. The next `Save()` papers over it, but anything
  running in between (e.g. the `OverlayAdded` handler) sees a doubled list.
- **File**: `MainViewModel.AddOverlay`.

### BUG-8. Overlay "locked" hint is inverted - [OPEN]
- The footer text "locked - Ctrl+Alt+C to unlock overlays" is bound with
  `InvBoolToVis` on `Locked`, so it displays while the overlay is **unlocked**
  and disappears once it is locked - the exact opposite of useful.
- **File**: `OverlayWindow.xaml` (footer TextBlock).

### BUG-9. Twitch IRC tag unescaping is order-broken - [OPEN]
- `UnescapeTag` runs `.Replace("\\s", " ")` and `.Replace("\\:", ";")`
  *before* `.Replace("\\\\", "\\")`. Sequential replaces cannot unescape
  correctly: a literal `\s` or `\:` in a display-name/color garbles the text
  (e.g. `\\s` becomes `\ ` instead of `\s`).
- **File**: `TwitchChatClient.UnescapeTag`.

### BUG-10. YouTube chat never recovers after "chat ended" - [OPEN]
- When a poll returns no continuation, `Run` sets status "chat ended" and
  returns permanently. A new stream on the same channel never reconnects; the
  overlay is stuck until the overlay is removed/re-added.
- **File**: `YouTubeChatClient.Run`.

---

## Robustness / performance

### BUG-11. One missing image file aborts the entire deploy - [OPEN]
- `ServerExporter.ExportCustomBackgrounds` throws if any custom background or
  overlay image referenced by a destination is gone from disk, which kills
  both "Export server bundle" and the one-click deploy for *everything*, even
  if that destination would be skipped anyway.
- **File**: `ServerExporter.ExportCustomBackgrounds`.

### BUG-12. `sudo docker compose up` can hang the deploy - [OPEN]
- `DeployService.Deploy` runs plain `sudo` (no `-n`, no PTY). On servers where
  sudo wants a password the deploy blocks until the 600s command timeout with
  no useful message.
- **File**: `DeployService.Deploy`.

### BUG-13. SFTP upload creates only one level of remote subdirectory - [OPEN]
- Files in nested folders call `sftp.CreateDirectory($"{RemotePath}/{dir}")`
  once; a two-level relative path (`assets/img`) fails because `assets`
  doesn't exist yet. Harmless with the current flat bundle, but a trap for
  any future nested layout.
- **File**: `DeployService.Deploy`.

### BUG-14. Constant SSH polling opens fresh connections every 10s - [OPEN]
- The monitor timer and auto-status timer each open a brand-new SSH
  connection every 10 seconds, forever, even when idle; `FetchSnapshot`
  connects per call. Combined they can hold/hammer the server with handshakes
  and burn the 10s status cycle on latency.
- **Files**: `MainViewModel` (constructor timers), `DeployService.FetchSnapshot`.

### BUG-15. Config + secrets rewritten on every keystroke - [OPEN]
- `Refresh()` calls `Save()` and is wired to every `PropertyChanged` of every
  destination/layer/upstream property (sliders, text boxes), and the
  `SshPassword` setter saves per keystroke. config.json and secrets.json are
  rewritten dozens of times per second while editing.
- **Files**: `MainViewModel.Refresh` / `SshPassword`.

### BUG-16. Non-atomic config save can corrupt config.json - [OPEN]
- `File.WriteAllText` truncates-then-writes; a crash/power loss mid-write
  leaves a truncated config that `ConfigStore.Load` silently replaces with
  defaults. Should write temp + move (or keep a .bak).
- **File**: `ConfigStore.Save`.

### BUG-17. Event handler leaks around layers - [OPEN]
- `HookLayers` never detaches handlers from replaced/cleared layer
  collections; Output Studio's `_hookedLayers` set grows forever and layer
  handlers are never unhooked. Slow memory growth across long sessions.
- **Files**: `MainViewModel.HookLayers`, `OutputStudio.HookLayer`.

### BUG-18. Cosmetic issues - [OPEN]
- Twitch client sets "connected" on *any* `JOIN` line (other users joining),
  not just the client's own join (`TwitchChatClient.Run`).
- Bandwidth/TB-per-month estimate is missing the bits-to-bytes division
  (~2.2x too high): `MainViewModel.Refresh`, `ServerExporter.BuildSetupGuide`.
- `OverlayConfig.BackgroundOpacity` is applied to the whole overlay border, so
  chat *text* fades too, not just the background (`OverlayWindow.xaml`).

---

## Notes for fixing the remaining items

- BUG-2: buttons should call `Move(i, i + 1)` directly (or `MoveDestinationTo`
  needs a mode flag separate from the drag-drop path).
- BUG-3: don't dispose the CTS from `Dispose` - cancel only, and let `Run`
  dispose it; or make the reconnect delay catch `ObjectDisposedException`.
- BUG-6/7: attach only in `CollectionChanged` (or only in the ctor), and let
  the collection sync own `Config.Overlays`/`Config.Destinations`.
- BUG-14/15: debounce saves (the overlay window already debounces - reuse the
  pattern), and reuse one SSH session like `RelayPreviewService` does.

---

# Second-pass findings

## Functionality

### BUG-19. "Show test card on all outputs" only ever cards ONE output - [OPEN]
- `RelayPreviewService.StartTestEncoders` runs the `pkill -f kat-preview-test`
  **inside** the per-destination loop, so each iteration kills the test
  encoders it just started for the previous destinations. With several
  ffmpeg-routed outputs enabled, only the last one ever shows the test card;
  the user sees one card and assumes the rest are broken.
- Related: `started` is counted from the exit status of the *detached*
  `docker exec -d`, which returns 0 even when the ffmpeg command then fails
  instantly - so "started N encoders" is not evidence anything ran.
- **File**: `RelayPreviewService.StartTestEncoders`.
- **Fix sketch**: pkill once before the loop (not per destination); verify a
  test encoder actually started by polling for its snapshot file instead of
  trusting `docker exec -d`.

### BUG-20. Portrait/Custom destinations die when OBS sends no audio - [OPEN]
- `BuildFfmpegArgs` hard-codes `pushMap = "-map \"[vmain]\" -map 0:a"` plus
  `-c:a aac`. If the incoming stream has no audio track (muted in OBS, or a
  video-only capture), ffmpeg exits immediately with
  "Stream map '0:a' matches no streams" and that destination produces nothing.
  Landscape `push` destinations are unaffected (bitstream copy), which makes
  it look like "only the portrait ones are broken".
- **File**: `RelayConfigGenerator.BuildFfmpegArgs`.
- **Fix sketch**: `0:a?` optional mapping is not enough on its own (aac
  encoder still needs an input) - detect the audio track first or add a
  silent-source fallback (`anullsrc`).

### BUG-21. Animated GIF overlay layers freeze after one loop - [OPEN]
- `BuildLayeredGraph` adds image layers as `-loop 1 -i "{file}"` regardless of
  extension. `-loop` belongs to the image2 demuxer; the GIF demuxer ignores
  it, so an animated GIF overlay plays exactly once and sticks on its last
  frame. The custom-background path already special-cases GIFs with
  `-ignore_loop 0`; the layer path doesn't.
- **File**: `RelayConfigGenerator.BuildLayeredGraph` (LayerType.Image case).

## Security / hygiene

### BUG-22. Deploy leaves a full copy of all stream keys in %TEMP% - [OPEN]
- `DeployService.Deploy` exports the complete bundle - including `nginx.conf`
  with every real platform stream key - to `%TEMP%\kat-relay-deploy`, uploads
  it, and never cleans it up. The keys sit in the temp folder indefinitely
  (until Windows storage cleanup) after every deploy.
- **File**: `DeployService.Deploy`.
- **Fix sketch**: delete the temp folder in a `finally`, or write keys only
  into the uploaded copy, not the local staging one.

### BUG-26. SSH key auth fails on passphrase-protected keys - [OPEN]
- `new PrivateKeyFile(t.KeyPath)` cannot open encrypted keys - connect throws
  an opaque "private key is encrypted / multi factor" error with no hint that
  the passphrase is the problem. `DeployTarget.Password` is sitting right
  there and SSH.NET accepts it as the passphrase.
- **Files**: `DeployService.ConnectSsh`, `DeployService.ConnectSftp`.

## Theme / UI

### BUG-23. ComboBox dropdown ignores MinWidth (TemplateBinding inside Popup) - [OPEN, cosmetic]
- The dark ComboBox template sets `MinWidth="{TemplateBinding ActualWidth}"`
  on the Popup's border. TemplateBinding does not evaluate inside Popup
  content (disconnected visual tree), so dropdowns shrink-wrap to their
  content instead of matching the closed box - long platform names make the
  popup narrower than the control.
- **File**: `App.xaml` (ComboBox template).
- **Fix sketch**: `{Binding RelativeSource={RelativeSource TemplatedParent},
  Path=ActualWidth}` (Rel-ative bindings do work inside Popup).

### BUG-24. Tooltips ignore the dark theme - [OPEN, cosmetic]
- `App.xaml` sets `ToolTip` Background/Foreground but ships no template; the
  default WPF tooltip chrome ignores both and renders the stock light
  tooltip - jarring bright popups over an otherwise dark UI (and over the
  transparent chat overlay during setup).
- **File**: `App.xaml` (ToolTip style).

## Documentation / minor / edge

### BUG-25. SETUP guide's scp step uploads the wrong folder - [OPEN, doc]
- `BuildSetupGuide` step 3 says `scp -r "<Directory.GetCurrentDirectory()>"`
  - the app's *working directory*, not the folder the user just exported (it
  also hard-codes `root@` even when a different SSH user is configured).
- **File**: `ServerExporter.BuildSetupGuide`.

### BUG-27. Custom-layout crop can exceed the frame for portrait sources - [OPEN, edge]
- `NormalizedCustomLayout`: when the crop height overflows it sets
  `cropH = 1; cropW = 1 / aspectFactor` but never re-clamps `cropW`. For a
  portrait upstream (aspectFactor < 1) that yields `cropW > 1`, and the
  Output Studio crop overlay then draws wider than the source frame. The
  server-side px math clamps, so the stream is fine - the live preview lies.
- **File**: `RelayConfigGenerator.NormalizedCustomLayout`.

### BUG-28. Small ones - [OPEN]
- `MainWindow.RegisterHotKey` ignores the Win32 return value - if Ctrl+Alt+C
  is already registered by another app, the global lock hotkey silently does
  nothing. (`MainWindow.RegisterHotKey`)
- `YouTubeChatClient.ResolveVideoId` treats any 11-character input without
  `.` or `/` as a video ID - an 11-character @handle is misrouted and fails
  to resolve with a confusing "no live stream found" message.
  (`YouTubeChatClient.ResolveVideoId`)
- `SecretsStore.Capture` calls `ToDictionary(d => d.Id.ToString(), ...)` -
  a duplicated destination Id (possible via hand-edited config) throws
  ArgumentException inside `Save`, which is swallowed, so secrets silently
  stop persisting. (`SecretsStore.Capture`)
- `OverlayWindow._statuses` can be repopulated by status callbacks from the
  *previous* sources queued just before `RestartSources`, briefly showing
  stale status text. (`OverlayWindow.StartSources/UpdateStatus`)
- Twitch/Kick clients register `ct.Register(...)` callbacks that are never
  disposed - a small per-reconnect leak. (`TwitchChatClient.Run`,
  `KickChatClient.Run`)

## Second-pass summary

| # | Area | Severity | Status |
|---|------|----------|--------|
| BUG-19 | Test card only on last output | Medium | OPEN |
| BUG-20 | No-audio stream kills portrait outputs | High | OPEN |
| BUG-21 | GIF overlays freeze after one loop | Medium | OPEN |
| BUG-22 | Stream keys left in %TEMP% after deploy | Medium (security) | OPEN |
| BUG-23 | ComboBox dropdown width broken | Low (cosmetic) | OPEN |
| BUG-24 | Light tooltips on dark theme | Low (cosmetic) | OPEN |
| BUG-25 | SETUP guide scp wrong folder | Low (doc) | OPEN |
| BUG-26 | Passphrase-protected SSH keys unsupported | Medium | OPEN |
| BUG-27 | Custom-layout crop preview > 1 on portrait sources | Low (edge) | OPEN |
| BUG-28 | Misc small items | Low | OPEN |
