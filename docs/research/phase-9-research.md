# Phase 9: research, architecture and plan for wireless and touch

Status: **Phase 9 deliverable, written 2026-11-22 before any Phase 10 or 11 code.** It records what was
read, what was decided and why, the first plan, the critique of that plan, and the plan that was
then implemented. Decisions are numbered D74 to D85 in the
[decision log](../architecture/decision-log.md). Companion documents:
[next-generation architecture](../architecture/next-generation-architecture.md),
[transport](../architecture/transport-architecture.md),
[input](../architecture/input-architecture.md),
[Extend mode research](../architecture/extend-mode-research.md).

Evidence tags used throughout: **[Read]** taken from a source listed in section 19, on the date
above; **[Code]** read from this repository; **[Measured]** run on this PC during the mission;
**[Assumed]** general knowledge not checked against a source; **[Not found]** looked for and not
found. Nothing here was run on the tablet during Phase 9.

## 1. Executive summary

- ExtendSpace already has a clean session architecture with one seam that matters for wireless: the
  host's transport (`IHostTransport`) prepares a route and the connection controller opens one TCP
  socket to it. The Android side already listens and serves whoever connects. **[Code]**
- **Wireless keeps that shape:** the tablet listens (now also on Wi-Fi, only while the user has it
  waiting, with Wi-Fi allowed), advertises itself with DNS-SD, and the PC finds it and connects, as it
  does over USB. Every reconnection, keyframe and teardown rule of the USB path is reused unchanged. The
  PC never opens an inbound port, so no Windows Firewall rule or prompt is needed. (D74)
- **Security before function:** every Wi-Fi connection is mutual TLS 1.3 between long-term
  self-signed identities pinned at pairing; an unpaired peer gets nothing but a refusal; ALPN separates
  pairing connections from session connections. (D76)
- **Pairing** is an explicit numeric comparison: both screens show the same six digits, derived with a
  commit-then-reveal exchange so that a man in the middle cannot choose them, and bound to both TLS
  identities; the user confirms on both devices. Only platform cryptography is used (TLS, SHA-256);
  no new crypto library. (D77)
- **Touch** goes client → host on the protocol's reserved input channel as complete contact frames,
  normalised to the video picture, and is injected on Windows with the synthetic pointer API as real
  touch (up to 10 contacts) or, as a fallback mode, as mouse input. Input is off until the PC user turns
  it on, and is released whenever a connection, stream or session ends. (D81–D84)
- **Extend mode** should adopt the community Virtual Display Driver (MIT, signed through the SignPath
  Foundation) as its first route, before writing a driver of our own; it is researched only. (D85)
- Of the six projects named in the brief, only **TabletMonitor** could be found; the others are
  recorded as not found rather than described. Six other open-source projects were studied instead.

## 2. The existing ExtendSpace architecture

Read from the code on 2026-11-22 (branch `main`, HEAD `8a9669f` plus the uncommitted stage 3–Phase 8
work). **[Code]**

**Windows host.** `App.xaml.cs` builds one `HostSession` through `HostComposition.CreateHost`:
`SessionCoordinator` (the only owner of session state, retries with backoff 250 ms→4 s within a 30 s
budget, bounded teardown) over three seams: `ISessionConnection` (implemented by
`HostConnectionController`), `IVideoPipeline` (`ExtendSpace.Media.DesktopVideoPipeline`:
Windows.Graphics.Capture → NV12 → Media Foundation H.264) and `ISessionClock`.
`HostConnectionController` owns `IHostTransport` (`UsbTransport`: adb, a per-session loopback port
forward re-created before every attempt) and one `Link` per connection: preface and handshake, a
reader loop, a writer loop with a 4-frame `VideoSendQueue` (D64 overflow keeps a queued keyframe),
ping every second with a clock-offset estimate, 5 s silence timeout. The `Link` talks to a raw
`Socket`; the transport hands back only a `TransportEndpoint` to connect to.

**Android client.** `ExtendSpaceApplication` holds one `ClientSessionController` (application scope,
single-threaded dispatcher) over `SessionLink` (`UsbHostLink`: a `LocalServerSocket` on the abstract
name `extendspace`, a uid check that admits only adb's shell user, blocking streams on dedicated
threads, one writer thread with a 16-message queue) and `VideoReceiver`
(`MediaCodecVideoReceiver`). The socket seam under the link (`HostListenerFactory`, `HostListener`,
`HostConnection`) already abstracts "listen, accept, input/output streams, read timeout, close".

