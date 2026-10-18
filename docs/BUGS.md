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

## BUG-44: Closing the app while overlays/streaming was either lethal or a ghost - [FIXED]

- **Found**: 2026-10-10, Kat's verification of the alerts push.
- **Symptom**: with a chat overlay active, clicking X on the main window left
  the process running with NO taskbar entry (WPF's default
  OnLastWindowClose + taskbar-less overlay windows) - the app looked like it
  refused to close, and a relaunch hit the single-instance guard pointing at
  a window that no longer existed.
- **First fix** (OnMainWindowClose) overshot: it killed the overlays on an
  accidental click - exactly what Kat feared.
- **Final behavior** (deliberate, per Kat): with overlays on screen OR a
  stream live (relay receiving the OBS feed, or an enabled destination
  pushing/live on its platform), clicking X **minimizes** the main window -
  an accidental click can never kill the overlays, and minimizing hides the
  screen from a capture. Closing again from the taskbar while minimized - or
  closing when nothing is active - exits the app for real
  (`ShutdownMode="OnExplicitShutdown"` + the window's Closing handler;
  `MainViewModel.IsStreamActive` provides the stream evidence).

## BUG-45: Overlay chat and alerts had fixed font and colors - [FIXED, feature gap]

- **Found**: 2026-10-10, same verification pass.
- **Symptom**: the overlay rendered in Segoe UI with a hard-coded near-white
  chat text and a hard-coded gold alert color - no way to match the stream's
  look.
- **Files**: `Models/Models.cs` (OverlayConfig), `Views/ChatLineTextBlock.cs`,
  `Views/OverlayWindow.xaml(.cs)`, `MainWindow.xaml` (settings panel).
- **Fix**: new per-overlay settings in the "Settings for:" panel: Font
  (editable picker; any installed font name; applies to chat + alerts), Chat
  text color and Alert color (hex). Invalid or half-typed hex falls back to
  the built-in default at render time, so typing never breaks the overlay.
  Author names keep their per-user hash colors by design.

## BUG-46: The poll widget sat on screen as an empty box forever - [FIXED]

- **Found**: 2026-10-11, first verification of the poll widget.
- **Symptom**: with the widget enabled but no poll running, an empty bordered
  box (the widget, width 340) stayed on screen indefinitely.
