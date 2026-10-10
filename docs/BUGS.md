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

### BUG-2. "Move right" button does nothing - [FIXED]
- `MainViewModel.MoveDestinationTo` does `if (index > old) index--;`
  (drag-drop semantics), but the `Move later` button calls
  `MoveDestinationTo(dest, i + 1)`, which the decrement cancels into
  `Move(i, i)` - a silent no-op. Move left works; drag-drop works; the right
  arrow on destination cards does nothing.
- **File**: `MainViewModel.MoveDestinationTo` / `MoveDestination`.
- **Fix**: `MoveDestination` now performs the one-slot move directly
  (`Destinations.Move(i, i + delta)`); drag-drop keeps its own
  `MoveDestinationTo` semantics.

---

## Crash bugs

### BUG-3. Chat client dispose race can crash the whole app - [FIXED]
- `Dispose()` does `_cts?.Cancel(); _cts?.Dispose();`. If the client's `Run`
  loop is mid-reconnect (inside `Task.Delay(backoff, ct)`), the token's CTS is
  already disposed and `Task.Delay` throws `ObjectDisposedException`. The
  catch clauses only handle `OperationCanceledException`, so the exception
  escapes the `async void Run` and takes down the process. Race triggers on
  overlay restart / channel change / app close while a client is reconnecting.
- **Files**: `TwitchChatClient.Dispose` + `Run`, `KickChatClient`,
  `YouTubeChatClient` (same pattern in all three).
- **Fix**: `Dispose` now cancels only; the CTS is disposed by the `Run` loop
  itself in a `finally`, on its own thread - a mid-reconnect `Task.Delay`
  can never observe a disposed token.

### BUG-4. Failed `ChatHub.Acquire` leaves a permanently broken entry - [FIXED]
- `ChatHub.Acquire` inserts the new `ChatEntry` into the dictionary *before*
  calling `Activate()`. If `Client.Start()`/connect throws, the dead entry
  stays registered with `RefCount 1`; every future acquire returns the broken
  entry and that platform never connects again until app restart.
- **File**: `ChatHub.Acquire` / `ChatEntry.Activate`.
- **Fix**: `Activate()` now runs before the dictionary insert; on failure the
  entry (and client) is disposed and the exception rethrown.

---

## Logic / state bugs

### BUG-5. Legacy key migration silently never works - [FIXED]
- `ConfigStore.TryReadLegacySecrets` builds `new SecretsData()`, but
  `SecretsData.DestinationKeys` has no initializer (stays `null`), so
  `data.DestinationKeys![id] = key!` throws NRE, which is swallowed by the
  catch and returns null. Users upgrading from the old config format lose all
  their destination stream keys without an error message.
- **Files**: `ConfigStore.TryReadLegacySecrets`, `SecretsStore.SecretsData`.
- **Fix**: `TryReadLegacySecrets` initializes `DestinationKeys`.

### BUG-6. Destinations get double event subscriptions - [FIXED]
- `MainViewModel` constructor calls `AttachDestination(d)` for each loaded
  destination, then adds the same objects to the `Destinations` collection -
  whose `CollectionChanged` handler attaches them **again**. Every property
  change is processed twice (double `Refresh` -> double `Save`), and replaced
  `Layers` collections stack handlers.
- **File**: `MainViewModel` constructor (lines ~194 and ~236 pre-fix).
- **Fix**: the ctor pre-attach loop is gone; `CollectionChanged` owns the
  lifetime (attach on Add, detach on Remove).

### BUG-7. `AddOverlay` duplicates the config entry - [FIXED]
- `Overlays.Add(overlay)` already re-syncs `Config.Overlays` via
  `CollectionChanged`; the explicit `Config.Overlays.Add(overlay)` right after
  adds it a second time. The next `Save()` papers over it, but anything
  running in between (e.g. the `OverlayAdded` handler) sees a doubled list.
- **File**: `MainViewModel.AddOverlay`.
- **Fix**: the explicit `Config.Overlays.Add` is removed; the collection sync
  is the single source of truth.

### BUG-8. Overlay "locked" hint is inverted - [FIXED]
- The footer text "locked - Ctrl+Alt+C to unlock overlays" is bound with
  `InvBoolToVis` on `Locked`, so it displays while the overlay is **unlocked**
  and disappears once it is locked - the exact opposite of useful.
- **File**: `OverlayWindow.xaml` (footer TextBlock).
- **Fix**: footer now uses `BoolToVis` (visible while locked).