**Protocol** (`protocol/specification/protocol.md`, draft 0): preface `EXSP\0`; frames of
channel(1) type(1) flags(2) length(4), big-endian; control channel JSON, video channel binary
(sequence, timestamp, Annex B access unit); channel 2 **reserved for input, 4 KiB limit**, and today
a client frame on it is a fatal `unexpected_message` on the host. Section 3 already requires that a
Wi-Fi binding authenticate mutually before any application data; sections 6 and 15 leave pairing and
the Wi-Fi binding open. Unknown JSON fields and unknown control types are ignored, which is what
makes optional additions possible without a version bump.

**What can be reused as is:** framing, all control messages, video delivery and recovery, keepalive,
the session state machines on both sides, the coordinator's reconnect policy, the client's
"go back to listening" model, the decoder and surface lifecycle, diagnostics.
**What needs change:** the host `Link` must run over a `Stream` (for TLS); the transport seam must be
able to secure a connected socket; the Android listener must be able to be a TLS listener and more
than one listener at once; the input channel needs message definitions; the host must stop treating
client input frames as a violation once input is negotiated.

## 3. UI inventory (from the code)

Screenshots: **not captured in Phase 9.** The Windows app was not launched (UI Automation reads are
used later in Phase 10/11 for verification) and the tablet was not touched. The inventory below is
read from XAML and Compose sources. **[Code]**

| App | Screen | What it shows | Wireless/touch impact |
| --- | --- | --- | --- |
| Windows | Main window | Mica title bar, left navigation: Home, Diagnostics (footer), Settings (built-in) | none |
| Windows | Home ("Connect") | Status card (title, detail, stream health, Disconnect/Connect), error InfoBar, "not available" InfoBar, **Android device** card (adb list, refresh, hints), **Display to mirror** combo, **Connection** radio (USB / Wireless — Wireless disabled with a reason), **Mode** radio (Mirror / Extend — Extend disabled) | Wireless radio becomes live; the device card lists Wi-Fi tablets when Wireless is chosen, with a Pair action; a touch-input choice is needed |
| Windows | Diagnostics | Build, session, adb state, per-second stream table | add transport and input counters |
| Windows | Settings | "There are no settings yet", About | trusted devices (Forget), identity |
| Android | Connect | Status card with Wait for PC / Open session, How to connect, Hosts list (empty), Methods (USB implemented, Wireless "not implemented" badge) | Wi-Fi switch, Pair with a PC, Paired PCs with Forget |
| Android | Session (`SessionActivity`) | Full-screen `SurfaceView` sized to the stream's shape (D69); overlay card for waiting/starting/reconnecting/ending/ended/failed; a tap toggles a small top bar (stream info, End) | taps must go to the PC when input is on; the controls need another way in; Back must not end a session by accident |
| Android | About | Version, licence | — |

How a session starts today: on the tablet, Wait for PC → session screen (waiting); on the PC, choose
device and display → Connect. That PC-chooses-device flow is the one the wireless design keeps.

## 4. Competing and related projects

The six projects named in the brief were searched for by exact name with standard and extended web
search on 2026-11-22. **PadDisplay, SecondScrn, DisplayMaster, FOLD and ezdisplay: [Not found]** — no
public repository or product page with those names and this purpose surfaced, so nothing is said
about them. Two repositories named **TabletMonitor** were found. Other projects were added because
they solve the same problem in the open. Reports are from READMEs, protocol documents and file
listings; where source files were not opened that is said. **[Read]**