- **Root cause**: `MainWindow.EnsurePollWindow` showed the window eagerly at
  creation, but the service's first "nothing running" publish (state = null)
  fired BEFORE the window subscribed to `PollStateChanged` - and
  `PollService.Publish` dedupes by fingerprint, so every later null was
  swallowed as "unchanged". The hide that would have removed the empty window
  was never delivered; the first real poll would have fixed it, but there was
  never one (the test poll 403'd, see BUG-47).
- **Files**: `MainWindow.xaml.cs` (EnsurePollWindow),
  `Views/PollOverlayWindow.xaml.cs` (Update).
- **Fix**: the widget window is NEVER shown by its creator - it starts
  hidden and only `Update(state)` with real content shows it (re-applying
  the click-through style after Show, since a fresh hwnd loses
  WS_EX_TRANSPARENT). Null state hides; disabled closes.

## BUG-47: "Create test poll" 403 reported as a moderation error - [FIXED, message]

- **Found**: same verification pass (screenshot).
- **Symptom**: the Polls tab status said "create test poll: this account is
  not a moderator of the channel (a login made before this feature needs one
  re-login)" - the first half is `ModerationService.DescribeAsync`'s
  moderation-specific 403 text and is wrong for polls: creating a poll on
  your OWN channel 403s only because the token predates the poll scopes.
- **File**: `Services/PollService.CreateTestPollAsync`.
- **Fix**: 403 is handled before the shared describer with the poll-specific
  message: "the login lacks poll permissions - log out and back in once
  (Channels & account tab)".

## BUG-48: Re-login did not fix the poll 403 - the channel is (likely) not an affiliate - [FIXED, diagnosis]

- **Found**: 2026-10-11, second verification round: a fresh login STILL got
  403 on "Create test poll".
- **Root cause candidate**: Twitch polls/predictions exist only on **affiliate
  and partner** channels - a non-affiliate channel 403s every polls call no
  matter how fresh the token is, and the widget correctly stays hidden (there
  is nothing to fetch), which looked like "the widget doesn't show up".
- **Fix**: the toolkit now reads the logged-in account's `broadcaster_type`
  (`/helix/users`, cached per login) and, for a non-affiliate channel, says so
  plainly instead of hinting at scopes: "Twitch polls exist only on affiliate
  and partner channels - this channel is neither...". The create-test-poll 403
  also surfaces Twitch's own error body message when it is not a scope error,
  so any other reason names itself.

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

---

# Fourth-pass findings (2026-10-11, full rescan) - all [FIXED] same day

Fourth full scan (services, chat clients, views/viewmodels, server docs; three
independent review passes plus a spot-check of every claim against the code).
Found during the poll-widget/OBS-auto-switch verification session. All items
were fixed in the bugfix pass right after the scan; the Fix lines below were
written during that pass.
Severity is impact when it fires; "trigger" notes how easily that happens.

### BUG-49. Output Studio can freeze the UI for 15-30s, repeatedly - [FIXED]
- `RelayPreviewService` guards its SSH work with one shared `Gate`; the same
  Gate is taken ON THE UI THREAD by `StartLivePreview`/`StopLivePreview`
  (Output Studio property changes, preview toggles, `StopServerPoll` on close)
  while background work holds it for long stretches: the 10s watchdog's
  `FetchHealthSnapshot` (30s command timeout), `ConnectSsh` on a dead server
  (15s), `StartTestEncoders` (~8s), the snapshot ladder (~8 sequential SSH
  commands). With the relay unreachable, editing any field or closing Output
  Studio freezes the UI until the background holder finishes, every monitor
  tick.
- **Files**: `Services/RelayPreviewService.cs` (Gate lock sites),
  `Services/DeployService.cs` (ConnectSsh/FetchHealthSnapshot), call sites in
  `Views/OutputStudio.xaml.cs`.
- **Fix**: Start/StopLivePreview never touch Gate anymore - an
  `Interlocked.Exchange` swap of the live CTS is the whole synchronization and
  Cancel() runs outside it, so they are lock-free from the UI thread. The one
  remaining Gate-holder reachable from the UI (`Shutdown()` in the Unloaded
  handler, which disposes the shared SSH session) is marshaled through
  `Task.Run`. The watchdog/go-live/status paths already ran their SSH work on
  background threads (verified during the fix).

### BUG-50. A non-standard author color can CRASH the whole app mid-stream - [FIXED]
- `Views/ChatLineTextBlock.cs:91` builds the author brush with
  `PlatformColors.FromHex(authorColor.Length == 7 ? authorColor : "#" + authorColor)`
  and NO try/catch. `FromHex` only special-cases length 6; a 3-digit Twitch
  color (`#F53` - a documented Twitch IRC quirk), a 5-char or 9-char value, or
  Kick's free-form `color` field yields `##...` inputs that
  `ColorConverter.ConvertFromString` rejects with FormatException - thrown
  inside the dependency-property changed callback during binding, i.e. an
  unhandled dispatcher exception. Note `BrushOrDefault` in the same file does
  this correctly with try/catch; the author path just doesn't use it.
- **Files**: `Views/ChatLineTextBlock.cs:88-92`, `Chat/ChatMessage.cs`
  (`PlatformColors.FromHex`).
- **Fix**: the author color is accepted only when it matches
  `^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$`; anything else falls back to the
  deterministic `HashColor`, exactly like the missing-color path already did.

### BUG-51. An overlay saved with "Shown on screen" off reappears after restart - [FIXED]
- `MainWindow.xaml.cs` `OpenOverlay` calls `window.Show()` unconditionally
  (line ~145), overriding the ctor's `if (!_config.IsVisible) Hide()`. The
  IsVisible *change* handler (line ~140) does the right thing - only the
  initial open is wrong. Untick "Shown on screen" (`MainWindow.xaml:351`),
  restart, the overlay is back on stream.
- **Fix**: `OpenOverlay` now does `if (overlay.IsVisible) Show(); else Hide();`.

### BUG-52. Closed overlay windows leak forever (status timer never stopped) - [FIXED]
- `OverlayWindow` starts a 2s `_statusTimer` in the ctor; `Closed` only
  persists geometry and releases sources. A running DispatcherTimer is rooted
  by the Dispatcher and its Tick lambda captures `this`, so every removed
  overlay (windows, chat history, subscription lists, visual tree) stays alive
  and renders status into a dead window for the rest of the session.
  `_saveDebounce` has the same issue benignly.
- **Fix**: both timers are stopped in the `Closed` handler.
- (Both review passes found this independently.)

### BUG-53. The upstream stream key is written in PLAINTEXT into SETUP.md of every exported/deployed bundle - [FIXED]
- `Services/ServerExporter.cs:225`: `` `Stream key: {cfg.Upstream.StreamName}` ``
  - the push secret that `SecretsData.UpstreamStreamName` exists to keep out
  of config.json, written into a doc file the bundle encourages sharing
  (WinSCP drag-drop), and the bundle `.gitignore` only excludes `nginx.conf`.
  Deploy also stages it under `%TEMP%\kat-relay-deploy` (that copy IS cleaned).
- **Fix**: SETUP.md now says where the toolkit keeps the key instead of
  printing it ("in the toolkit, Upstream section -> Stream name"), with an
  explicit do-not-share note; the bundle `.gitignore` excludes SETUP.md too.

### BUG-54. User-controlled ingest host interpolated UNQUOTED into server shell commands - [FIXED]
- `Services/DeployService.cs:346`: `Run(client, $"getent ahostsv4 {host} ...")`
  with `host = IngestHost(url)` unvalidated; same pattern inside
  `RelayPreviewService` (getent under docker exec). `System.Uri` accepts RFC-3986
  sub-delims in hosts, so a crafted `rtmp://$(...)...` ingest URL in a shared
  config.json (explicitly designed to be shareable) could execute commands on
  the victim's VPS at next deploy/test. Also `RemotePath` is interpolated into
  single-quoted SSH strings where a `'` breaks quoting.
- **Fix**: new `DeployService.IsSafeIngestHost` (`^[A-Za-z0-9.-]+$`, no leading
  dash) - `ValidateIngestHosts` fails the deploy naming an unsafe host without
  ever probing it, and the preview diagnostics skip the probe for one. Every
  `DeployTarget` is built through `MainViewModel.BuildTarget`, which strips `'`
  from RemotePath so the single-quoted remote strings can never be broken out
  of.

### BUG-55. Twitch OAuth login can hang on a silent browser preconnect - [FIXED]
- `Services/TwitchAuthService.WaitForCodeAsync` accepts one connection at a
  time and reads it inline. Chrome/Edge speculative preconnects open empty TCP
  connections; the inline read then blocks the accept loop while the real
  redirect waits on a connection that is never accepted -> "no login response
  arrived" after 5 minutes despite a successful browser login. Intermittent,
  browser-dependent.
- **Fix**: every accepted client is handled concurrently (a task per
  connection); the first request carrying the expected state wins via a
  TaskCompletionSource, per-connection failures are swallowed and the accept
  loop keeps serving.

### BUG-56. The !command self-echo guard compares display-name to login - localized display names can loop a "!"-reply command - [FIXED]
- `CommandRunner` suppresses echoes of its own replies only when
  `msg.Author == auth.Login`, but `msg.Author` is Twitch's `display-name` tag
  (localized; != login for many accounts). With such an account and a response
  starting with `!` (e.g. `!so`), the bot's own reply re-triggers the command
  every cooldown, spamming chat. `TwitchChatClient` never parses the
  `user-login` tag.
- **Files**: `Services/CommandRunner.cs` (~124-135),
  `Chat/TwitchChatClient.cs` (tag parsing ~299).
- **Fix**: TwitchChatClient parses the `user-login` tag into the new
  `ChatMessage.AuthorLogin`; CommandRunner compares that (falling back to the
  display name on older paths).

### BUG-57. A viewer typing "RECONNECT" briefly kills the Twitch chat connection - [FIXED]
- `Chat/TwitchChatClient.cs:129`: `line.Contains("RECONNECT")` matches the
  whole raw line including PRIVMSG text and tags; any viewer posting
  RECONNECT in caps throws the synthetic IOException and drops the
  connection (3s reconnect, brief overlay gap).
- **Fix**: new `IrcCommand` helper extracts the actual command token (after
  the optional @tags and :prefix); only a real server `RECONNECT` command
  triggers the reconnect.

### BUG-58. Malformed OAuth redirect request line aborts the login with a confusing error - [FIXED]
- `TwitchAuthService.cs:~206`: `new Uri("http://x" + parts[1])` throws
  UriFormatException on absolute-form targets (`GET http://localhost:8770/?...`)
  or garbage; the exception escapes the wait loop -> "login failed: Invalid
  URI" instead of waiting for the real redirect.
- **Fix**: the URI parse is wrapped in try/catch - a malformed request line
  drops that connection and the wait continues.

### BUG-59. Chat source keys are format-sensitive - the same channel can connect twice and !commands can double-fire - [FIXED]
- `Chat/ChatSources.cs`: YouTube spec keys use the raw lowercased URL
  (`youtube.com/watch?v=X` vs `youtu.be/X` vs `@handle` = three keys), Kick
  keys differ between manual chatroom id and channel name. Two overlays with
  different spellings of the SAME channel get two connections; the command
  runner then sees every message twice - masked by the default 5s cooldown,
  but with cooldown 0 commands (replies, OBS scene flips) execute twice.
- **Fix**: YouTube keys canonicalize to the video id when the input carries
  one (watch URL, youtu.be, bare 11-char id all collapse), otherwise to the
  bare handle/name/path form; Kick keys by channel name whenever present (the
  manual chatroom id is just another way into the same chatroom), id only for
  id-only entries.

### BUG-60. Concurrent Save() calls race on a fixed .tmp name - a dropped save can lose the rotated Twitch refresh token - [FIXED]
- `ConfigStore.AtomicWrite` uses the fixed `path + ".tmp"`. `LoginTwitch`'s
  Task.Run continuation calls `Save()` off the UI thread while the 15s
  autosave/debounce save on the UI thread; two concurrent writes to the same
  .tmp -> IOException -> swallowed by `Save()` -> that save is lost. Worst
  case: the just-rotated refresh token (Twitch rotates on every use) is not
  persisted -> forced re-login later, silently.
- **Fix**: both halves - `AtomicWrite` uses a unique temp file per write (and
  cleans it up on failure), and `MainViewModel.Save` serializes the whole
  config+secrets save behind a lock, so concurrent savers can no longer
  interleave.

### BUG-61. MJPEG frame parser crashes when the JPEG SOI marker splits across two reads - [FIXED]
- `Services/RelayPreviewService.cs:443`: `frame.Write(buffer, i - 1, 2)` -
  when a channel read ends with `0xFF` and the next starts with `0xD8`, at
  `i == 0` this writes at offset -1 -> ArgumentOutOfRangeException; the catch
  tears down the SSH channel and kills the remote preview encoder. Costs a
  full preview restart per occurrence.
- **Fix**: the SOI marker is written as two explicit `WriteByte` calls - no
  negative-offset slice exists anymore.

### BUG-62. Chat diagnostics button can die until app restart (TLS handshake without timeout) - [FIXED]
- `Services/ChatDiagnostics.cs:~30`:
  `AuthenticateAsClientAsync("irc.chat.twitch.tv")` without a token - a TCP
  connect that succeeds but a TLS handshake that never completes hangs the
  capture forever; `_diagBusy` is only reset in the task's finally, so the
  button stays disabled with no status.
- **Fix**: the handshake runs on the `SslClientAuthenticationOptions` overload
  tied to the same 10s connect window the TCP connect already uses.

### BUG-63. One `null` string value in config.json silently resets the ENTIRE config - [FIXED]
- `ConfigStore.Load` -> `ClearKnownDeadDefaultIngest` calls
  `dest.IngestUrl.Trim()`; STJ assigns JSON `null` over the `""` default, the
  NRE is swallowed by the per-file catch, so both `config.json` AND the `.bak`
  "fail" -> `CreateDefaults()` -> all destinations/overlays/commands gone on
  next start. Hand-edited configs are an anticipated scenario.
- **Fix**: new `ConfigStore.NormalizeNullStrings` walks the freshly
  deserialized graph (reflection, depth-capped, per-property try/catch so the
  pass itself can never throw) and coalesces every null string back to ""
  BEFORE any code - including the TikTok cleanup - touches the config.

### BUG-64. Server preview shows a frozen frame labelled "LIVE" after the feed dies - [FIXED]
- `Views/OutputStudio.xaml.cs`: once the MJPEG live preview delivered one
  frame (`_liveFrames > 0`), the snapshot fallback and status updates are
  permanently disabled for the session - when the stream ends or the SSH feed
  stalls, the box keeps the last frame with "LIVE - this is what the platform
  is receiving" indefinitely. False "all fine" in a diagnostic view.
- **Fix**: the last live frame's timestamp is tracked; after 12s without a
  frame the box stops trusting the picture - the snapshot fallback poll and
  the honest status text resume, and live statuses display again. A frame
  arriving flips it straight back to LIVE.

### BUG-65. The "Toggle click-through on ALL overlays" button skips the poll widget - [FIXED]
- `MainViewModel.ToggleOverlaysLockCommand` toggles only chat overlays; the
  Ctrl+Alt+C hotkey (MainWindow.ToggleAllLocks) also toggles the poll widget.
  After using the button, the widget's lock state diverges from the overlays -
  a "click-through everything" click can leave a clickable widget swallowing
  game clicks.
- **Fix**: one code path - new `MainViewModel.ToggleAllOverlaysLock` toggles
  every chat overlay AND `Config.PollWidget.Locked`; both the button command
  and the hotkey route through it (the widget applies its lock style from the
  existing PropertyChanged subscription, so the window handle is not needed).

### BUG-66. The Ctrl+Alt+X hotkey alias is never unregistered - [FIXED]
- `MainWindow.OnClosed` unregisters only `HotkeyId`; if C was taken and X
  registered, the alias lingers. Harmless today (process exits right after)
  but a hole in the teardown path.
- **Fix**: `OnClosed` unregisters both ids.

### BUG-67. Auto-switch "hold 0 = stay on the scene" is broken by a later different-type alert - [FIXED]
- Raid with hold 0 promises "staying - switch back yourself"
  (`ObsAutoSwitchService` keeps `_activeTarget` set). A later sub/follow alert
  continues the chain, switches away, and after ITS hold returns program to
  the original scene - yanking the streamer off the scene they asked to stay
  on, with a contradictory status line.
- **Fix** (semantic decision: honor the stay): a hold-0 switch enters "stay
  mode". While the stay target is still on program, further alerts are
  IGNORED (with a status line saying so); once the user has switched somewhere
  by hand, the state is stale, is cleared, and the next alert starts a fresh
  chain. Normal hold chains keep their documented "return to the original
  scene" semantics unchanged.

### BUG-68. Test-poll 403 path can block the UI thread (sync-over-async) - [FIXED]
- `PollService.BodyMessage` uses `ReadAsStringAsync().GetAwaiter().GetResult()`
  and `CreateTestPollAsync`'s continuations run on the dispatcher - a stalled
  connection freezes the UI for the socket timeout in the rare non-scope 403
  path.
- **Fix**: `BodyMessageAsync` awaits the read; the caller awaits it.

### BUG-69. Rapidly toggling the poll widget can run two poll loops briefly - [FIXED]
- `PollService.SetEnabled(false)` cancels and nulls `_loop` while the old
  `RunAsync` may still be inside a fetch; immediate re-enable starts a second
  loop -> duplicate Helix polls and stale status overwrites for one cycle.
  Self-resolving, cosmetic-to-minor.
- **Fix**: a generation counter, bumped on every enable/disable; `Publish`
  drops snapshots from an older generation, so an in-flight fetch of a dying
  loop can never overwrite the fresh loop's state (disable also publishes its
  hide with the current generation so the widget still goes away).