### BUG-9. Twitch IRC tag unescaping is order-broken - [FIXED]
- `UnescapeTag` runs `.Replace("\\s", " ")` and `.Replace("\\:", ";")`
  *before* `.Replace("\\\\", "\\")`. Sequential replaces cannot unescape
  correctly: a literal `\s` or `\:` in a display-name/color garbles the text
  (e.g. `\\s` becomes `\ ` instead of `\s`).
- **File**: `TwitchChatClient.UnescapeTag`.
- **Fix**: single-pass character scanner over the escape sequences.

### BUG-10. YouTube chat never recovers after "chat ended" - [FIXED]
- When a poll returns no continuation, `Run` sets status "chat ended" and
  returns permanently. A new stream on the same channel never reconnects; the
  overlay is stuck until the overlay is removed/re-added.
- **File**: `YouTubeChatClient.Run`.
- **Fix**: "chat ended" now waits ~15s, then the loop re-resolves the live
  video and reattaches to the next stream's chat automatically.

---

## Robustness / performance

### BUG-11. One missing image file aborts the entire deploy - [FIXED]
- `ServerExporter.ExportCustomBackgrounds` throws if any custom background or
  overlay image referenced by a destination is gone from disk, which kills
  both "Export server bundle" and the one-click deploy for *everything*, even
  if that destination would be skipped anyway.
- **File**: `ServerExporter.ExportCustomBackgrounds`.
- **Fix**: missing images are skipped with a warning in the deploy log; the
  rest of the bundle still ships (`Export` takes an optional log callback).

### BUG-12. `sudo docker compose up` can hang the deploy - [FIXED]
- `DeployService.Deploy` runs plain `sudo` (no `-n`, no PTY). On servers where
  sudo wants a password the deploy blocks until the 600s command timeout with
  no useful message.
- **File**: `DeployService.Deploy`.
- **Fix**: deploy probes `sudo -n true` once and uses `sudo -n` everywhere
  (hardening, compose, remote mkdir). Without passwordless sudo it falls back
  to direct docker access and logs what to do instead of hanging.

### BUG-13. SFTP upload creates only one level of remote subdirectory - [FIXED]
- Files in nested folders call `sftp.CreateDirectory($"{RemotePath}/{dir}")`
  once; a two-level relative path (`assets/img`) fails because `assets`
  doesn't exist yet. Harmless with the current flat bundle, but a trap for
  any future nested layout.
- **File**: `DeployService.Deploy`.
- **Fix**: `EnsureRemoteDirPath` creates every path level in order.

### BUG-14. Constant SSH polling opens fresh connections every 10s - [FIXED]
- The monitor timer and auto-status timer each open a brand-new SSH
  connection every 10 seconds, forever, even when idle; `FetchSnapshot`
  connects per call. Combined they can hold/hammer the server with handshakes
  and burn the 10s status cycle on latency.
- **Files**: `MainViewModel` (constructor timers), `DeployService.FetchSnapshot`.
- **Fix**: status polling now runs through `RelayPreviewService.RunOnServer`,
  the same persistent, auto-reconnecting SSH session the preview service
  keeps alive.

### BUG-15. Config + secrets rewritten on every keystroke - [FIXED]
- `Refresh()` calls `Save()` and is wired to every `PropertyChanged` of every
  destination/layer/upstream property (sliders, text boxes), and the
  `SshPassword` setter saves per keystroke. config.json and secrets.json are
  rewritten dozens of times per second while editing.
- **Files**: `MainViewModel.Refresh` / `SshPassword`.
- **Fix**: property changes now go through a 1s debounced `ScheduleSave()` -
  same pattern the overlay window already used; the 15s auto-save remains the
  safety net.

### BUG-16. Non-atomic config save can corrupt config.json - [FIXED]
- `File.WriteAllText` truncates-then-writes; a crash/power loss mid-write
  leaves a truncated config that `ConfigStore.Load` silently replaces with
  defaults. Should write temp + move (or keep a .bak).
- **File**: `ConfigStore.Save`.
- **Fix**: new `ConfigStore.AtomicWrite` (temp + `File.Replace`, keeping a
  .bak); used by both config.json and secrets.json saves.

### BUG-17. Event handler leaks around layers - [FIXED]
- `HookLayers` never detaches handlers from replaced/cleared layer
  collections; Output Studio's `_hookedLayers` set grows forever and layer
  handlers are never unhooked. Slow memory growth across long sessions.
- **Files**: `MainViewModel.HookLayers`, `OutputStudio.HookLayer`.
- **Fix**: MainViewModel tracks per-destination subscriptions (handler +
  collection) and unhooks on replace/remove; Output Studio unhooks removed
  layers and the previous destination's layers on selection change.