| Project | Stack | Extend | Capture / codec | Transport, discovery, security | Input | Lessons |
| --- | --- | --- | --- | --- | --- | --- |
| **hpixsel/TabletMonitor** (MIT) | .NET 10 WPF; Kotlin/Compose | Bundles the signed community **Virtual Display Driver**; a small service does admin-only steps | DXGI Desktop Duplication; Media Foundation H.264 | **Tablet listens (TCP 47800), PC connects**; USB via `adb forward`; Wi-Fi discovery over UDP broadcast 47801; "approve new device" on the PC; encryption and authentication of the session: [Not found] | Full set of active pointers per message, normalised to the video frame, **InjectTouchInput** | Same direction we chose; same pointer-frame model; shows a bundled-VDD route working |
| **FitzVB/FlexDisplay** (MIT) | Rust (warp/tokio); Kotlin | Community VDD, configured by editing its XML and reloading | FFmpeg ddagrab + NVENC/QSV/AMF/x264 | `adb reverse`; Wi-Fi: PC listens on **0.0.0.0:9001**, IP typed by hand; WebSocket; **no authentication or encryption found** | **SendInput mouse** with `MOUSEEVENTF_VIRTUALDESK`, normalised coordinates into the chosen monitor's rectangle | An unauthenticated LAN listener is the anti-pattern to avoid; mouse-only touch is the simplest fallback |
| **TristanSibug/SecondMonitor** (MIT) | C# .NET 10; Java | Community VDD; DisplayConfig to attach/restore | FFmpeg ddagrab, H.264 | USB `adb reverse` only, no Wi-Fi | Multi-touch "TouchInjector" plus a mouse-mode fallback (source not read); notes **elevated windows ignore injected touch** unless the app is elevated | Keep a mouse fallback; document the UIPI limit |
| **efekurucay/anydisplay** (MIT) | Rust host; Kotlin | Community VDD (not bundled) | DXGI DD, MF H.264/HEVC | TCP 47820, **mDNS `_anydisplay._tcp`** or typed IP; ECDH P-256 + HKDF, AES-256-GCM per frame, **6-digit SAS comparison** pairing; whether its SAS uses a commitment: [Not found] | Touch frames listing all contacts, u16 coordinates over the image, **CreateSyntheticPointerDevice / InjectSyntheticPointerInput, 10 contacts**, pen with pressure/tilt; **still contacts re-sent every 100 ms** | Closest to our design; its hand-rolled channel crypto is what we avoid by using TLS |
| **YuioDP/TabletMonitor** | Java (minSdk 17); Python PC | Points to VDD; mirrors otherwise | Screenshots → JPEG, 25 fps | Tablet listens on TCP 5000, PC connects; Wi-Fi by typed IP; no authentication | none | Shows how low the bar is in hobby projects |
| **VirtualDrivers/Virtual-Display-Driver** (MIT) | IddCx (fork of ge9/IddSampleDriver, Microsoft sample) | is the driver | — | — | — | Signed via **SignPath Foundation**, distributed through GitHub releases and winget; configured through `C:\VirtualDisplayDriver\vdd_settings.xml`; whether third parties may bundle it: [Not found] in the README (MIT suggests yes, to confirm) |

Commercial products (spacedesk, SuperDisplay, Duet) were not studied from source; their public pages
say only that they offer USB and Wi-Fi and extend through their own drivers. **[Read]** (search
summaries only)

**What not to copy:** listening on all interfaces without authentication (FlexDisplay, YuioDP);
custom AEAD framing where TLS does the job (anydisplay); bundling adb when its licence terms are
unconfirmed (TabletMonitor, FlexDisplay; our D73 stands); editing a driver's XML and reloading it as
the control path (fragile; Extend milestone E2 has to do better).

## 5. Virtual display research (summary; details in extend-mode-research.md)

- Display **creation** and display **capture** are separate problems. Windows.Graphics.Capture,
  Desktop Duplication and Media Foundation only read or encode pixels of monitors that already exist;
  none can create a monitor. **[Read]** (API documentation) A monitor that Windows extends the desktop
  onto needs a display driver: an **IddCx** indirect display driver is the supported user-mode model,
  runs in Session 0, and Microsoft lists "creating virtual monitors" as one of its scenarios. **[Read]**
- Signing: attestation signing needs a Hardware Developer Program account and an EV certificate;
  Microsoft's documentation now says attestation is "for testing purposes", and attested drivers
  "can't be published to Windows Update for retail audiences"; the retail route is the Windows Hardware
  Compatibility Program (HLK). **[Read]** This is the cost the existing
  [IddCx feasibility](../architecture/iddcx-feasibility.md) study already identified.
- Every open-source Android second-screen project found uses the **community Virtual Display
  Driver**, which is MIT-licensed and signed through the SignPath Foundation. **[Read]**
- Recommendation (D85): milestone E1 proves Extend with VDD installed by the user; owning a driver is
  considered only if VDD's control interface, licence or update cadence proves unworkable.

## 6. Wireless transport research

Requirements from the code and protocol: reliable ordered bytes for control; video tolerant of a
bounded queue; detect a dead link within 5 s; reconnect within 30 s; no unauthenticated listener.