### BUG-70. KickChatClient fire-and-forget pongs can collide or hit a closing socket - [FIXED]
- `_ = SendText(...)` for pongs: two `pusher:ping`s close together -> the
  second concurrent `SendAsync` throws inside a discarded task (pong lost,
  Pusher may drop the connection); the `ct.Register(ws.Dispose)` can also
  dispose between the state check and the send. Silent degradation only.
- **Fix**: all sends serialize through a per-client `SemaphoreSlim`; the send
  re-checks the socket state under the lock, and failures (cancel/closing
  socket) are swallowed - a lost ping/pong just reconnects.

### BUG-71. ChatEntry.Subscribe can deliver a stale initial status over a newer one - [FIXED]
- `Chat/ChatHub.cs:~51-60`: the initial status is pushed outside the
  subscriber lock; a client status fired in between paints "starting..." over
  a newer status until the next change. Cosmetic, recurs on every subscribe.
- **Fix**: snapshot-and-swap under the gate - `Status` is written under the
  subscriber lock and Subscribe reads its initial value inside the same lock,
  so any change after the snapshot reaches the subscriber via the list itself
  and always wins.

### BUG-72. Twitch send pacing is computed before the send gate - rapid commands land back-to-back - [FIXED]
- `Chat/TwitchChatClient.cs:~230-247`: two `TrySendAsync` calls both read the
  same `_lastSend`, both sleep, then serialize on `_sendGate` and fire ~0ms
  apart - the intended spacing is not enforced (Twitch may silently drop).