### BUG-18. Cosmetic issues - [FIXED / one item verified not a bug]
- Twitch client sets "connected" on *any* `JOIN` line (other users joining),
  not just the client's own join (`TwitchChatClient.Run`).
  **Fixed**: "connected" now fires on the 001 welcome or our own nick's JOIN.
- Bandwidth/TB-per-month estimate is missing the bits-to-bytes division
  (~2.2x too high): `MainViewModel.Refresh`, `ServerExporter.BuildSetupGuide`.
  **Verified not a bug**: the current `* 0.45 * 720 / 1000` already includes
  the conversion (0.45 GB/h per Mbps = 0.125 bytes-per-bit x 3600s / 1000);
  40 Mbps = 12.96 TB/month, which is correct.
- `OverlayConfig.BackgroundOpacity` is applied to the whole overlay border, so
  chat *text* fades too, not just the background (`OverlayWindow.xaml`).
  **Fixed**: opacity now applies to a backdrop-only border; content sits above
  it at full strength.

---

## Notes (fix approaches used in the second pass)

- BUG-2: `MoveDestination` performs `Destinations.Move(i, i + delta)` itself;
  `MoveDestinationTo` keeps drag-drop semantics for the pipeline cards.
- BUG-3: `Dispose` cancels only - the `Run` loop disposes its own CTS in a
  `finally`, on its own thread.
- BUG-6/7: attach/detach happens only in `CollectionChanged`, and the
  collection sync owns `Config.Overlays`/`Config.Destinations`.
- BUG-14/15: property saves are debounced 1s (`ScheduleSave`); all periodic
  server reads share `RelayPreviewService`'s persistent SSH session.

---

# Second-pass findings

## Functionality

### BUG-19. "Show test card on all outputs" only ever cards ONE output - [FIXED]
- `RelayPreviewService.StartTestEncoders` runs the `pkill -f kat-preview-test`
  **inside** the per-destination loop, so each iteration kills the test
  encoders it just started for the previous destinations. With several
  ffmpeg-routed outputs enabled, only the last one ever shows the test card;
  the user sees one card and assumes the rest are broken.
- Related: `started` is counted from the exit status of the *detached*
  `docker exec -d`, which returns 0 even when the ffmpeg command then fails
  instantly - so "started N encoders" is not evidence anything ran.
- **File**: `RelayPreviewService.StartTestEncoders`.
- **Fix**: pkill runs once before the loop; after launching all encoders each
  test snapshot file is polled (`[ -s file ]`) and only verified encoders are
  counted.

### BUG-20. Portrait/Custom destinations die when OBS sends no audio - [FIXED]
- `BuildFfmpegArgs` hard-codes `pushMap = "-map \"[vmain]\" -map 0:a"` plus
  `-c:a aac`. If the incoming stream has no audio track (muted in OBS, or a
  video-only capture), ffmpeg exits immediately with
  "Stream map '0:a' matches no streams" and that destination produces nothing.
  Landscape `push` destinations are unaffected (bitstream copy), which makes
  it look like "only the portrait ones are broken".
- **File**: `RelayConfigGenerator.BuildFfmpegArgs`.
- **Fix**: `-map 0:a?` (optional mapping) - with no audio input stream the
  output simply has no audio track (valid FLV) and the destination keeps
  streaming; when audio exists it is encoded exactly as before.

### BUG-21. Animated GIF overlay layers freeze after one loop - [FIXED]
- `BuildLayeredGraph` adds image layers as `-loop 1 -i "{file}"` regardless of
  extension. `-loop` belongs to the image2 demuxer; the GIF demuxer ignores
  it, so an animated GIF overlay plays exactly once and sticks on its last
  frame. The custom-background path already special-cases GIFs with
  `-ignore_loop 0`; the layer path doesn't.
- **File**: `RelayConfigGenerator.BuildLayeredGraph` (LayerType.Image case).
- **Fix**: new `ImageInputArg` helper picks `-ignore_loop 0` for `.gif` and
  `-loop 1` otherwise; used by image layers, the layered background and the
  legacy custom background alike.

## Security / hygiene

### BUG-22. Deploy leaves a full copy of all stream keys in %TEMP% - [FIXED]
- `DeployService.Deploy` exports the complete bundle - including `nginx.conf`
  with every real platform stream key - to `%TEMP%\kat-relay-deploy`, uploads
  it, and never cleans it up. The keys sit in the temp folder indefinitely
  (until Windows storage cleanup) after every deploy.
- **File**: `DeployService.Deploy`.
- **Fix**: the staging folder is deleted in a `finally`, on every path
  (success, failure, exception).