| Option | Latency | Reliability | Security | Effort here | Verdict |
| --- | --- | --- | --- | --- | --- |
| **TCP + TLS 1.3, same framing** | One RTT for TCP, one for TLS 1.3 at connect; head-of-line blocking on loss | Proven by the USB path; the send queue already drops to a keyframe when the writer blocks | TLS from the platforms (SChannel, Conscrypt), mutual auth with pinned keys | Small: the framing, recovery and state machines are reused | **Chosen (D74, D76)** |
| UDP + custom reliability/FEC | Best on lossy links | Everything to build: ordering, congestion control, keyframe repair | DTLS needed; not in .NET's box | Very large | Rejected for now; revisit only with measured Wi-Fi loss |
| QUIC (`System.Net.Quic`, Cronet on Android) | Streams avoid cross-stream HOL blocking | Good | TLS 1.3 built in | .NET needs msquic (bundled on Windows 11); Android has no platform QUIC socket API; a large new stack | Rejected: no Android platform API; protocol section 15 item 3 keeps it open |
| WebRTC | Built for this | Good | DTLS-SRTP | Huge dependency on both sides, signalling needed | Rejected |
| Wi-Fi Direct | Avoids the router | Device-specific; Windows Wi-Fi Direct APIs are separate | Own pairing | Large | Deferred |

**Who listens.** Option A: PC listens, tablet connects (FlexDisplay, Sunshine-style). Option B:
tablet listens, PC connects (both TabletMonitor projects, and our USB path). B is chosen (D74)
because: the coordinator's connect-and-retry model and the client's listen-and-serve model are kept
exactly, so reconnection after an IP change is "browse again, connect again"; the PC needs no inbound
firewall rule (an unpackaged app that listens triggers the Windows Defender Firewall prompt, and
adding a rule needs elevation **[Assumed]**); the device choice stays on the PC, where it already is;
and the listening socket exists only while the tablet user has asked it to wait. The cost is a
deviation from the brief's described flow (tablet picks the PC); the tablet instead shows which PCs it
has paired with and its own name, and the PC user picks the tablet.

**Discovery.** DNS-SD over mDNS (RFC 6762/6763): Android `NsdManager` registers
`_extendspace._tcp` with a TXT record (API 21+; **[Assumed]** no permission beyond INTERNET; a
multicast lock is taken to be safe); Windows browses through the OS DNS-SD stack
(`DeviceInformation` watcher on the DNS-SD association-endpoint protocol). A home-grown mDNS client
was rejected (port 5353 is owned by the Windows DNS client service); UDP broadcast (TabletMonitor) was
rejected (noisier and needs its own responder). Stale entries leave when the watcher reports
removal; the tablet withdraws the advertisement when it stops waiting. A manual "connect by address"
is kept as a fallback for networks that drop multicast.

**IPv4/IPv6.** The tablet listens dual-stack; the PC prefers the IPv4 address DNS-SD returns and
falls back to a global IPv6 one. Link-local IPv6 (needs a scope id) is not used.

**Bandwidth.** The default policy streams 1080p60 at 12 Mbit/s (D16). Typical 5 GHz Wi-Fi gives far
more, but airtime is shared and half-duplex; Wi-Fi can stall for hundreds of milliseconds. The send
queue's drop-to-keyframe (D64) is exactly the behaviour wanted there: show the current picture, not a
backlog. **[Assumed]** until measured (Phase 10 hardware check W5).

## 7. Security research

Threat model for Wi-Fi (in addition to the existing [security model](../security/security-model.md)):
a LAN attacker who can (1) connect to the tablet's port, (2) observe or alter traffic (ARP spoofing,
rogue AP), (3) run a pairing attempt at the same time as the user, (4) flood connections.

- **Identity.** Each install creates a P-256 key and a self-signed certificate once. Device ID =
  SHA-256 of the certificate's SubjectPublicKeyInfo, which is what protocol section 6 already
  proposed. Android: key in **AndroidKeyStore** (non-exportable). Windows: PKCS#12 encrypted with
  **DPAPI** for the current user under `%LOCALAPPDATA%\ExtendSpace` (CNG persisted keys were the
  alternative; DPAPI keeps the file portable to backup-exclusion rules and testable). (D79)
- **Channel.** TLS 1.3 only, mutual authentication, certificates checked by **pinning the SPKI hash**
  against the trusted list, never by a CA. ALPN `exsp/0` for sessions, `exsp-pair/0` for pairing.
  An unpaired peer on `exsp/0` gets a fatal `not_paired` error and the connection is closed before any
  session message is read; nothing reaches `ClientSessionController`. (D76)