- **Fix**: pacing is enforced while HOLDING the send gate (with a writer
  re-fetch after the wait, since the session can be replaced mid-sleep).

### BUG-73. EventSub ReceiveAsync decodes UTF-8 per chunk - split multi-byte characters corrupt alert text - [FIXED]
- `Chat/TwitchEventSubClient.cs:~221-236`: `sb.Append(Encoding.UTF8.GetString(...))`
  per frame chunk; a multi-byte sequence split at the 16KB boundary decodes as
  U+FFFD. Cosmetic, rare, never crashes.
- **Fix**: bytes accumulate in a MemoryStream and are decoded once at
  EndOfMessage (the same correct pattern the DeployService stream reader uses).

### BUG-74. Typing in the OBS password field recreates the OBS client per keystroke - [FIXED]
- `MainViewModel.ObsPassword` setter -> `RecreateObsClient()` per character:
  websocket client + connect thread churn while typing, and old clients'
  StatusChanged callbacks are not generation-guarded, so a stale
  "connecting..." can overwrite a newer "connected" in the status light.
  Possibly pre-existing; flag for the fix session to confirm.
  Confirmed during the fix.
- **Fix**: the recreate is debounced 500ms (explicit recreates - keys file
  change, enable toggle - supersede the pending one immediately), and each
  client captures its generation; a replaced client's status callbacks are
  dropped.

