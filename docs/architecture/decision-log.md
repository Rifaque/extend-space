# Architecture decision log

The durable record of ExtendSpace's architecture and product decisions. Decisions are appended,
never rewritten; a later decision that changes an earlier one says so and links back.

- **D1 to D51** are in the decision register of
  [production session architecture, section 11](production-session-architecture.md#11-decision-register).
- **D52 to D59** are in [section 19.8](production-session-architecture.md#198-decisions) of the same
  document, with their full write-ups in sections 19 and 20.
- **D60 to D63** were written up only in sections 20.11 to 20.13 and the D63 evidence folder; they
  are summarised below so that the numbering has one index.
- **D64 onwards** are recorded here in full.

## D60 to D63, summarised

| # | Decision | Where |
| --- | --- | --- |
| D60 | A new Android decoder keeps the 30-picture startup allowance until it has caught up (output produced and fewer than 4 waiting) or 1 s has passed; what waits is bounded at 16 MiB in every phase. | [Section 20.11](production-session-architecture.md#2011-cold-start-backlog-drops-d60) |
| D61 | A keyframe offered to a full decoder backlog supersedes what waits; the receiver alone owns recovery (asks at once, again after 1 s with no keyframe); the link never drops a keyframe request while streaming and merges waiting ones. | [Section 20.12](production-session-architecture.md#2012-fast-recovery-from-a-backlog-d61) |
| D62 | Host keyframe-request observability: `keyframe <event> t=<µs>` lines for client requests, gate decisions, releases, the first keyframe after requests, its send, and the pipeline's obligation. No behaviour change. | [Section 20.13](production-session-architecture.md#2013-host-keyframe-request-observability-d62) |
| D63 | Investigation only, no source change: one V19l run on the Xiaomi Pad 7 with a 300 ms stall showed the 404 ms recovery round to be the host's own queue-full request spending the gate's 500 ms window on a keyframe (K1) that a second overflow then discarded unsent, so that the client's request waited for the rest of the window. | `artifacts/d63-20261122/` (git-ignored evidence); resolved by D64 |

---

## D64: Host video recovery is asked for when the writer can send, and a queued keyframe survives overflow

**Date:** 2026-11-22. **Status:** decided and implemented; validated by .NET unit and loopback-socket
tests and deliberate breakage; no device run yet (see Validation).

### Context

D63 established, from one physical run, the mechanism behind a roughly 400 ms recovery:

1. The client froze for 300 ms; the host's writer blocked on the socket; the 4-frame send queue
   overflowed. `VideoSendQueue` dropped everything and refused frames until a keyframe.
2. `HostConnectionController.OfferVideo` asked the gate for a keyframe **at the overflow**
   (`host_queue_full`). The gate passed it (nothing had passed for 4.5 s) and opened its 500 ms
   window. The pipeline forced K1 within 2 ms; K1 was queued into the empty queue.
3. The writer was still blocked. Three frames queued behind K1; the next one overflowed the queue
   again, and the overflow policy dropped **everything**, K1 included. K1 never left the PC.
4. The second overflow's request, and every refused frame after it (27 in all), were merged into a
   request deferred to the end of the window. The client's own `decoder_backlog` request, 41 ms
   later, was merged into the same one. The release came 395 ms after the client asked; the client
   received its keyframe 404 ms after asking.

What the source also showed:

- Every frame refused while the queue waited for a keyframe called the gate (`host_awaiting_keyframe`),
  so at every stream start the first refused frame spent the window too, and a client `startup`
  request was deferred and produced a second keyframe (section 20.13, "seen in the source").
- A frame too large to send was dropped **without** using up a sequence number and without making the
  queue wait for a keyframe, so the frames predicted from it would have been sent as if nothing were
  missing, and decoded against a missing picture. (Never seen: it needs an access unit over 8 MiB.)
- The gate belonged to the controller and was not reset per connection, so a request deferred on one
  connection could be released on the next one's writer.

"Request passed", "keyframe produced", "keyframe queued", "keyframe written" and "keyframe received"
were being treated as one event by the gate's accounting: the window was spent at "passed", whatever
became of the keyframe.

### Options considered

**A. Keyframe-aware overflow retention alone.** Keep a queued keyframe and the frames that depend on
it when the queue overflows; drop only what follows.
- For: K1 would have survived and been sent; ordering and decodability are preserved by construction
  (only frames ahead of a keyframe, or after the kept run, are dropped); no protocol change; memory
  bound unchanged (still 4 frames).
- Against: **does not fix the latency.** After K1 and its three dependants, the stream still breaks
  (the frame that overflowed is gone), a new keyframe is still needed, and the gate's window, spent
  at K1's pass, still holds it for up to 500 ms. The client would see a slightly fresher frozen
  picture for the same ~400 ms.

**B. Gate accounting: reopen the window when a passed keyframe is discarded unsent.**
- For: directly addresses "the window was spent on a keyframe that never left".
- Against: during a long stall every overflow would discard the latest forced keyframe and reopen the
  window, so the host would force a keyframe every ~5 frames (about 12 a second) while nothing can be
  sent, each one stale, and CBR rate control would starve the frames after them. On a link that is
  slow rather than stalled, a keyframe storm makes congestion worse, which is what the gate exists to
  prevent. Needs keyframe identity tracked from the pipeline through the queue.

**C. Let client requests bypass or pre-empt a host-consumed window.**
- For: small change; would have cut round 1 to a few ms.
- Against: weakens the protocol's rate limit (section 10) against the client; still forces K1 during
  the stall and wastes it; costs two keyframes within ~100 ms on every such recovery; a client
  request arriving while the writer is still blocked would force another stale keyframe.

**D (chosen). Ask when the writer can send, plus retention (A), plus three related corrections.**
- Overflow does not ask the gate. It records on the link that a recovery keyframe is owed. The
  writer, at the top of its loop (so only when no write is in progress, which means the socket has
  taken everything given to it), takes the obligation and asks the gate, unless a keyframe has been
  queued meanwhile (for example the encoder's periodic one), in which case it asks nothing.
- A frame refused while the queue waits for a keyframe asks for nothing: the keyframe it waits for
  is always owed already (to the stream's start, D27, asked by the coordinator directly; to an
  overflow, asked by the writer; or to a too-large frame, asked at once).
- Overflow retention (A) in `VideoSendQueue`, so that a queued keyframe is lost only to a newer
  keyframe or to the end of the connection.
- A too-large frame uses up its sequence number and makes the queue wait for a keyframe.
- The gate is reset when a stream starts.

### Rationale

- No keyframe is forced while nothing can be sent, so none is wasted and none spends the window.
  During a stall of any length the host forces **zero** keyframes (before: one per 500 ms, each
  discarded).
- When the stall ends, the first request is made at once and passes (the window is unspent), so the
  keyframe is made from a picture captured after the stall and is sent within the encoder's ~2 ms.
  For the D63 round this removes the ~395 ms gate wait. Expected client-side recovery is the
  encode time plus transit, as in D63's round 2 (8 ms), but that is **not yet measured** on the
  device.
- The 500 ms gate keeps protecting a congested link: a link that is slow rather than stalled still
  gets at most one requested keyframe per 500 ms.
- Retention makes "a keyframe that was queued is sent unless superseded" an invariant, which the
  later reasoning (and the tests) rely on.
- The protocol and wire format are unchanged; the client is unchanged.

### Consequences

- The gate's `host_awaiting_keyframe` source is no longer produced by the controller (kept in the
  enum for the gate's own tests and log names).
- Two new log lines (architecture section 20.13's format): `keyframe recovery_owed t= dropped=
  kept_keyframe=` once per overflow recovery, and `keyframe recovery_writer_free t= owed_ms=
  action=request|none_keyframe_queued` when the writer takes it.
- At a stream start the client's `startup` request now passes at once instead of being deferred
  behind a host request, which removes a redundant second keyframe per stream.
- Residual, unchanged by D64: if the client notices a gap or backlog only **after** the post-stall
  keyframe was passed, its request waits for the rest of that window (protocol section 10's limit).
- Residual: when the writer is merely slow (a large keyframe write), the request waits for that
  write to finish, a few milliseconds on USB.

### Validation

- `VideoSendingTests`: D63 round 1 replayed at the queue (K1 survives the second overflow with its
  dependants, in order; the next keyframe follows the gap); only frames ahead of the newest
  keyframe are dropped when that makes room; a newer keyframe replaces a kept one; the old
  "no keyframe queued" behaviour kept; 200 random runs with random writer timing never send a
  non-keyframe after a gap and never exceed capacity; `AwaitKeyframe`.
- `HostConnectionControllerTests`, over a real loopback socket with a client that stops reading:
  no request while the writer is blocked, one `recovery_owed` line however many overflows; once the
  client reads, one `recovery_writer_free action=request` and one `gate_passed
  source=host_queue_full`, and the answering keyframe arrives; an overflow answered by a keyframe
  before the writer is free asks nothing; a too-large frame leaves a gap and nothing after it is sent
  before a keyframe. The D62 log test was rewritten for the new rule.
- `KeyframeRequestGateTests`: reset drops the deferred request and the interval.
- `PipelineSessionIntegrationTests`: the assertion that the overflow had asked for a keyframe
  **while the client was not reading** described the old behaviour; it now checks the startup
  request during the stall and the overflow's request after reading resumes. The gap-then-keyframe
  property it checks is unchanged.
- Deliberate breakage, each alone, source restored and hash-checked: pre-D64 overflow (keyframe
  dropped) failed 3 tests; asking at the overflow failed 2; the writer never asking failed 2; a
  too-large frame without a gap failed 1; the writer asking although a keyframe was queued failed 1
  (this one survived at first and a test was added for it).
- **Not yet done:** a V19l rerun on the Pad 7 with the 300 ms stall. Expected: no `gate_passed`
  between `recovery_owed` and `recovery_writer_free`; the client's request, if it is deferred, is
  answered by the keyframe already in flight; recovery in the order of round 2's 8 ms. The command is
  in ISSUES.md (ES-031).

---

## D65: Connect is enabled for USB Mirror, with the device chosen on screen

**Date:** 2026-11-22. **Status:** decided and implemented; unit-tested; not yet run from the UI on
hardware.

### Context

Until now `SessionCapabilities.CurrentBuild` disabled Connect for every selection, so the production
path could be started only by gated acceptance tests on the PC and a debug-only screen on the
tablet. The device was the only one attached or `ANDROID_SERIAL` (D43); several devices with none
chosen was a terminal error with no UI to choose. Section 20.8 listed what was to be done "before
Connect can be enabled": V17, V18, parts of V6, V7, V11, V14, the release display screen, D43, D51,
and stage 6.

### Options considered

1. Keep Connect disabled until every item of section 20.8 is closed. Rejected: several items need
   the owner's hands (V18 revoke authorisation, V17 a second device) or are open research (stage 6
   capture-starvation detection, D51); the product would stay unusable without a developer
   workaround, which this phase exists to remove.
2. **Enable USB Mirror now; keep the unrun acceptance items as beta blockers in the issue ledger.**
3. Enable it behind a hidden switch. Rejected: a second configuration to test, and the gated tests
   already serve developers.

### Decision

- `CurrentBuild` reports USB and Mirror available; Wireless and Extend stay unavailable with their
  reasons.
- The Connect screen lists Android devices (`adb devices -l`, every 2 s while a device can be chosen,
  and on demand) with model, serial and state; it chooses the only usable device by itself, never one
  of several; it keeps the choice by serial across refreshes, drops it when the device goes away, and
  enables Connect only for a usable chosen device, saying what to do otherwise (no device, adb
  missing, unauthorised, offline). The choice is fixed while a session runs.
- The choice reaches the transport through `HostDeviceSelection`, read by `UsbTransport` when it
  chooses a session's device (`UsbTransportOptions.DeviceSerialSource`), taking precedence over
  `ANDROID_SERIAL`. D43's rule is otherwise unchanged: the transport checks the device again, keeps
  it for the session (a reconnection never picks up another device), and still never guesses among
  several.
- Mirror captures the main display, as the pipeline already did; the mode text says so.

### Consequences

- D43's "no UI to choose yet" is closed. D51 remains open but matters less: a wireless-debugging adb
  device is shown in the list and can be chosen deliberately; it is auto-chosen only if it is the only
  usable device.
- The remaining hardware acceptance (V6, V7, V11 frozen host, V14, V17, V18, V9 and stage 6) moves to
  ISSUES.md as beta blockers, not as a gate on the button.

### Validation

`DeviceSelectionTests` (10): no device and adb missing disable Connect with the reason; the only
usable device is chosen and handed to the transport at start; several are never guessed; unauthorised
and offline are listed but not connectable, and become connectable when they turn ready; the choice
survives reordering and is dropped on removal; an unlisted device cannot be chosen; the choice is fixed
during a session; overlapping refreshes run once; Wireless needs no USB device. `UsbTransportTests`:
`-l` parsing with model; the on-screen choice wins over `ANDROID_SERIAL` and is kept for the session
and read again for the next; discovery's adb failure classes. The three tests that asserted "Connect
is disabled in this build" were rewritten to the new truth. Not done: running the WinUI page itself
(no UI automation exists; the XAML compiles with x:Bind checking).

---

## D66: The Android release session screen

**Date:** 2026-11-22. **Status:** decided and implemented; the state mapping is unit-tested; the
screen has not been run on hardware.

### Context

Only the debug harness `sessiontest/SessionDisplayActivity` lent a surface to the production
controller. The release Connect screen could not start a session.

### Decision

- `ui/session/SessionActivity` (main source set, not exported): full screen, immersive, screen kept
  on; a `SurfaceView` fitted at 16:9 and letterboxed (the host's policy streams only 16:9, D16); it
  lends and withdraws its surface exactly as the harness does (D4, D59), so rotation, Home and
  screen-off follow the paths already accepted on the Pad 7 with the harness.
- What covers the picture comes from one pure function, `sessionOverlay(state)`: waiting for the PC
  (with instructions), starting, reconnecting, ending, ended (Wait again, Close), failed (the reason;
  Try again, Close); nothing while streaming, where a tap shows an "End session" control for 4 s.
- Leaving the screen (Back, Close, or the activity finishing) ends an active session or clears a
  failure, so a session is never left running without its screen. Rotation does not end it.
- The Connect screen's primary action is **Wait for PC** (clears a seen failure first, then
  `waitForHost(USB)`, then opens the session screen); with a session in progress it is **Open
  session**; a previous session still releasing is reported instead of failing silently. The hosts
  list (wireless only) is hidden while empty, and the "How to connect" steps replace its
  placeholder.
- No foreground service (D5 stays open): Home or screen-off longer than the 30 s grace ends the
  session, as before.

### Consequences

- The debug harness is unchanged and still used by the gated host tests (`rewait` and `listen`
  extras); the release APK contains neither it nor the experiments (checked with `dexdump`).
- The stream's real aspect ratio is not sent to the screen; a non-16:9 stream would be letterboxed
  at 16:9. Recorded as ES-040 for Phase 5.

### Validation

`SessionOverlayTest` (6): every state's content and actions, every error code's sentence, and the
states in which leaving ends the session. Debug and release builds, lint (no new code finding).
Not done: the screen on a device (Phase 3's owner-run acceptance list, ISSUES.md ES-030).

---

## D67: Stream health, layer 1, with one bounded action

**Date:** 2026-11-22 (Phase 5). **Status:** implemented; unit-tested; thresholds unvalidated on hardware (D8 stays open for the values).

**Context.** Section 8.2 designed a two-layer stall detector. Layer 2 (telling starvation from a still screen) needs an experiment first and stays unbuilt. Nothing implemented layer 1; the Windows UI said nothing about a frozen or struggling stream.

**Options.** (a) A capture timeout ("no frame for N s = stall"): rejected, section 8.1 shows it fires on every still screen and missed three of four starved runs. (b) Restart capture on low activity: rejected (D31; would turn a still screen into churn and could cascade into encoder and transport resets). (c) **Layer 1 as designed: classify each second from neighbouring counters.**

**Decision.** `StreamHealthMonitor` (pure) classifies each sample as `Flowing`, `Quiet`, `EncoderStalled` (submitted, nothing out for 500 ms), `TransportStalled` (queue drops in 3 consecutive seconds), `ClientStalled` (client receiving, rendering nothing for 2 s), or `NoClientReport` (2.5 s, counted from the stream's start). `StreamDiagnostics` samples once a second while streaming, logs each change, and takes the only action section 8.3 allows: a forced keyframe at most every 2 s while the client is stalled. `Quiet` is described to the user as normal. **Deviation from section 8.3:** an encoder stall is reported, not made terminal, until the threshold has been seen on hardware; a false terminal fault would end healthy sessions.

**Validation.** `StreamHealthMonitorTests` (11) including boundaries; deliberate breakage of five rules all caught.

## D68: Display choice by device name

**Date:** 2026-11-22 (Phase 5). **Status:** implemented; unit-tested.

**Context.** The pipeline already took a monitor index (`SessionRequest.MonitorIndex`), but an index can point at another monitor after Windows renumbers. **Decision.** The Connect screen lists monitors (`DisplayCatalog`, EnumDisplayMonitors in pixels under PerMonitorV2) with label, resolution, refresh and main flag; the main display is the default and is requested as "main" (so it follows Windows' primary); another is requested by device name, and the capture fails with `monitor_not_found` ("no longer connected") rather than capture a different one. The choice is kept by name across refreshes, falls back to the main display if its monitor goes, and is fixed during a session. **Rejected:** friendly monitor names via the DisplayConfig API (more interop, no data that numbers confuse users). **Validation.** `DisplayAndHealthTests`, `MonitorSelectionTests`.

## D69: The picture is fitted to the stream's own shape

**Date:** 2026-11-22 (Phase 5). **Status:** implemented; unit-tested. Supersedes D66's fixed 16:9.

The client now publishes the stream's size (`ConnectionController.stream`, from `stream_config`), and the session screen sizes its surface with `fitPicture`: the largest size of the stream's shape inside the screen, centred, never stretched or cropped. Tested for tablet and phone shapes in both orientations and a sweep of 42 combinations. The protocol is unchanged.

## D70: Closing the Windows app ends the session first

**Date:** 2026-11-22 (Phase 6). **Status:** implemented; unit-tested (`SessionShutdown`); not run on hardware.

**Defect found (ES-100).** Closing the window during a session ended the process with the session running: the tablet saw a lost link and showed "Reconnecting" for 30 s, and the adb forward was never removed. **Decision.** The window's `Closing` is cancelled once, the session is stopped, and the window closes when the session reaches `Disconnected` or `Failed`, or after 11 s (just over the 10 s teardown deadline, D50), whichever is first.

## D71: One version for both apps

**Date:** 2026-11-22 (Phase 7). SemVer with a pre-release label, starting at **0.2.0-beta.1** (0.1.0 was the pre-Connect development version). Android `versionCode = M·1,000,000 + m·10,000 + p·100 + n` (n = beta number, 99 final). Details in [release process](../release/release-process.md).

## D72: Windows: a self-contained ZIP with a per-user installer; Android: signing from a git-ignored file

**Date:** 2026-11-22 (Phase 7). **Status:** implemented; package validated in scratch folders; Android signing proven with a throwaway key.

**Options (Windows).** MSIX: cannot be installed at all without a trusted signature, and no certificate exists. A classic installer (Inno Setup, WiX): a new toolchain, and still unsigned. **Chosen:** a self-contained `dotnet publish` (no .NET or Windows App SDK runtime to install) in a ZIP with `Install.cmd` → `Install-ExtendSpace.ps1`: per-user folder, Start menu shortcuts, upgrade in place, uninstaller; no admin, no registry, no services. The build script found and works around ES-110 (publish omits `ExtendSpace.pri` and `*.xbf`). **Android:** release signing read from `apps/android/keystore.properties` (git-ignored); unsigned without it; R8 left off because no minified build has been run on a device.

## D73: adb stays an external prerequisite

**Date:** 2026-11-22 (Phase 7). Bundling Google's prebuilt Platform-Tools is not done because their licence terms (the Android SDK licence) were not confirmed to permit redistribution; building adb from AOSP is a project of its own. The host detects adb on `PATH`/`ANDROID_HOME`, shows its version on Diagnostics, and explains how to install it in the device card, the error advice and the install guide. Owner decision if bundling is wanted.

---

The decisions below were made in **Phase 9** (2026-11-22/23) before any Phase 10 or 11 code. Research,
options and the critique that shaped them: [Phase 9 research](../research/phase-9-research.md).
Status lines are updated in place as the implementation and its verification progress.

## D74: Over Wi-Fi the tablet listens and the PC connects, as over USB

**Status:** decided (Phase 9); implemented in Phase 10. **Context:** the brief described the tablet
discovering and choosing the PC. **Options:** (a) PC listens, tablet connects; (b) tablet listens,
PC discovers it and connects. **Decision: (b).** It keeps the coordinator's connect-and-retry model,
the client's listen-and-serve model and the device choice on the PC unchanged; the PC opens no inbound
port (no firewall rule or prompt); the tablet listens only while its user has it waiting with Wi-Fi
allowed. **Cost:** a deviation from the described flow, accepted; the tablet shows its name and its
paired PCs instead. **Rejected (a):** inverts every lifecycle rule (research section 14, item 1).

## D75: The transport seam secures a connected socket; the host's link runs over a Stream

**Status:** decided; implemented in Phase 10. `IHostTransport` gains `SecureAsync(Socket) → Stream`
(USB: pass-through) and `Kind`; the controller's `Link` reads and writes a `Stream`.
`RoutedTransport` chooses USB or Wi-Fi per session from `HostDeviceSelection`. Android:
`CompositeHostListener` accepts from the local-socket listener and the Wi-Fi endpoint;
`HostConnection.preauthorized` exempts TLS-authenticated connections from the adb uid check.
**Rejected:** a copied wireless connection controller (duplication of the most tested code); a
transport plugin framework (two routes do not justify it).

## D76: Wi-Fi connections are mutual TLS 1.3 with pinned self-signed identities and ALPN

**Status:** decided; implemented in Phase 10. Device ID = SHA-256 of the certificate's
SubjectPublicKeyInfo. ALPN `exsp/0` (session) and `exsp-pair/0` (pairing). The tablet refuses an
unpaired PC's session with a fatal `not_paired` before reading any session message; the PC refuses a
tablet whose key does not match the pin (`authentication_failed`, terminal). TLS is the platforms'
own (SChannel, Conscrypt). **Rejected:** custom channel encryption; CA-based validation.

## D77: Pairing is a commit-reveal numeric comparison inside the TLS connection

**Status:** decided; implemented in Phase 10; **not externally reviewed (ES-120).** The host commits
to a nonce, the tablet answers with its nonce, the host reveals; both show six digits derived from
both nonces and both observed device IDs; both users confirm; trust is stored only when both accept.
Exact specification: [transport architecture section 5](transport-architecture.md#5-pairing-protocol-exsp-pair0).
**Rejected:** PIN + HMAC (offline brute force by an active MITM); SAS without commitment (grindable);
SRP-6a via Bouncy Castle (viable, but 10 MB in an unminified APK, a network NuGet restore, and a typed
code); QR (camera and library; a good later addition); trust on first use.

## D78: Discovery is DNS-SD, advertised by the tablet only while it is reachable

**Status:** decided; implemented in Phase 10. `_extendspace._tcp`, TXT `id`, `v`, `p`. Android
`NsdManager`; Windows' own DNS-SD through a `DeviceInformation` watcher, adapter in
`ExtendSpace.Media`. A manual address is the fallback. **Rejected:** own mDNS code (port 5353 belongs
to the Windows DNS client service); UDP broadcast.

## D79: Identity and trust storage

**Status:** decided; implemented in Phase 10. Windows: P-256 certificate as PKCS#12 protected with
DPAPI (current user) under `%LOCALAPPDATA%\ExtendSpace`; trusted tablets in a JSON file without
secrets. Android: AndroidKeyStore key (non-exportable) and its self-signed certificate; trusted PCs in
the no-backup directory. **Open risk:** AndroidKeyStore keys as a TLS server key on every vendor (Q1).

## D80: Wireless reuses the video path unchanged

**Status:** decided. Same 1080p60/12 Mbit/s policy (D16), same 4-frame queue and keyframe recovery
(D64), same keepalive. Change only on measured Wi-Fi evidence (hardware check W5).

## D81: Input travels on channel 2 as complete contact frames, offered by the host

**Status:** decided; implemented in Phase 11. `stream_config.input = {"touch": true}` offers it;
channel 2 type 1 `touch` carries a sequence number and 1–10 contacts (id, phase, x, y as u16 over the
picture). Same connection as video: the directions are independent (research section 11).
**Rejected:** a second connection for input; JSON input; batching moves.

## D82: Windows injects real touch by default and mouse as a fallback; input is off by default

**Status:** decided; implemented in Phase 11. Touch: `CreateSyntheticPointerDevice(PT_TOUCH, 10)` +
`InjectSyntheticPointerInput`, held contacts refreshed every 100 ms. Mouse: `SendInput` with a gesture
interpreter (tap, drag, press-and-hold right click, two-finger wheel). The PC user chooses Off / Touch /
Mouse; Off is the default (security principle 7). **Rejected for now:** a virtual HID digitiser (a
signed driver); mouse only (not touch).

## D83: Coordinates are normalised to the picture and mapped through the encoder's letterbox

**Status:** decided; implemented in Phase 11. The tablet normalises to the `SurfaceView`, which is the
picture (D69). The PC removes the encoder's bars (`fit(monitor → stream)`), scales to the monitor and
offsets by its virtual-screen origin, read per touch-down by device name. A contact that starts in the
PC-side bars is ignored; one that moves into them is clamped.

## D84: Input is bound to one streaming connection and released on every end

**Status:** decided; implemented in Phase 11. Release on connection end, new stream, session stop,
input turned off, target monitor lost; the tablet cancels contacts on `ACTION_CANCEL`, detach and
pause. The tablet's Back shows the controls first while input is on.

## D85: Extend mode starts from the community Virtual Display Driver

**Status:** decided (research only; nothing built). Milestone E1 uses VDD (MIT, signed through
SignPath Foundation) installed by the user and the existing Mirror capture path; an own IddCx driver
is reconsidered at E5 with data. [Extend mode research](extend-mode-research.md).

## D86: Implementation-time decisions in Phases 10 and 11

**Date:** 2026-11-23. **Status:** implemented; tested as noted in PROJECT_STATUS.md.

1. **Fixed Wi-Fi port 27184**, any free port if taken. A fixed port keeps a paired tablet reachable at
   its last address when multicast is dropped; the port is advertised either way.
2. **A session connection while the tablet is not waiting** (only the pairing window keeps the endpoint
   up) gets a fatal `busy`, which the PC retries within its budget.
3. **Input is not remembered across launches**: Off at every start of the PC app (ES-127), and offered
   to the next connection when turned on. Off takes effect at once.
4. **Back first shows the controls** while touch is on; a second Back leaves. **The background tap no
   longer toggles the controls** while touch is on; a 48 dp handle in the top-right corner does.
5. **Mouse mode thresholds:** tap within 500 ms and 12 px, press-and-hold 600 ms, 120 wheel units per
   48 px of two-finger movement, computed once per frame.
6. **Held touch contacts are re-injected every 100 ms** by a 50 ms timer in `HostInput`.
7. **Wireless state lives in `%LOCALAPPDATA%\ExtendSpace`** and the Android no-backup directory; a
   damaged trust file is set aside and the list starts empty (fails closed).
8. **Windows-only adapters for wireless and input live in `ExtendSpace.Media`** (DPAPI, DNS-SD,
   injectors), keeping Core platform-neutral; renaming that assembly is deferred.
9. **Bouncy Castle is a test-only dependency** on Android (certificates for JVM TLS tests); nothing of
   it ships.

---

The decisions below were made in the **Extend mode mission** (2026-11-23), before its code. Research,
the initial plan and its critique: [extend-mode-driver-research.md](../research/extend-mode-driver-research.md).

## D87: Extend uses the community Virtual Display Driver, installed by the user

**Status:** decided; implemented (detection only); **proven on hardware 2026-11-23** (E-H1). VDD (VirtualDrivers/Virtual-Display-Driver, MIT,
SignPath-signed) is what every peer project uses and the only signed, redistributable option. ExtendSpace
**does not install, update or elevate**: the user installs VDD (winget or its control app), guided by the
app and the docs. **Rejected:** bundling with an elevated helper (privileged attack surface, support of a
third party's regressions); Parsec VDD as the first provider (better lifecycle, binary licence unknown);
SudoVDA (unsigned); own driver now (signing; E5). Supersedes D85's "E1 route" in detail, not in direction.

## D88: ExtendSpace reads the display topology and never changes it

**Status:** decided; implemented. The virtual monitor is found with `QueryDisplayConfig` and
`DisplayConfigGetDeviceInfo` (EDID `MTT`/0x1337, or output technology `INDIRECT_VIRTUAL`); if it is not
part of the desktop the user is told to choose *Extend these displays*. No `SetDisplayConfig`, no pipe
commands, no settings-file writes. **Why:** VDD cannot hot-add or remove monitors (count read at adapter
start), and programmatic topology changes can move physical monitors.

## D89: Extend is Mirror's pipeline with the virtual monitor as source, and a stream shaped to it

**Status:** decided; implemented; **proven on hardware** (E-H2, E-H4, E-H5). The request names the virtual monitor's device name (capture refuses
any other, D68; input maps onto the same rectangle, D83). In Extend the stream is the monitor's shape
fitted in 1920 × 1080 (1080 × 1920 for portrait), even sizes, falling back to the 1280 × 720 envelope;
Mirror keeps D16.

## D90: `stream_config.mode` tells the tablet it shows an extended display

**Status:** decided; implemented. Optional `"mode": "extend"`; absent means mirror. Wording only.

## D91: The virtual monitor's lifecycle is the driver's

**Status:** decided; documented. With VDD the monitor exists while the driver is enabled, whether or not
ExtendSpace runs; ExtendSpace creates nothing, so a crash leaves nothing of its own. Users disable it in
Display settings or VDD's control app. A keepalive provider (Parsec VDD) or our own driver would remove
the monitor with the session; revisit after hardware proof.

## D92: A generic display-mode model chooses the virtual display's mode for the tablet

**Status:** decided; implemented; **verified on hardware** (2026-11-23). `TabletModeAdvisor` (Core, both
hosts) scores each mode by coverage of the tablet's screen, sharpness after the D89 stream envelope and
desktop room, preferring 60 Hz; nothing in it names a device. On Windows, ExtendSpace switches the
virtual display, and only it, between the modes the driver already offers (`ChangeDisplaySettingsEx`,
refused for any display the detector does not classify as virtual); the tablet-shaped custom size
(1616 × 1080 for the Pad 7) is suggested for the user to add in the driver's own settings. ExtendSpace
does not write `vdd_settings.xml` or reload the driver (D88 kept): the reload is VDD's riskiest path and
the folder is writable by every user (ES-146). Measured: 800 × 600 at 30 Hz → 1920 × 1080 at 60 Hz,
physical monitors unchanged, 60 fps Extend.

## D93: Every still press in Mouse mode is a click or a right click

**Status:** decided; implemented. A press lifted before the long press is a left click; one held past it
is a right click even if the 50 ms tick had not yet fired. Removes the 500–600 ms dead zone, the probable
cause of ES-135; the injector itself was measured exact (32,333 points, 40/40 clicks).

## D94: "Bring windows back" is the Windows lifecycle mitigation, on request only

**Status:** decided; implemented. Moves visible, ownerless, non-tool top-level windows whose centre is on
the virtual display to the same offset on the main display. Never automatic; display settings untouched.
Persistence stays the driver's (D91).

## D95: Wi-Fi discovery on Windows is not rewritten

**Status:** decided. ES-134 was the tablet not answering queries on this network; add-by-address and
last-known address already cover it. The portable browser of D103 is available if the Windows API
proves unreliable.

## D96: The Linux host is .NET + Avalonia over the shared Core

**Status:** decided; implemented. `apps/linux`: `ExtendSpace.Linux` (platform backend) and
`ExtendSpace.Host.Linux` (Avalonia 11.3, the same `ConnectViewModel`), referencing
`apps/windows/src/ExtendSpace.Core` and the shared version props (D71). Core stays platform-neutral; no
fork. Host-facing wording comes from `HostPlatform` (D101).

## D97: Linux capture and encoding use GStreamer in-process

**Status:** decided; implemented; Lab-verified. `ximagesrc` (X11) or `pipewiresrc` (portal) → convert and
scale → VA-API, NVENC or x264 → `appsink`, through a small P/Invoke layer; Annex-B access units go to
Core's sink unchanged. `h264parse` (gst-plugins-bad) is used only when present.

## D98: Linux Extend on GNOME and KDE is the ScreenCast portal's VIRTUAL source

**Status:** decided; implemented; **hardware pending** (no GNOME/KDE session was available). The
compositor creates the monitor for the session and removes it when the session closes; the user consents
in the desktop's dialog. wlroots desktops get guidance (`swaymsg create_output`) instead.

## D99: Linux Extend on X11 is a RandR monitor on an enlarged framebuffer (experimental)

**Status:** decided; implemented; partly Lab-verified. `xrandr --fb` grows the screen to the right,
`--setmonitor EXTENDSPACE-VIRTUAL … none` declares the monitor; both are undone on session end and on a
failed declaration. Physical outputs are not touched. Whether window managers treat it as a monitor is
unverified (Xvfb cannot grow), hence experimental (ES-152).

## D100: Linux input uses the RemoteDesktop portal or XTest, never uinput

**Status:** decided; implemented. Wayland (GNOME/KDE): portal pointer and **touch** in stream coordinates,
so input cannot leave the shared monitor. X11: XTest mouse emulation; Touch is refused (no root). Input
stays Off by default.

## D101: Host wording is platform-neutral in Core

**Status:** decided; implemented. `HostPlatform.Name` supplies "Windows"/"Linux"; Android's texts say
"PC" instead of "Windows".

## D102: Linux distribution starts with a self-contained tarball and a per-user installer

**Status:** decided; implemented; Lab-verified (install, upgrade, launch, uninstall, data kept). No root;
the installer checks GStreamer and adb and prints the distribution's command, never runs it. Flatpak is
the next format (ES-153).

## D103: A portable mDNS browser in Core

**Status:** decided; implemented; **verified on the real network** (found the tablet at its new DHCP
address in 3 s). Shares UDP 5353 with any daemon and hears multicast answers; falls back to one-shot
queries. Used by the Linux host; Windows keeps its DNS-SD API (D95).