- **Pairing ceremony options** examined (prior art **[Read]**: HomeKit uses SRP-6a with a setup code;
  adb wireless debugging uses SPAKE2 with a 6-digit code; Bluetooth Secure Connections numeric
  comparison and ZRTP use a commitment before a short authentication string):

  | Ceremony | Active MITM | Dependencies | UX | Verdict |
  | --- | --- | --- | --- | --- |
  | Trust on first use | Undetected | none | none | Rejected |
  | PIN + HMAC proof over TLS | **Broken**: the first proof sent lets the attacker brute-force a 6-digit PIN offline | none | type a code | Rejected |
  | SAS of both fingerprints, no commitment | **Broken**: the attacker grinds its own TLS randomness until the codes match (~10^6 hashes) | none | compare | Rejected |
  | SRP-6a / SPAKE2 with a typed code | One online guess per attempt | Bouncy Castle: SRP is in `bcprov` (verified in the cached 1.85.2 jar, 10 MB) and `BouncyCastle.Cryptography` on .NET (needs a network restore) | type a code | Viable; rejected for size and dependencies |
  | QR code with fingerprint and one-time secret | Secure | camera + barcode library | scan | Deferred (good later addition) |
  | **Commit-reveal SAS inside mutual TLS** | One guess per attempt: 10^-6 | none (SHA-256, TLS) | **compare six digits, confirm on both** | **Chosen (D77)** |

  The chosen ceremony is the commitment-based short-authentication-string pattern used by Bluetooth
  numeric comparison and ZRTP, composed over TLS; it is a composition of standard primitives, not a new
  primitive, and it is written down exactly (transport architecture section 5) so that it can be
  reviewed. **It has not had an external review**; that is recorded as ES-120.
- **Authorisation.** The tablet accepts a pairing connection only while its user has opened "Pair
  with a PC" (two minutes, one attempt at a time, closed after a mismatch); the PC starts pairing only
  from its user's click. Either side can forget the other; the tablet then refuses that PC's sessions
  (`not_paired`), and the PC no longer offers Connect for that tablet.
- **DoS.** One session at a time; TLS handshakes run on bounded worker threads with a 5 s limit and at
  most four in flight, so a stalled handshake cannot block the paired PC. A LAN attacker can always jam
  Wi-Fi; that is out of scope.
- **Privacy.** The DNS-SD TXT record carries a short device-ID prefix and the device name, visible to
  the LAN while the tablet waits. An active prober could read the certificate anyway. Documented.
- **Logging.** No key, certificate body, SAS or nonce is logged; device IDs are logged only as an
  8-character prefix.

## 8. Android touch research

- `MotionEvent` reports all pointers of a gesture in one event: `ACTION_DOWN` (first),
  `ACTION_POINTER_DOWN`/`_UP` (others, `actionIndex`), `ACTION_MOVE` (all, batched with history),
  `ACTION_UP` (last), `ACTION_CANCEL` (system took the gesture). Pointer **IDs** are stable for a
  contact's life; indices are not. Tool type distinguishes finger, stylus, eraser, mouse. **[Assumed]**
  (platform knowledge; no surprises in the code paths used)
- **Mapping:** the session screen's `SurfaceView` is already laid out to exactly the picture's
  rectangle (D69), so a touch on the view, divided by the view's size, is the position on the picture
  regardless of letterbox, rotation or tablet resolution. Touches in the black bars never reach the
  view. Rotation recreates the activity; the active gesture is cancelled by the system.
- **Lost releases:** a gesture can end without `ACTION_UP` (activity paused, view detached, window
  focus lost). The capture layer therefore sends a cancel for every live contact on `ACTION_CANCEL`,
  on detach and on pause, and the host also releases on any connection or stream end.
- **System gestures:** in immersive mode with transient bars, an edge swipe can still produce Back.
  Back during a touch-enabled stream shows the controls instead of ending the session.
- **Stylus:** pressure, tilt and hover are available in `MotionEvent`; deferred (scope).

## 9. Windows input research

**[Read]** from Microsoft Learn:

- `CreateSyntheticPointerDevice(PT_TOUCH | PT_PEN, maxCount, feedback)` and
  `InjectSyntheticPointerInput` exist from Windows 10 1809; touch up to `MAX_TOUCH_COUNT`, pen exactly
  one. Coordinates are "relative to top left of the virtual screen", in pixels. Each call is an array
  "that represents all contacts". Flag sequences: `INRANGE|INCONTACT|DOWN`, `INRANGE|INCONTACT|UPDATE`,
  `UP`; cancel is `CANCELED` with `UP`. An `UP` must repeat the last `UPDATE`'s location or the call
  fails and **all contacts are cancelled**. For press-and-hold "multiple frames must be sent to ensure
  input is not cancelled". A display change cancels all contacts. Timestamps less than 1 ms apart give
  `ERROR_NOT_READY` (retry).
- `InitializeTouchInjection`/`InjectTouchInput` (Windows 8) are the older touch-only equivalent, not
  marked superseded.