### BUG-26. SSH key auth fails on passphrase-protected keys - [FIXED]
- `new PrivateKeyFile(t.KeyPath)` cannot open encrypted keys - connect throws
  an opaque "private key is encrypted / multi factor" error with no hint that
  the passphrase is the problem. `DeployTarget.Password` is sitting right
  there and SSH.NET accepts it as the passphrase.
- **Files**: `DeployService.ConnectSsh`, `DeployService.ConnectSftp`.
- **Fix**: `LoadPrivateKey` tries the key with the deploy password as
  passphrase, falls back to unencrypted, and reports an actionable error if
  neither works.

## Theme / UI

### BUG-23. ComboBox dropdown ignores MinWidth (TemplateBinding inside Popup) - [FIXED, cosmetic]
- The dark ComboBox template sets `MinWidth="{TemplateBinding ActualWidth}"`
  on the Popup's border. TemplateBinding does not evaluate inside Popup
  content (disconnected visual tree), so dropdowns shrink-wrap to their
  content instead of matching the closed box - long platform names make the
  popup narrower than the control.
- **File**: `App.xaml` (ComboBox template).
- **Fix**: `MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource
  TemplatedParent}}"` (RelativeSource bindings do work inside Popup).

### BUG-24. Tooltips ignore the dark theme - [FIXED, cosmetic]
- `App.xaml` sets `ToolTip` Background/Foreground but ships no template; the
  default WPF tooltip chrome ignores both and renders the stock light
  tooltip - jarring bright popups over an otherwise dark UI (and over the
  transparent chat overlay during setup).
- **File**: `App.xaml` (ToolTip style).
- **Fix**: the ToolTip style now ships a dark ControlTemplate (border +
  content) so the setters actually take effect.

## Documentation / minor / edge

### BUG-25. SETUP guide's scp step uploads the wrong folder - [FIXED, doc]
- `BuildSetupGuide` step 3 says `scp -r "<Directory.GetCurrentDirectory()>"`
  - the app's *working directory*, not the folder the user just exported (it
  also hard-codes `root@` even when a different SSH user is configured).
- **File**: `ServerExporter.BuildSetupGuide`.
- **Fix**: the guide now names the actual exported folder and uses the
  configured SSH user and remote path.

### BUG-27. Custom-layout crop can exceed the frame for portrait sources - [FIXED, edge]
- `NormalizedCustomLayout`: when the crop height overflows it sets
  `cropH = 1; cropW = 1 / aspectFactor` but never re-clamps `cropW`. For a
  portrait upstream (aspectFactor < 1) that yields `cropW > 1`, and the
  Output Studio crop overlay then draws wider than the source frame. The
  server-side px math clamps, so the stream is fine - the live preview lies.
- **File**: `RelayConfigGenerator.NormalizedCustomLayout`.
- **Fix**: `cropW = Math.Min(1 / aspectFactor, 1)`.

### BUG-28. Small ones - [FIXED]
- `MainWindow.RegisterHotKey` ignores the Win32 return value - if Ctrl+Alt+C
  is already registered by another app, the global lock hotkey silently does
  nothing. (`MainWindow.RegisterHotKey`)
  **Fixed**: registration failure is announced in the window title.
- `YouTubeChatClient.ResolveVideoId` treats any 11-character input without
  `.` or `/` as a video ID - an 11-character @handle is misrouted and fails
  to resolve with a confusing "no live stream found" message.
  (`YouTubeChatClient.ResolveVideoId`)
  **Fixed**: inputs starting with `@` never take the video-ID shortcut.
- `SecretsStore.Capture` calls `ToDictionary(d => d.Id.ToString(), ...)` -
  a duplicated destination Id (possible via hand-edited config) throws
  ArgumentException inside `Save`, which is swallowed, so secrets silently
  stop persisting. (`SecretsStore.Capture`)
  **Fixed**: keys are collected with an indexer (last wins), no throw.
- `OverlayWindow._statuses` can be repopulated by status callbacks from the
  *previous* sources queued just before `RestartSources`, briefly showing
  stale status text. (`OverlayWindow.StartSources/UpdateStatus`)
  **Fixed**: callbacks carry a generation counter; stale ones are dropped.
- Twitch/Kick clients register `ct.Register(...)` callbacks that are never
  disposed - a small per-reconnect leak. (`TwitchChatClient.Run`,
  `KickChatClient.Run`)
  **Fixed**: registrations are disposed via `using`.

### BUG-29. No way to verify what is actually deployed - [FIXED]
- Reported while debugging "the live preview still says no incoming stream":
  the toolkit could only GUESS whether the server ran the same bundle the app
  would ship - an old deployment silently kept the preview broken and the
  messages blamed the wrong thing.