### BUG-75. VPS-SETUP.md bandwidth claim is ~20x off - [FIXED]
- `server/VPS-SETUP.md:39`: "Twitch+YouTube+Kick at 6 Mbps each is ~1.4 TB per
  8-hour day" - real math (the toolkit's own verified formula): 18 Mbps x
  0.45 GB/h x 8 h = ~65 GB per 8-hour day (~1.9 TB/month at 8h/day). The
  figure matches neither the day nor the month; readers may pick a too-small
  VPS traffic plan.
- **Fix**: corrected to "~65 GB per 8-hour day (~2 TB/month at 8h daily)".

## Fourth-pass summary

| # | Area | Severity | Status |
|---|------|----------|--------|
| BUG-49 | UI freeze: RelayPreviewService Gate held on UI thread | High | FIXED |
| BUG-50 | Author-color parse can crash the app | High | FIXED |
| BUG-51 | Hidden overlay reappears after restart | Medium | FIXED |
| BUG-52 | Closed overlay windows leak (status timer) | Medium | FIXED |
| BUG-53 | Upstream key plaintext in SETUP.md | Medium (security) | FIXED |
| BUG-54 | Ingest host unquoted in server shell | Medium (security) | FIXED |
| BUG-55 | OAuth login stalls on browser preconnect | Medium | FIXED |
| BUG-56 | Self-echo guard vs display-name != login | Medium | FIXED |
| BUG-57 | Chat text "RECONNECT" drops the connection | Low | FIXED |
| BUG-58 | Malformed redirect line aborts OAuth | Low | FIXED |
| BUG-59 | Format-sensitive chat keys -> double fires | Low-medium | FIXED |
| BUG-60 | Save() race can drop the rotated refresh token | Low-medium | FIXED |
| BUG-61 | MJPEG parser crash on split SOI marker | Low-medium | FIXED |
| BUG-62 | Diagnostics button dies on hung TLS handshake | Low | FIXED |
| BUG-63 | null string in config resets everything | Low | FIXED |
| BUG-64 | Frozen preview frame labelled LIVE | Low-medium | FIXED |
| BUG-65 | Click-through button skips poll widget | Low | FIXED |
| BUG-66 | Hotkey alias never unregistered | Low | FIXED |
| BUG-67 | Auto-switch hold-0 broken by later alert | Low | FIXED |
| BUG-68 | Test-poll 403 blocks UI thread | Low | FIXED |
| BUG-69 | Poll widget double loop on fast toggle | Low | FIXED |
| BUG-70 | Kick pong sends can collide | Low | FIXED |
| BUG-71 | Stale initial chat status overpaints | Low | FIXED |
| BUG-72 | Twitch send pacing not enforced under burst | Low | FIXED |
| BUG-73 | EventSub UTF-8 chunk corruption | Low | FIXED |
| BUG-74 | OBS password typing churns the client | Low | FIXED |
| BUG-75 | VPS-SETUP.md bandwidth math ~20x off | Low (doc) | FIXED |