- `SendInput` mouse with `MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK` maps 0–65535 onto the
  virtual desktop. **[Assumed]** (well-known)
- Not documented on those pages: integrity rules. **[Read]** from SecondMonitor and anydisplay
  READMEs: elevated windows ignore injected input unless the injector is elevated (UIPI). Treated as a
  known limitation, not worked around (the host stays unelevated, security principle 6).
- HID/virtual touchscreen drivers (a VHF or UMDF HID minidriver) would give "real" digitiser input
  including the pen, but need a signed kernel or UMDF driver: same signing cost as Extend. Deferred.

| Approach | Native touch semantics | Multi-touch | Pen later | User mode | Chosen |
| --- | --- | --- | --- | --- | --- |
| SendInput mouse | No (clicks and drags; scroll and right-click by gesture interpretation) | No | No | Yes | **Fallback "Mouse" mode** |
| InjectTouchInput | Yes | Yes | No | Yes | Not used (superset below) |
| **Synthetic pointer (PT_TOUCH)** | Yes: Windows does tap, pan-to-scroll, press-and-hold right-click, pinch in touch-aware apps | 10 contacts | Same API with PT_PEN | Yes | **Default "Touch" mode (D82)** |
| Virtual HID driver | Yes, as a digitiser | Yes | Yes | No (driver) | Deferred |

**Mapping on the PC:** normalised picture coordinates → through the encoder's own letterbox (the
stream is the monitor scaled to fit, `OutputGeometry`) → the mirrored monitor's rectangle in
virtual-screen pixels, read under per-monitor DPI awareness (WinUI 3 apps are PerMonitorV2). Monitor
origin and arrangement come from `GetMonitorInfo` by device name at each touch-down, so a monitor
moved in Display Settings is followed. (D83)

## 10. Input experience

- PC: one choice next to Mirror/Extend: **Touch input: Off / Touch / Mouse**, default **Off**
  (security principle 7). Changing it during a session takes effect at once for the PC (contacts are
  released when it is turned off) and is reflected in the next `stream_config`.
- Tablet: when the PC allows input, the picture takes touches; a small translucent handle in a corner
  opens the controls (stream info, End); Back opens the controls first. When input is off, a tap
  toggles the controls as today. No on-screen mouse buttons or toolbars beyond that: two-finger scroll
  and press-and-hold come from Windows in Touch mode and from the interpreter in Mouse mode.
- Distinguishing states: the status lines say "over USB" / "over Wi-Fi"; the PC's device list marks
  each tablet "USB", "Wi-Fi, paired", or "Wi-Fi, not paired"; Mirror vs Extend stays the Mode choice.

## 11. Performance and latency

| Contribution | Expected | Basis |
| --- | --- | --- |
| Android touch to event delivery | 4–16 ms (panel sampling; batched to vsync) | **[Assumed]** |
| Encoding and writing a touch frame | < 0.1 ms; ~40 bytes per frame | **[Code]** design |
| USB (adb forward) one way | ~1 ms (RTT 1–2 ms measured in stage 4) | **[Measured]** earlier stages |
| Wi-Fi one way | 2–10 ms typical, spikes of 100+ ms | **[Assumed]** |
| TLS record processing | microseconds per frame (AES-GCM) | **[Assumed]** |
| Injection | < 1 ms | **[Assumed]** |
| Capture → encode → send → decode → display | 30–60 ms measured on USB in Phase 2.4 | **[Measured]** earlier stages |

**Same connection for input and video?** Video flows host → client; input flows client → host. On a
full-duplex TCP connection the two directions have separate send buffers, so touch can never queue
behind video; only TCP ACKs for video share the client's direction and are tiny. On the client, input
shares the single writer thread and its queue with pongs, stats and keyframe requests, all small.
**A separate input connection is therefore not justified**; input frames are never dropped, and
consecutive moves are not batched. On Wi-Fi the radio is half-duplex, so heavy video can delay input
airtime; the remedy is the same bounded video queue, not a second socket.

## 12. Architecture comparison

| Concern | Separate wireless stack | **Shared session over a pluggable secured stream (chosen)** |
| --- | --- | --- |
| Code duplicated | Handshake, keepalive, recovery twice | None; transport-specific code is route + security only |
| USB risk | none | `Link` moves from `Socket` to `Stream`: covered by the existing 470+ controller tests |
| Reconnect on IP change | new logic | existing coordinator retry; the route re-resolves |
| Future Extend | two places to change | one: Extend is a different display source |

## 13. Initial implementation plan (as first written)