- **Fix**:
  - `ServerExporter.Export` stamps a `BUNDLE-VERSION` file (content hash of
    Dockerfile + compose + nginx.conf + hardening + resolv.conf, plus a UTC
    timestamp); `ComputeNginxConfigHash` hashes the generated relay config.
  - `DeployService.GetServerBundleInfo` reads the server side back: the
    stamped version and the sha256 of the RUNNING container's
    `/etc/nginx/nginx.conf`, compared against what this app generates now.
  - Shown everywhere it matters: "Test connection" prints bundle version and
    match/mismatch; the Deploy tab status line appends
    `server bundle up to date (hash)` / `OUTDATED (server ... vs app ...) - redeploy`;
    the Output Studio live view switches from "nginx sees no incoming stream"
    to the real cause (relay down / nothing publishing / destination disabled /
    config older than this app / dead ingest host / the actual relay-log error
    line).
- **Note**: app builds older than this change can't show the new diagnostics -
  rebuild and run the app from source, then redeploy once to stamp the server.

### BUG-30. Stale shipped TikTok default blocks every deploy - [FIXED]
- The default TikTok destination ships `rtmp://push.tiktokcdn.com/live`, which
  no longer resolves in DNS (TikTok rotates ingest endpoints; the real URL has
  to come from the creator's live-key page). With that destination enabled,
  `ValidateIngestHosts` correctly refused to deploy - but it blocked ALL
  destinations, and the user had no way to know the URL was the toolkit's own
  stale default. Found live during a deploy on 140.238.99.143.
- **Fix**:
  - `ConfigStore.Load` clears the known-dead default host on load
    (`ClearKnownDeadDefaultIngest`); `CreateDefaults` no longer ships it.
  - `RelayConfigGenerator` renders destinations without an ingest URL as a
    comment in nginx.conf (`# (skipped: no ingest URL set ...)`) instead of a
    broken `push`/`exec_push` line - a missing URL can no longer produce a
    config that kills the relay. `DescribeDestination` mirrors it.
  - The live preview names the situation: "has no ingest URL - paste the RTMP
    URL from the platform's live-key page, then redeploy".
  - Other dead user-entered hosts still fail the deploy with the explicit
    SR-3 message (that behavior is correct - nginx would restart-loop).

### BUG-31. Moving the keys file wipes the SSH password out of it - [FIXED]
- `MainViewModel.MoveKeysFile` did `SaveFromConfig(newPath, cfg, _secrets)` and then
  `_secrets = SecretsStore.Capture(cfg)`. `Capture` builds from config alone and has
  no SshPassword, so `_secrets.SshPassword` became empty - the UI field cleared and
  the next `Save()` (debounced, seconds later) rewrote the new keys file with an
  empty SSH password. Found while adding the Euler Stream API key (EulerApiKey),
  which follows the same preserve-via-`previous` pattern as SshPassword.
- **File**: `MainViewModel.MoveKeysFile`.
- **Fix**: reload the file just written (`SecretsStore.Load(dialog.FileName)`)
  instead of `Capture`, and re-raise SshPassword/EulerApiKey so the UI follows.

### BUG-32. Watchdog flagged every routed encoder as "down" (shell quoting) - [FIXED]
- Found live during the first watchdog test: a Twitch destination streaming
  correctly showed "unreachable" with an "ENCODER IS DOWN" alarm. The health
  command nested the container scripts inside a host `sh -c "..."` double-quoted
  string, and the host shell expanded every `$(...)` and `$var` meant for the
  CONTAINER before docker exec ever ran (`$p` unset, tr read empty stdin) - the
  encoder process list and snapshot ages always came back empty, so all routed
  destinations (landscape-Custom counts as routed) looked dead. The relay-log
  section had no `$` in it, which made the quoted "reasons" look plausible.
- **File**: `DeployService.BuildHealthCommand`.
- **Fix**: the whole pass is one base64 script piped into `sh`, and each
  container script is base64 piped into `docker exec -i kat-relay sh` - the same
  zero-quoting pattern the preview/test-encoder code already used.

## Second-pass summary

| # | Area | Severity | Status |
|---|------|----------|--------|
| BUG-19 | Test card only on last output | Medium | FIXED |
| BUG-20 | No-audio stream kills portrait outputs | High | FIXED |
| BUG-21 | GIF overlays freeze after one loop | Medium | FIXED |
| BUG-22 | Stream keys left in %TEMP% after deploy | Medium (security) | FIXED |
| BUG-23 | ComboBox dropdown width broken | Low (cosmetic) | FIXED |
| BUG-24 | Light tooltips on dark theme | Low (cosmetic) | FIXED |
| BUG-25 | SETUP guide scp wrong folder | Low (doc) | FIXED |
| BUG-26 | Passphrase-protected SSH keys unsupported | Medium | FIXED |
| BUG-27 | Custom-layout crop preview > 1 on portrait sources | Low (edge) | FIXED |
| BUG-28 | Misc small items | Low | FIXED |
| BUG-29 | Deployed relay version unverifiable | Medium | FIXED |
| BUG-30 | Stale shipped TikTok default blocks deploys | High | FIXED |
| BUG-31 | Keys-file move wiped the SSH password | Medium | FIXED |
| BUG-32 | Watchdog false "encoder down" via shell quoting | High | FIXED |

All findings from both passes are now fixed; the only [OPEN]-worthy leftover
is the BUG-18 bandwidth-math claim, which turned out to be correct as written
(verified: 0.45 GB/h per Mbps already contains the bits-to-bytes division).

---

# Third-pass findings

Full rescan of every source file (all services, chat clients, views, XAML and
server docs) after the second-pass fixes landed. All items were found and
**[FIXED]** in this pass. Line numbers refer to the tree at the time of the fix.

### BUG-33. Custom-layout crop preview always draws the crop centered - [FIXED]
- `CropOverlay` renders the kept region at `(SourceW - CropW) / 2` and takes no
  X/Y at all; `UpdatePreview` only sets `CropW`/`CropH`. For CenterCrop that is
  correct (the crop is always centered), but a Custom portrait destination's
  crop window is draggable (`SourceCrop_MouseDown/Move` write `dest.CropX/Y`,
  the server honors them), so after a drag the green outline + dim-out stay
  centered while the result box and the relay both use the moved crop. The
  source-frame preview lies about what is kept.
- **Files**: `Views/OutputPreviewElements.cs` (CropOverlay.OnRender),
  `Views/OutputStudio.xaml.cs` (UpdatePreview crop section).
- **Fix**: CropOverlay gained CropX/CropY properties (NaN default = the historic
  centered behavior); UpdatePreview passes the real crop origin (normalized
  layout or ComputeCropRect) so the outline moves with a dragged crop.

### BUG-34. Legacy custom foreground drag uses the wrong width (right ~44% not draggable) - [FIXED]
- The foreground is FgScale of the output HEIGHT and 9:16 wide, i.e.
  FgScale*1080 of the 1080-wide output - its normalized width is plain
  `FgScale` (NormalizedCustomLayout clamps FgX to `1 - fgScale`, and
  RenderLegacyCustom draws `fgW = fgH * 9/16` of a 158-wide canvas ≈ FgScale).
  The result-box drag code instead uses `n.FgScale * 9.0 / 16.0` as the width
  in BOTH the hit test (`CustomResult_MouseDown`:
  `nx > n.FgX + n.FgScale * 9.0 / 16.0`) and the drag clamp
  (`CustomResult_MouseMove`). Effect: clicking the right ~44% of the
  previewed stream never starts a drag, and drags stop early at the right edge.
- **File**: `Views/OutputStudio.xaml.cs` (CustomResult_MouseDown/MouseMove).
- **Fix**: both spots use plain `n.FgScale` for the width, matching
  NormalizedCustomLayout's `1 - fgScale` clamp and RenderLegacyCustom's drawing.

### BUG-35. Watchdog "encoder frozen" can never fire when no snapshot age is known - [FIXED]
- `ParseHealthSnapshot` records an age only if the AGES probe returned a line
  for that destination. `ApplyHealth` reads it with
  `snap.SnapshotAges.TryGetValue(d.Id, out int age)` - a missing age reads as
  0 ("0s old"), so a running encoder with no readable snapshot age is instantly
  marked "was healthy", and the frozen condition (`wasHealthy && age > 60`)
  can never trigger for it. The exact failure the check exists for (encoder
  alive, snapshots not advancing or absent) is invisible whenever the age
  probe yields nothing. Related nit: the AGES glob `/tmp/kat-preview-*.jpg`
  also matches `kat-preview-test-*.jpg`, so a leftover test card could feed a
  destination's age (they are rm'd after each run, so this needs a hard kill).