Fix-approach notes (fourth pass):

- BUG-49: Start/StopLivePreview are lock-free (Interlocked CTS swap, cancel
  outside the Gate); only `Shutdown()`'s client disposal still uses Gate and
  that call is Task.Run'd from Output Studio's Unloaded. The watchdog, status
  polling, go-live and the Output Studio snapshot/test-encoder paths were
  verified to already run off the UI thread.
- BUG-54: `IsSafeIngestHost` is the single choke point - the deploy's
  `ValidateIngestHosts` probe, the preview's getent probe and (via
  BuildTarget) the RemotePath quoting all go through validated values only.
- BUG-60: unique temp file per AtomicWrite + `_saveGate` around MainViewModel.
  Save; both halves together make a dropped save impossible.
- BUG-63: NormalizeNullStrings runs before `ClearKnownDeadDefaultIngest` and
  is itself exception-proof (per-property try/catch), so it can never become
  the new reason Load falls back to defaults.
- BUG-67: the "honor the stay" semantic was chosen - while the hold-0 target
  is on program, later alerts are ignored with an explanatory status; the
  documented return-to-original behavior of normal hold chains is untouched.

Scan hygiene: every finding above was double-checked against the source (file
and line verified, or explicitly marked "verify during fix"). Areas examined
and found clean this pass: ChatHub refcounting, EventSub reconnect state
machine, token handling (no secrets in logs/UI), SSH streaming/UTF-8 decode in
DeployService, deploy temp-folder cleanup, AtomicWrite + .bak order,
LiveCheckService, ChatFilter regex/locking, ObsAutoSwitchService gate/hold
logic, poll widget event wiring/dedupe, WPF dispatcher marshaling on all
service->UI paths, handler lifetimes (_hookedLayers, overlay channel
handlers), SecretBox masking, and the ComboBox popup RelativeSource binding.