1. Windows listens on a TCP port for Wi-Fi; the tablet discovers PCs and connects; the client gets a
   reconnect loop of its own.
2. Pairing by a 6-digit PIN shown on the PC and typed on the tablet, checked by an HMAC over the TLS
   channel binding.
3. Wireless gets its own `WirelessHostConnection` copied from `HostConnectionController` with TLS
   added.
4. Discovery with a small mDNS implementation in `ExtendSpace.Core`.
5. Input on a second TCP connection so that it cannot wait behind video.
6. Touch injected with `SendInput` mouse events only.
7. The Android screen keeps tap-to-toggle controls; touches go to the PC only on long press.
8. Tests: unit tests per class; hardware later.

## 14. Critique of the initial plan

1. **PC listening** inverts every lifecycle rule in the code: the coordinator would wait for a
   connection instead of making one, the client would need its own reconnect policy, the Android link
   would become a client, and the PC needs a firewall exception (prompt, or elevation to add a rule).
   It also makes a session startable from the LAN side, which needs a new authorisation path on the
   PC. Highest risk to USB, no user benefit that compensates.
2. **PIN + HMAC is insecure** against an active attacker: whichever side sends its proof first hands a
   MITM a 10^6 offline search (section 7). A security design that fails against the threat it is for.
3. **Copying the controller** duplicates 1,400 lines of the most carefully tested code; every future
   recovery fix would have to be made twice. The only difference wireless needs is a `Stream`.
4. **Own mDNS** contends with the Windows DNS client service for port 5353 and adds a parser to
   maintain; the OS already has DNS-SD.
5. **A second connection for input** solves a problem the full-duplex connection does not have
   (section 11), and doubles connect/teardown/authentication work and failure modes.
6. **Mouse-only** is not touch: no multi-touch, no pan-to-scroll, no press-and-hold in touch-aware
   apps. The synthetic pointer API gives native semantics in user mode; mouse stays as a fallback.
7. **Long-press-to-send** makes every tap ambiguous and adds latency to every touch; it is a UI that
   fights the product's purpose.
8. **Testing was underspecified:** no cross-implementation vectors for the new wire formats, no
   TLS loopback tests, no lifecycle tests for stuck contacts, no USB regression gate.
9. **Missing:** input off by default; release of contacts on every end path; host-side refresh of
   held contacts (Windows cancels contacts that are not refreshed); the encoder's letterbox in the
   mapping; Back gestures ending sessions; key storage; revocation.

## 15. Revised plan (implemented in Phases 10 and 11)

**Phase 10**

1. Baseline: run both suites (done: Windows 878/856 pass/1 known flake/21 skipped; Android 268/268).
2. Host `Link` over `Stream`; `IHostTransport.SecureAsync(Socket)` with a pass-through default for
   USB; all existing tests unchanged and passing (USB regression gate).
3. Identity, trust stores and the pairing exchange as pure, tested code on both sides; byte-exact
   shared vectors for the SAS in `tests/pairing-vectors/`.
4. Android: `TlsHostListener` (mutual TLS 1.3, ALPN, pin check, bounded handshakes), `CompositeHostListener`
   (USB local socket + Wi-Fi), `NsdAdvertiser`, pairing handler; the existing link unchanged above it.
5. Windows: `WirelessTransport` (resolve → TLS with pinning), DNS-SD browser in `ExtendSpace.Media`
   (Windows-only adapters), pairing client, routed transport chosen per session.
6. UI: Wireless enabled; device card lists Wi-Fi tablets with Pair/Forget; tablet Wi-Fi switch, Pair
   with a PC, paired PCs list.
7. Tests: TLS loopback on both, pairing state machines, stores, discovery merging, routing; run the
   full suites after each step.

**Phase 11**

1. Protocol: `stream_config.input` and the input channel's `touch` frame; vectors shared by both suites.
2. Android: `TouchCapture` (pure: MotionEvent-like samples → frames, cancel-all) and its wiring on the
   session surface; controls handle; Back behaviour.
3. Windows: `InputMapper` (pure mapping through the letterbox to the monitor), `InputDispatcher`
   (validation, lifecycle, release, hold refresh), `MouseGestureInterpreter` (pure), and the two
   injectors in `ExtendSpace.Media`; the setting on Home.
4. Tests: mapping tables, lifecycle cases (disconnect during touch, end during touch, mode off during
   touch), stale-connection rejection, mutation checks.

## 16. Phase 10 acceptance criteria