- **Files**: `DeployService.ParseHealthSnapshot`, `MainViewModel.ApplyHealth`.
- **Fix**: ApplyHealth tracks "age known" (`hasAge`); only a REAL fresh age
  (≤ 30s) marks the encoder healthy. Frozen now fires on a stale file (> 60s)
  immediately, or after two ticks with no readable age (so an encoder restart
  gap - kill, nginx respawn, first frame - stays silent), with the alarm text
  naming the unreadable-snapshot case. The test snapshot moved out of the
  watchdog's glob: `/tmp/kat-preview-test-*.jpg` is now `/tmp/kat-test-*.jpg`
  (pkill token `kat-test`), so a leftover test card can never feed a
  destination's frozen-frame age.

### BUG-36. Test-card verification starves later destinations (serial polling vs 8s timeout) - [FIXED]
- `StartTestEncoders` starts every routed encoder under `timeout 8` (the test
  snapshot is deleted afterwards), then verifies serially: up to 8 x 400ms per
  destination. From roughly the 4th destination on, polling begins after the
  8s window already closed, so those files are gone before they are checked -
  `verified` undercounts ("could not start the test encoders" when the count
  is 0 even though encoders ran) and later outputs' cards may never show the
  card even though it played.
- **File**: `RelayPreviewService.StartTestEncoders` (verification loop).
- **Fix**: one shared 7s deadline with round-robin polling over the still-
  pending destinations (400ms per round) - every encoder gets checked while
  its run is alive regardless of position in the list.