---

## Fifth pass (2026-10-17) - over the phase-two wave

Scope: quality ladder, audio processing, stream info, key rotation, status
board, resource view, metrics chart, bandwidth watchdog, backups, server
doctor. Harness re-run after fixes: 30/30.

### BUG-76. Restoring a backup was silently undone at shutdown - [FIXED]
- **Cause**: RestoreBackup wrote the restored config.json + keys file, but
  MainWindow.Closed ALWAYS calls `_vm.Save()`, which re-serialized the OLD
  in-memory config over both files during shutdown - the restore was a no-op
  that looked like it worked. The 15s autosave and 1s edit debounce had the
  same race if they landed between the writes and the shutdown.
- **Files**: `MainViewModel` (new `SuppressSave` flag honored by `Save()` and
  `ScheduleSave()`), `RestoreBackup` (sets it and stops `_autoSave`,
  `_saveDebounce`, `_backupTimer` before the first write).
- **Fix**: every save path stands down from the moment the restore confirms
  until the restart reloads from disk; the flag is per-process, so the new
  instance saves normally.

### BUG-77. Ladder suffix sanitizer kept Unicode letters - [FIXED]
- **Cause**: `char.IsLetterOrDigit` accepts �/?/? etc., which then landed in
  the stream key (`key-720p?`) and in the watchdog snapshot filename.
- **Files**: `QualityVariantConfig.Suffix`.
- **Fix**: `char.IsAsciiLetterOrDigit` - stream names stay platform-safe and
  snapshot filenames stay boring.

### BUG-78. Server doctor false-failed a healthy ufw firewall - [FIXED]
- **Cause**: the check ran `ufw status | head -n 20`; a box with 20+ rules
  cut the 1935 line off, so the doctor said "ufw is active but 1935 is not in
  the list" and offered the ufw auto-fix for a firewall that was fine.
- **Files**: `ServerDoctor.BuildScript` (no `head`), `RunAsync` (verdict now
  needs BOTH "Status: active" and no 1935 rule; inactive ufw reads as
  "absent/inactive", which filters nothing).
- **Fix**: full status parsed in C#; the auto-fix button only appears for a
  genuinely blocking active ufw.

### BUG-79. Deleted ladder rung vouched for a future rung's frozen alarm - [FIXED]
- **Cause**: the variant watchdog's `_variantHealthy` set kept a rung's
  "was healthy" key after the rung was deleted. A replacement rung that came
  up without a readable snapshot could then be declared FROZEN after two
  ticks on the dead rung's evidence, with the main output perfectly fine.
- **Files**: `MainViewModel.HandleVariantHealth` (the no-variants path now
  clears every `<destId>:` key).
- **Fix**: per-destination cleanup alongside the alarm clear.

### BUG-80. Stream info 422 showed a raw Twitch error - [FIXED]
- **Cause**: titles over 140 characters come back as HTTP 422 and fell into
  the generic "apply failed (422)" message with a JSON blob.
- **Files**: `StreamInfoService.ApplyAsync`.
- **Fix**: 422 maps to "Twitch rejected the change - titles max out at 140
  characters".

### BUG-81. Doctor's ufw-fix button state set off the UI thread - [FIXED]
- **Cause**: `UfwFixable` was assigned in the background continuation, so the
  button's Visibility binding and CanExecute requery raced the doctor run.
- **Files**: `MainViewModel.RunServerDoctorAsync`.
- **Fix**: the flag is set inside the dispatcher block that installs the
  checklist.

### BUG-82. Startup stack overflow: Refresh re-entered itself via the key-age label - [FIXED]
- **Found**: 2026-10-17. Any build of the phase-two batch (9761685/c3041e6)
  died at launch: "A new guard page for the stack cannot be created"
  (0xC00000FD), within seconds, before the window ever appeared.
- **Cause**: Refresh() ends by calling RaiseKeyRotation(), which calls
  d.RefreshKeyAge() per destination; that raises KeyAgeText; the
  DestinationPropertyChanged handler did NOT ignore KeyAgeText, so every
  raise re-entered Refresh() - Refresh -> RaiseKeyRotation -> KeyAgeText ->
  DestPropChanged -> Refresh -> ... until the stack died (fresh stack page
  reservation failing). ViewersText has the same shape via the live-check
  viewer update. Both are status OUTPUT, not config input - exactly the
  BUG-38 class the PushLight/LiveLight ignores already cover.