| # | Criterion | How shown |
| --- | --- | --- |
| A10.1 | USB Mirror behaviour unchanged | All pre-existing tests pass; no protocol change for USB |
| A10.2 | Host `Link` runs over a plain or TLS stream | Controller tests on both |
| A10.3 | Mutual TLS 1.3 with pinning; unpaired peer refused before session messages | TLS loopback tests both sides |
| A10.4 | Pairing: same SAS on both sides from the shared vectors; mismatch, timeout, cancel and no-pairing-window refused | Unit tests + vectors |
| A10.5 | Trust stored, listed, forgotten; forgotten peer refused | Store and listener tests |
| A10.6 | Discovery: advertise while waiting with Wi-Fi on; browse, merge, drop stale | Unit tests; Windows browse PC-verified against a local advertisement |
| A10.7 | Wireless session reaches Streaming with the same handshake | Loopback TLS end-to-end test on the host; hardware W1 |
| A10.8 | Debug and Release builds of both apps; lint | Builds |
| A10.9 | Hardware: W1–W8 in the beta acceptance document | **Owner-run; pending unless run** |

## 17. Phase 11 acceptance criteria

| # | Criterion | How shown |
| --- | --- | --- |
| A11.1 | Input sent only when the host offers it; old hosts unaffected | Protocol tests both sides |
| A11.2 | Mapping correct through both letterboxes, any monitor origin, DPI-independent pixels | Deterministic tables |
| A11.3 | Down/move/up/cancel sequences valid for Windows (UP repeats last position; full frames) | Dispatcher tests against a recording injector |
| A11.4 | Every end path releases contacts and stops input: connection lost, stream ended, session ended, input turned off, stale connection | Lifecycle tests |
| A11.5 | Held contacts refreshed every 100 ms | Dispatcher test with a manual clock |
| A11.6 | Mouse mode: tap, drag, press-and-hold right-click, two-finger scroll | Interpreter tests |
| A11.7 | Android: frames from touch, cancel-all on lifecycle; controls reachable; Back safe | JVM tests |
| A11.8 | Hardware: T1–T8 (tap, drag, scroll, right-click, accuracy, USB, Wi-Fi, disconnect during touch) | **Owner-run; pending unless run** |

## 18. Risks and unresolved questions

| # | Risk / question | Mitigation |
| --- | --- | --- |
| Q1 | AndroidKeyStore keys with Conscrypt as a TLS **server** on all vendors | Identity behind an interface; device check W1; fallback: software key wrapped by a Keystore AES key |
| Q2 | SChannel accepting the Android Keystore's self-signed certificate (no extensions) | Validation is by pin in the callback; device check W1 |
| Q3 | Windows DNS-SD watcher behaviour unpackaged | PC-verified with a local advertisement; manual address fallback |
| Q4 | Wi-Fi throughput and stalls at 12 Mbit/s | Hardware W5; lower bitrate policy for Wi-Fi if needed |
| Q5 | Synthetic touch on PCs without a touch digitiser | Mouse mode fallback; hardware T-checks |
| Q6 | Elevated windows ignore injected input (UIPI) | Documented limitation |
| Q7 | The pairing composition has had no external review | ES-120 |
| Q8 | Networks that block multicast or isolate clients | Manual address; documented |

## 19. Sources

Projects: <https://github.com/hpixsel/TabletMonitor> (README, docs/PROTOCOL.md, folder listings);
<https://github.com/FitzVB/FlexDisplay> (README, How_this_app_works.md, Cargo.toml);
<https://github.com/TristanSibug/SecondMonitor> (README, listings);
<https://github.com/efekurucay/anydisplay> (README, docs/PROTOCOL.md, windows/README.md);
<https://github.com/YuioDP/TabletMonitor> (README);
<https://github.com/VirtualDrivers/Virtual-Display-Driver> (README).
Microsoft: CreateSyntheticPointerDevice, InjectSyntheticPointerInput, InjectTouchInput,
InitializeTouchInjection, POINTER_INFO, POINTER_TOUCH_INFO, POINTER_TYPE_INFO
(learn.microsoft.com/windows/win32/api/winuser/…); Indirect display driver model overview;
Attestation signing; Driver signing offerings; IddCx reference.
Pairing prior art: apple/HomeKitADK Documentation/crypto.md (SRP-6a, 3072-bit);
AOSP `packages/modules/adb/pairing_auth/pairing_auth.cpp` (SPAKE2 with the 6-digit code, AES-128-GCM).
Local: `bcprov-jdk18on-1.85.2.jar` listing (contains `crypto/agreement/srp/SRP6Client` etc.).
Searches for PadDisplay, SecondScrn, DisplayMaster, FOLD, ezdisplay: no matching project.