### BUG-37. server/nginx.conf.example contradicts the generated config - [FIXED, doc / mild security]
- The hand-deploy example still shows: the dead `push.tiktokcdn.com` default
  (cleared from real configs in BUG-30 - an nginx start against the example
  restart-loops), the hard `-map 0:a` that killed no-audio streams (BUG-20),
  and `listen 8080` WITHOUT the loopback bind while its own comment claims
  "docker-compose binds 127.0.0.1:8080" - compose actually runs host
  networking now and the real config listens on `127.0.0.1:8080`. Anyone
  deploying from the example exposes /stat to the internet. Also
  `BuildSetupGuide` says "Frankfurt/Ashgate EU" - Ashgate is not a Hetzner
  location (presumably Falkenstein was meant).
- **Files**: `server/nginx.conf.example`, `ServerExporter.BuildSetupGuide`.
- **Fix**: the example now uses `-map 0:a?` (with a comment why), a
  paste-your-ingest TikTok placeholder instead of the dead default host, and
  `listen 127.0.0.1:8080` with a host-networking-accurate comment; the setup
  guide says Falkenstein instead of the nonexistent "Ashgate".

### BUG-38. Light changes re-trigger full Refresh + config-rewrite churn - [FIXED]
- `DestinationConfig.PushLight`/`LiveLight` raise PropertyChanged for
  themselves, and `DestinationPropertyChanged` in MainViewModel calls
  `Refresh()` for EVERY property. Every 10s watchdog tick that flips a light
  therefore regenerates the whole nginx preview and queues a config+secrets
  save (1s debounce) - wasted work and pointless disk writes mid-stream.
  Refresh only depends on the geometry/bitrate/URL-ish properties.
- **Files**: `Models/Models.cs` (PushLight/LiveLight setters),
  `MainViewModel.DestinationPropertyChanged`.