- **Diagnosis** (bisect + %TEMP% kat-bisect.log stage logging): old VM +
  anything = healthy; new VM + anything = crash; disabling G3
  (RaiseKeyRotation in Refresh + LiveCheckTick) was the healthy combination -
  the recursion lived in Refresh -> RaiseKeyRotation.
- **Fix**: DestinationPropertyChanged ignores KeyAgeText and
  ViewersText alongside the lights (early return before the Layers/Variants
  rehook and Refresh). Verified with a title-checked 35s launch: the bisect
  log shows six sequential Refresh runs, zero re-entry.
- **Note**: with the recursion gone, a SECOND startup crash surfaced that the
  stack overflow had always masked - BUG-83 below. Every "still crashes after
  the ignore list" observation during the bisect was that bug, not this one
  (the Windows Event Log's .NET Runtime records prove it).

### BUG-83. MetricsChart wrote to its own frozen Pen - instant startup crash once BUG-82 was out of the way - [FIXED]
- **Cause**: MetricsChart froze its static pens (FreezePen), then the
  constructor did GridPen.DashStyle = DashStyles.Dash; on the already
  frozen pen - InvalidOperationException: Cannot set a property on object
  'System.Windows.Media.Pen' because it is in a read-only state, thrown
  while MainWindow.xaml (line 343) instantiated the control, i.e. a
  deterministic XamlParseException crash at every launch of every build that
  contains the chart (shipped in 9761685 with the metrics feature).
- **Found**: via the Application event log (.NET Runtime, Id 1026) after
  BUG-82's fix stopped the earlier crash from hiding this one.
- **Fix**: the dash style is applied inside FreezePen BEFORE Freeze()
  (new optional DashStyle parameter; the mutating constructor is gone).
- **Lesson recorded**: a "cannot set - read-only" throw inside a XAML load
  names a frozen Freezable being mutated; and when one startup crash is
  fixed, re-read the event log - the next crash in line may have been there
  all along.

### BUG-84. Server doctor false-warned "MTU/MSS hardening not detected" on a hardened box - [FIXED]
- **Found**: 2026-10-18, live release-testing against the real relay. The
  deploy had JUST applied the hardening ("MSS clamp on OUTPUT+FORWARD,
  tcp_mtu_probing=1" in the deploy log; verified on the server: both mangle
  rules present, sysctl.d file written, boot unit enabled) - and the doctor
  run seconds later still warned "not detected".
- **Cause**: two stacked shell mistakes in the doctor's MSS probe, all in one
  line: \iptables -t mangle -S OUTPUT 2>/dev/null | grep ... | head -n1 ||
  sysctl ... || echo unknown\. (1) \head -n1\ exits 0 even on EMPTY input,
  so the \\\||\\\ fallbacks were unreachable - an empty probe could never fall
  through to the sysctl check. (2) the probe ran as the plain SSH user; on
  nft-backed iptables a non-root read fails with "Permission denied" (stderr
  suppressed), so the section was empty on every non-root deploy - exactly
  the toolkit's default setup (opc@ubuntu). My first fix kept the same trap
  in the then-branch (detected via sudo, printed via non-root iptables).
- **Fix**: the probe is now an if/else on a REAL exit status:
  \if iptables ... | grep -q clamp-mss-to-pmtu || sudo -n iptables ... |
  grep -q clamp-mss-to-pmtu; then echo 'clamp-mss-to-pmtu rule present';
  else sysctl -n net.ipv4.tcp_mtu_probing || echo unknown; fi\. Root and
  non-root SSH users both read correctly; a genuinely unhardened box still
  warns via the sysctl value (0/unknown).
- **Lesson**: never put \\\head\\\ between a filter and a \\\||\\\ fallback - head
  always exits 0 and swallows the failure the fallback exists for. Same
  pattern at line 147 (docker --version) audited: benign, the C# fallback
  lands on the same verdict.

## Release-testing session (2026-10-18) - what was verified beyond the harness

- 76-minute app soak through every timer tick: no crash records.
- Full deploy cycle against the live relay (140.238.99.143, Hetzner): current
  config deployed, doctor all green, config matches, 1935 reachable from
  outside, metrics /stat parses.
- Phase-two generator output accepted by the real nginx: a test 720p rung +
  loudnorm chain deployed (in-memory only), verified present in nginx -T,
  relay healthy, then the user's real config redeployed over it.
- Twitch API paths with the stored login: prefill + category search work
  (no re-login needed). The Apply PATCH and everything needing a live stream
  (viewer counts, audible audio, OBS switching) remain Kat-timed.