- **Fix**: `DestinationPropertyChanged` returns early for
  PushLight/PushLightText/LiveLight/LiveLightText (mirroring the Output
  Studio's own filter); all config-relevant properties still refresh.

### BUG-39. AtomicWrite's .bak is written but never used for recovery - [FIXED, minor]
- `ConfigStore.AtomicWrite` keeps the previous good file as `.bak`, but `Load`
  on a corrupt config.json falls straight to `CreateDefaults` and ignores the
  backup - the one scenario the .bak exists for is not handled. Similarly
  `SecretsStore.Load` returns an empty SecretsData on any parse error, so a
  corrupt keys file silently shows no keys until the user reopens a file.
- **Files**: `ConfigStore.Load` / `ConfigStore.AtomicWrite`,
  `SecretsStore.Load`.
- **Fix**: both `Load`s try `path + ".bak"` on parse failure (or missing file)
  before falling back to defaults/empty. No UI warning: the stores have no
  channel to the UI at load time, and recovering the data is the substantive
  half - noted here so the simplification is deliberate.

## Third-pass summary

| # | Area | Severity | Status |
|---|------|----------|--------|
| BUG-33 | Crop preview ignores dragged crop position | Low (visual) | FIXED |
| BUG-34 | Legacy custom fg drag hit-test 44% too narrow | Low (UI) | FIXED |
| BUG-35 | Frozen-encoder watchdog blind when age unknown | Medium | FIXED |
| BUG-36 | Test card starves outputs past the 3rd-4th | Medium-low | FIXED |
| BUG-37 | Stale nginx example (dead host, open /stat) | Low (doc/security) | FIXED |
| BUG-38 | Light flips trigger Refresh + save churn | Low (perf) | FIXED |
| BUG-39 | .bak never used for recovery | Low | FIXED |

Verified NOT bugs during this pass (checked, no change needed): the legacy
`BuildLegacyCustomGraph` px clamping (FloorEven bounds both axes); the
base64-in-base64 health/preview command quoting; `MoveKeysFile` reload path;
the OverlayWindow status generation counter; `ChatHub` activate-before-register
and refcounting; `SecretsStore.Capture` duplicate-id handling; Twitch tag
unescaping order; `tpad`+`adelay` delay pairing; `Url()` masking when keys are
hidden; the ComboBox popup MinWidth RelativeSource binding.

## BUG-40: ChatEntry never invoked subscriber lists - overlays deaf to real chat

- **Found**: 2026-10-10, live debugging with per-stage counters (raw/in/shown).
- **Symptom**: overlays rendered injected test messages but NEVER a single real
  chat message, on every build since phase one; the header status looked
  correct only by luck (Subscribe pushes the current status once, and the
  command runner had connected before overlays subscribed). The !commands
  runner was equally deaf through the same path.
- **Root cause**: ChatEntry.Subscribe stored callbacks in _messageSubs/
  _statusSubs, but OnMessage/OnStatus raised unused C# events
  (MessageReceived/StatusUpdated) that nothing subscribes to. The lists
  were never invoked - messages flowed into a void between the client and
  every consumer.
- **Files**: Chat/ChatHub.cs (ChatEntry).
- **Fix**: OnMessage/OnStatus snapshot and invoke the subscription lists under
  a gate (try/catch per subscriber); dead events removed.

## BUG-41: Right-click moderation menu never opened on chat TEXT - [FIXED]

- **Found**: 2026-10-10, Kat's verification of the manual-open fix (880971f) -
  the menu still never appeared when right-clicking actual chat lines.
- **Root cause**: `OnOverlayRightClick` read the clicked line via
  `(e.OriginalSource as FrameworkElement)?.DataContext`. A right-click on the
  words of a chat line hit-tests to the `Run` inside the line's TextBlock -
  and `Run` is a `FrameworkContentElement`, NOT a `FrameworkElement`, so the
  cast came back null and the handler silently returned. Only blank padding
  inside a line resolved to the TextBlock - clicking the text (the normal
  thing to do) never opened the menu.
- **Files**: `Views/OverlayWindow.xaml.cs` (OnOverlayRightClick).
- **Fix**: `FindClickedMessage` walks up from OriginalSource through the
  content tree (`FrameworkContentElement.Parent` - Run -> TextBlock) and the
  visual tree until something carries a ChatMessage. Also no more silent
  failures: right-clicking a real chat line now ALWAYS shows something - the
  mod menu when applicable, otherwise an explainer ("log in to Twitch ..." /
  "<platform> moderation isn't wired up yet"). Right-clicking empty space or
  the header still does nothing by design.

## BUG-42: Chat Overlays tab cut off at the bottom - [FIXED]

- **Found**: 2026-10-10, same verification pass (screenshot).
- **Symptom**: the tab's left column was a bare StackPanel - the overlays
  ListBox (which drives the whole "Settings for:" panel) sat below the window
  edge and was unreachable, and the "Toggle click-through on ALL overlays"
  button label clipped mid-word at the column edge.
- **File**: `MainWindow.xaml` (Chat Overlays tab).
- **Fix**: left column is now a ScrollViewer, reordered for how it is used:
  Overlays list (with Add/Remove and the click-through toggle, whose label
  now wraps) on top, then My channels, then the Twitch account setup and
  chat diagnostics. Nothing can be cut off at any window size.

## BUG-43: Moderation menu opens but is unreadable - [FIXED]

- **Found**: 2026-10-10, Kat's verification of the BUG-41 fix (screenshot:
  menu items are faint gray on a translucent default chrome).
- **Root cause**: the app themes every control EXCEPT ContextMenu/MenuItem,
  so the code-built menu rendered with default bright menu chrome - and the
  app-wide implicit light TextBlock foreground washed its text out on it.
- **Files**: `App.xaml` (new ContextMenu + MenuItem templates, same pattern
  as the BUG-24 ToolTip fix).
- **Fix**: dark menu template (solid #1F232B panel, border, light text,
  blue highlight on hover); disabled explainer items dim via Opacity (a
  Foreground change would lose to the implicit TextBlock style).

Layout round 2 (same verification): the "Settings for:" panel hugged the
left column with a dead zone to its right on wide windows. Round 3 after
Kat's feedback: the proportions were still wrong - the channel/account side
should be the DOMINANT section and "Settings for:" the small one. The grid
is now: left column = * (fills the window, min 420), right column = fixed
500 for the compact settings panel; the two long hints moved under their
rows so nothing clips at 500.
