# ExtendSpace issue ledger

The single list of known defects, risks, gaps and deferred features. One row per issue; IDs are
never reused. Severity: **Critical** (data loss, crash or a broken core flow with no workaround),
**High** (blocks the friends beta), **Medium**, **Low**. "Beta" says whether it blocks the beta gate
in [ROADMAP.md](ROADMAP.md). Decisions are in
[docs/architecture/decision-log.md](docs/architecture/decision-log.md); the V-numbered device tests
in [architecture section 18.12](docs/architecture/production-session-architecture.md#1812-device-acceptance-procedure).

Last updated 2026-11-23, end of the CachyOS native validation mission. Hardware procedures H1–H13 referred to below
are in [docs/quality/beta-acceptance.md](docs/quality/beta-acceptance.md).

## CachyOS native validation mission (2026-11-23)

Native CachyOS on the external SSD, **Hyprland 0.57.2 on Wayland** (not KDE), real Pad 7 over USB and
Wi-Fi. Environment and results: [platform-acceptance.md, Phase A](docs/quality/platform-acceptance.md#phase-a-cachyos--run-2026-11-23-native-real-pad-7).
P-levels: P0 security/data loss, P1 major function broken, P2 significant usability/reliability, P3 minor.

| ID | Title | P | Root cause | Fix | Retest |
| --- | --- | --- | --- | --- | --- |
| ES-158 | ~~Extend unavailable on Hyprland ("wlroots: run swaymsg create_output")~~ | P1 | Linux Extend relied on the portal's VIRTUAL source; xdph advertises it but only shows its usual picker | D105: Hyprland headless output `EXTENDSPACE-1`, tablet-sized at 60 Hz, captured as a monitor through the portal, removed after | **PASS** on hardware: windows moved onto it, shown on the tablet |
| ES-159 | ~~No mouse input on Hyprland~~ | P1 | Input used only the RemoteDesktop portal, which xdph lacks | D104: the compositor's `zwlr_virtual_pointer_v1` (pure C# Wayland client, bound to the streamed output; no uinput, no root) | **PASS** USB and Wi-Fi (284 and 467 frames) |
| ES-160 | ~~Input on asks for a RemoteDesktop session the portal does not have, failing the whole connection~~ | P1 | RemoteDesktop requested whenever input was on | Requested only where the portal offers it (probed at start) | Hardware: sessions with Mouse start |
| ES-161 | ~~A failed acquisition leaves the Extend output (ghost display) until the app exits~~ | P1 | Capture released only after acquisition succeeded | `GstVideoPipeline` releases what it made on any acquisition failure; leftovers removed at start | Live: removed 5 ms after the failure |
| ES-162 | ~~First Extend after a start is 1920 × 1080, not tablet-shaped~~ | P2 | The display is made before the handshake tells the tablet's shape | Last tablet's screen remembered (`last-tablet.json`) | Hardware: 1616 × 1080 at 60 Hz |
| ES-163 | ~~App exits without a word in the log; host not disposed on window close~~ | P2 | No exit/crash logging; disposal only on Avalonia `ShutdownRequested` | Logs crash, unobserved task failure and exit; disposal on `Exit` | Seen: "shutdown requested / exited (code 0)" |
| ES-164 | ~~A second instance removes the first one's Extend display~~ | P1 | No single-instance guard; leftover cleanup at start | Per-user `flock` lock; second start refused (exit 2, logged) | Installed package: refused |
| ES-165 | ~~High latency (owner: "latency is very high")~~ | P1 | `vah264enc` holds about 67 ms (4 frames) when fed from PipeWire (2–3 ms from a test source; cause inside the VA encoder not found) | Encoders ordered by measured latency: NVENC, x264, VA-API; `EXTENDSPACE_ENCODER` override | Tracer p95 537 ms → 5.5 ms; owner confirmed |
| ES-166 | ~~Latency spikes up to 0.5 s on a mostly still screen~~ | P1 | `videorate` duplicating holds each picture until the next (keepalive 500 ms) | `videorate drop-only` | Tracer; owner confirmed still screen fine |
| ES-167 | ~~Main display's restore token never used; wrong output silently mirrored~~ | P2 | Main display is requested without a name; portal output not checked | Main resolved to its output name on Hyprland; per-output tokens; a different output from the dialog refused (D68) | Lab: "restored without the dialog" |
| ES-168 | ~~Two-finger scroll jumps in 48 px notches~~ | P2 | Core quantised scrolling to wheel notches | D107: optional `IPreciseScrollInjector`; Wayland finger-axis / portal smooth axis; Windows unchanged | Owner confirmed smooth |
| ES-169 | ~~Touch offered on Hyprland/X11 where it cannot work~~ | P2 | Fixed input list | D106: `IInputAvailability`; Touch shows why and cannot be chosen | UI on hardware |
| ES-170 | ~~Linux package cannot be built on Linux; false "ximagesrc missing" on Wayland; no icon~~ | P2 | PowerShell + Windows `tar.exe`; check ignored the session | `build-package.sh`; session-aware check; Arch/CachyOS line; SVG icon | Build, install, upgrade, uninstall, purge |
| ES-171 | ~~Declining pairing on the PC can reset the connection before the tablet reads "declined"~~ | P3 | Socket closed with the tablet's answer unread → RST on Linux | Graceful TLS close on decline | Core test, 3 runs |
| ES-172 | ~~Diagnostics labels run under their values~~ | P3 | 220 px column | Wider, wrapping label column | Visual |
| ES-173 | VA-API encoder adds about 67 ms only with PipeWire input | Medium | Open | Avoided by encoder order (ES-165); investigate upstream | Tracer |
| ES-174 | `hyprland-share-picker` crashed twice (SIGABRT, SIGSEGV) while picking | Low | Third party (xdph) | Per-output restore tokens avoid the picker after the first time | coredumpctl |
| ES-175 | Mode, input and transport choices are not remembered across restarts | Low | Not implemented | — | — |
| ES-176 | ExtendSpace's window overlapped the terminal in Hyprland's tiling (XWayland window) | Low | Unclear (Avalonia over XWayland in dwindle) | Workaround: move it to another workspace | Observed |
| ES-177 | `HostConnectionControllerTests` overflow/keyframe test fails now and then under full-suite load on Linux | Low | Test timing assumes the writer is still blocked | — (6/6 alone) | Pre-existing |
| ES-178 | A second start only prints to stderr; it does not raise the running window | Low | Not implemented | — | — |
| ES-179 | Not run natively: manual IP, Forget/revocation, declining the dialog, killed process mid-session, sleep/wake, tablet restart, network change, 10-minute soak | Medium | Time/owner | Owner procedure (Phase A rows) | NOT RUN |

## Windows completion and Linux host mission (2026-11-23)

Plans and critiques: [windows-completion.md](docs/research/windows-completion.md),
[linux-host-research.md](docs/research/linux-host-research.md); acceptance:
[platform-acceptance.md](docs/quality/platform-acceptance.md).

| ID | Title | Sev. | Category | State | Verification |
| --- | --- | --- | --- | --- | --- |
| ES-135 | ~~Missed mouse click~~ | — | Bug | **Probable cause fixed** (D93): a still press lifted at 500–600 ms did nothing, and arrival-time jitter could put a tap there. The injector was measured exact (32,333 points, 40/40 clicks); the original single event cannot be re-run | `HostInputMouseTests` (+5), gated accuracy test |
| ES-140 | ~~800 × 600 at 30 Hz default~~ | — | UX | **Resolved for 16:9** (D92): "Use 1920 × 1080 at 60 Hz" on the Extend card; 60 fps measured. 3:2 needs a custom mode in VDD Control (suggested size shown) | Hardware 2026-11-23 |
| ES-141 | Persistent virtual monitor | Medium | UX | **Mitigated** (D94): "Bring windows back" and a Display settings link; persistence stays the driver's | Unit tests; button not pressed on hardware |
| ES-145 | ~~30 Hz Extend~~ | — | Performance | **Resolved**: 60 Hz modes exist at every size; 60 fps measured | Hardware |
| ES-146 | `C:\VirtualDisplayDriver` is writable by every signed-in user, so any unelevated program can change the driver's configuration (EDID, modes) | Medium | Security (third-party) | ExtendSpace never writes it (D92); documented for users; report upstream (owner) | `icacls` |
| ES-147 | The virtual display disappeared once during the mission; the owner reconnected it; cause unknown | Medium | Third-party stability (ES-143) | Recorded; ExtendSpace recovered its reading | Observed |
| ES-150 | Linux host on GNOME and KDE (portal Mirror, Extend VIRTUAL, touch, pointer) is untested on a real desktop | **High** | Hardware acceptance | Implemented (D98, D100); owner procedure in platform-acceptance | OWNER |
| ES-151 | Linux identity at rest is protected only by folder permissions (0700), not encrypted | Medium | Security | Documented; Secret Service (libsecret) next | — |
| ES-152 | X11 Extend (RandR monitor on an enlarged framebuffer) may not be treated as a monitor by some window managers; unverified (Xvfb cannot grow) | Medium | Experimental | Labelled experimental in the UI | OWNER |
| ES-153 | Linux packaging is a tarball only; no Flatpak/deb/rpm | Low | Packaging | D102 | — |
| ES-154 | Linux USB (adb) not run (no USB in WSL) | Medium | Hardware acceptance | Same Core transport as Windows | OWNER |
| ES-155 | Touch chosen on an X11 session is refused (no touch injection without uinput); the input list still offers Touch | Low | UX | Explained in the input note and About; hide the option per platform later | — |
| ES-156 | The lab tablet now trusts a test Linux host (the WSL lab machine) whose identity lived in WSL's /tmp | Low | Housekeeping | Remove it on the tablet under Paired PCs | — |
| ES-157 | Linux hardware encoders (VA-API, NVENC) are chosen when present but were not run (the lab had x264 only) | Medium | Hardware acceptance | Fallback to x264 is automatic | OWNER |

## Extend mode mission (2026-11-23)

Extend implemented in code (D87–D91) and proven on hardware for creation-by-driver, identification,
capture, USB, Wi-Fi and touch ([acceptance](docs/quality/extend-mode-beta-acceptance.md)).

| ID | Title | Sev. | Category | State | Verification |
| --- | --- | --- | --- | --- | --- |
| ES-091 | Extend mode | — | Feature | **Foundation implemented** with the user-installed Virtual Display Driver (D87–D91) | E-H1, E-H2, E-H4, E-H5 PASS on hardware |
| ES-140 | VDD's default virtual monitor is 800 × 600 at 30 Hz, and its modes are 16:9 (plus 4:3); a tablet-shaped mode (3:2 for the Pad 7) needs VDD's own settings file, which ExtendSpace does not write (D88) | **High** (UX) | Limitation | The user picks a larger mode in Display settings; ExtendSpace streams whatever shape it is (D89). Guided custom modes are the next milestone | E-H1 |
| ES-141 | The virtual monitor stays while VDD is enabled, with no tablet connected: the pointer and windows can wander onto an invisible screen | Medium | UX (D91) | Documented; disable it in Display settings or VDD Control; a keepalive provider (Parsec VDD) or our own driver would remove it with the session | Seen on hardware |
| ES-142 | VDD reports output technology HDMI, not INDIRECT_VIRTUAL; identification rests on its EDID (MTT/0x1337) | Low | Finding | The detector checks EDID first (tested); a VDD build with another EDID would read as "no virtual display" | E-H1 |
| ES-143 | VDD stability on current Windows builds (upstream #468, #474, #490, #531) | High (risk) | Third-party | User-installed and never reloaded by ExtendSpace; recovery documented (uninstall VDD; Safe Mode if the screen is black) | Watch |
| ES-144 | Not run on hardware: changing the virtual display's resolution during a session, Mouse mode on it, quitting ExtendSpace mid-session, disabling the display, uninstalling VDD | Medium | Hardware acceptance | E-H3 (part), E-H5 (mouse), E-H6, E-H7 | Owner-run |
| ES-145 | Extend sessions run at the virtual monitor's refresh (30 Hz with VDD's defaults) | Medium | Performance | Choose a 60 Hz mode in Display settings | E-H4 |

## Phase 9–11 mission (2026-11-22/23): wireless, pairing, touch

Implemented in code (D74–D85); hardware acceptance is the open items below.

| ID | Title | Sev. | Category | Resolution / state | Verification |
| --- | --- | --- | --- | --- | --- |
| ES-090 | Wi-Fi transport and pairing | — | Feature | **Implemented** (D74–D80); moved out of Deferred | TLS loopback tests both sides; cross-implementation pairing and session on one PC; **hardware: ES-121** |
| ES-092 | Input forwarding (touch) | — | Feature | **Touch implemented** (D81–D84); pen and keyboard stay deferred (ES-124) | Mapping, lifecycle, gesture and connection tests; a touch carried end to end on one PC; **hardware: ES-122** |
| ES-120 | The pairing ceremony (commit-reveal numeric comparison over TLS) is a composition of standard primitives that has had no external review | Medium | Security | Specified exactly in the transport architecture, section 5; vectors and breakage tests | Owner: get a review before a public release |
| ES-121 | Wi-Fi acceptance on hardware is partly done | **High** | Hardware acceptance | **W1 (pairing) and W2 (Mirror over Wi-Fi) PASS** on this PC and the Pad 7 on 2026-11-23; W3–W8 not run | `artifacts/phase10-11-20261123/`; owner-run for the rest |
| ES-122 | Touch acceptance on hardware is partly done | **High** | Hardware acceptance | Tablet touches reached Windows over Wi-Fi (on the wrong display before ES-132's fix, as the owner saw). After the fix the real touch injector hit the exact point on a test window, and a tablet tap went to the centre of the main monitor (inferred: the window there received it; not logged). T1–T8 not run | Owner-run |
| ES-123 | ~~AndroidKeyStore keys as a TLS server key under Conscrypt unproven~~ | — | Risk | **Resolved**: W1 paired over real Wi-Fi with the keystore key (SChannel client, Conscrypt server); the same code on both screens | W1, 2026-11-23 |
| ES-132 | **Touches landed on another display** (owner's report: on the laptop panel, not the mirrored monitor): synthetic touch takes points relative to the virtual screen's top-left, not the main display's origin | **Critical** (for touch) | Bug (found on hardware) | **Fixed**: `VirtualScreen.ToTouchSpace` subtracts the virtual screen's origin | `VirtualScreenTests` (this PC's layout); real-injector tap arrived at the exact point |
| ES-133 | The mouse injector and monitor locator used scaled coordinates in a process that is not per-monitor DPI aware (clicks 1.25 × off at 125 %) | High | Bug (found on hardware) | **Fixed**: they run in physical pixels on their own thread whatever the caller | Cursor landed exactly; click received |
| ES-134 | Discovery of the tablet is unreliable on this network: in one run the tablet answered no mDNS query (not even for its system services) though Android had announced it; in another Windows found it at once | Medium | Network/platform | Add-by-address on the PC, the tablet shows its address, paired tablets offered at their last address (ES-126 resolved) | Hardware, 2026-11-23 |
| ES-135 | One injected mouse click at (1400,800) was not received by the test window twice, while clicks elsewhere were | Low | Unexplained | Recorded; T3 will show whether it matters | — |
| ES-124 | Pen pressure, tilt, hover; keyboard | Low | Deferred feature | Wire format leaves room (reserved field); Windows' synthetic pointer API supports a pen | — |
| ES-125 | `UsbTransportTests.A_port_another_program_took…` failed once at the mission's baseline: the OS can hand back the refused port, which was never bound | Low | Test flake (pre-existing) | Passes 5/5 alone and in later full runs; fix by excluding the refused port explicitly | Seen once |
| ES-126 | ~~No on-screen "connect by address"~~ | — | UX gap | **Resolved** after ES-134: Add by address on the PC; the tablet shows its address; a paired tablet is offered at its last address | Directory and view-model tests; used on hardware |
| ES-127 | The input choice is not remembered across launches (Off each start), and turning it on mid-session applies from the next connection | Low | UX | Deliberate for now (safer default); documented on screen | — |
| ES-128 | Wi-Fi uses the USB stream policy (1080p60, 12 Mbit/s); behaviour on a weak network is unmeasured | Medium | Performance | Queue drops to a keyframe (D64) keeps the picture current; measure in W5, then decide on a Wi-Fi bitrate | W5 |
| ES-129 | While waiting, the tablet's DNS-SD record shows its name and a 16-digit key prefix on the LAN | Low | Privacy | Accepted and documented (an active prober could read the certificate anyway) | — |
| ES-130 | Windows that run elevated, and the secure desktop, ignore injected input (UIPI) | Low | Limitation | Documented; the host stays unelevated by design | — |
| ES-131 | ~~The Windows DNS-SD watcher has not been run against a real advertisement~~ | — | Verification gap | **Resolved**: the gated test passed on this PC, and the watcher found the tablet in W1 | 2026-11-23 |

## Resolved in the Phase 5–8 mission

| ID | Title | Sev. | Category | Resolution | Verification |
| --- | --- | --- | --- | --- | --- |
| ES-040 | The Android screen fitted every stream at 16:9 | Low | UX | D69: fitted to the stream's own size | `PictureFitTest` (7), controller test; **hardware: H2** |
| ES-041 | Placeholder pages (Devices, Quality, Display) in the Windows navigation | Medium | UX | Removed from navigation; display choice moved to Home (D68) | UI Automation read of the packaged app: Home, Diagnostics, Settings only |
| ES-042 | No stream figures on the Windows Diagnostics page | Low | Observability | D67: per-second table, each figure named where it is measured | `DisplayAndHealthTests`; packaged app shows the table |
| ES-044 | Mirror showed the main display only | Medium | Feature | D68: display choice by device name | `DisplayAndHealthTests`, `MonitorSelectionTests`; **hardware: H9** |
| ES-100 | Closing the Windows app during a session left the tablet "Reconnecting" for 30 s and the adb forward in place | High | Bug (lifecycle) | D70: the window stops the session (bounded 11 s) before closing | `SessionShutdownAndAdviceTests`; mutation caught; **hardware: H2** |
| ES-110 | The published Windows app crashed at start (0xC000027B): `dotnet publish` of the unpackaged WinUI app left out `ExtendSpace.pri` and `*.xbf`; a first fix copied a stale build's files and still crashed | Critical (for the package) | Packaging | `Build-Package.ps1` rebuilds clean, copies only this run's files, refuses a package without them | Isolated install, launch, clean exit (0), upgrade, uninstall; UI read of the packaged app. The two broken builds are kept in `artifacts/release/superseded-*` |
| ES-111 | Failures a user can fix showed the component's technical sentence | Medium | UX | Plain advice for no app listening, adb missing, unauthorised, not attached, several devices, device not answering | `SessionShutdownAndAdviceTests` (8 cases) |

## Resolved in the Phase 1–4 mission

| ID | Title | Sev. | Category | Evidence | Resolution | Verification |
| --- | --- | --- | --- | --- | --- | --- |
| ES-001 | A recovery after a short stall took ~400 ms: the host's queue-full request spent the keyframe gate's window on a keyframe that a second overflow then discarded unsent (D63) | High | Reliability | V19l, `artifacts/d63-20261122/` | D64: the writer asks once it can send; overflow keeps a queued keyframe | Queue replay of D63 round 1, loopback regression test, 5 mutations caught; **device rerun pending (ES-031)** |
| ES-002 | Every frame refused while awaiting a keyframe called the gate, so at every stream start the client's `startup` request was deferred and made a redundant keyframe | Low | Bug | Architecture section 20.13 | D64: refused frames ask nothing | `HostConnectionControllerTests` (log test rewritten) |
| ES-003 | An access unit too large to send was dropped without a sequence gap and without waiting for a keyframe, so its dependants would be decoded against a missing picture | Medium | Bug (latent; needs an access unit > 8 MiB) | Read from `OfferVideo` | D64: it uses up its sequence number and the queue waits for a keyframe | New loopback test; mutation caught |
| ES-004 | The keyframe gate was not reset per connection: a request deferred on one connection was released on the next | Low | Reliability | Architecture section 20.13 | D64: reset at each stream start | `KeyframeRequestGateTests` |
| ES-005 | Windows Connect was disabled for every selection and there was no way to choose a device | Critical (for v1) | Missing feature | `SessionCapabilities.CurrentBuild` | D65: USB Mirror enabled; device list and choice | 10 view-model tests, 4 transport tests; **UI on hardware pending (ES-030)** |
| ES-006 | Android had no release screen to start a session or show the picture; only a debug harness | Critical (for v1) | Missing feature | `sessiontest/` only | D66: `SessionActivity`, Wait for PC | `SessionOverlayTest`, builds, release APK checked; **screen on hardware pending (ES-030)** |
| ES-007 | Settings/About on both apps said nothing works | Low | UX | Strings | Updated to what the build does | Read |

## Open: hardware acceptance (owner-run; the code is done)

Each needs the PC's screen, the tablet, or the owner's hands. Commands are given where they exist.

| ID | Title | Sev. | Beta | What to run, and what passes |
| --- | --- | --- | --- | --- |
| ES-030 | The Phase 3 UI flow end to end on hardware, **now on build 0.2.0-beta.1: procedures H1, H2, H3** | High | Yes | Install the debug or release APK (`adb install -r app/build/outputs/apk/debug/app-debug.apk`), run `dotnet run --project src/ExtendSpace.Host` in `apps/windows`. On the tablet tap **Wait for PC**; on Windows choose the tablet and **Connect**. Pass: picture within ~1 s; Back on the tablet ends both sides; **Disconnect** on Windows shows "Session ended" on the tablet; rotate, Home and return, screen off/on resume; pull and replug the cable within 30 s recovers; repeat connect/disconnect 10 times; close either app mid-stream and the other side ends cleanly; with no device / adb missing / unauthorised the Windows card says so |
| ES-031 | D64 on hardware: V19l with the 300 ms stall (**procedure H5**) | High | Yes | `ExtendSpace.Media.Tests.exe -method "ExtendSpace.Media.Tests.Device.ProductionVideoAcceptanceTests.V19l_a_short_stall_of_the_app_is_recovered_from_while_streaming"` (in `apps/windows/tests/ExtendSpace.Media.Tests/bin/Debug/net10.0-windows10.0.26100.0`) with `EXTENDSPACE_DEVICE_TESTS=1` and `EXTENDSPACE_STALL_MILLIS=300`, the harness open on the tablet and the moving pattern on screen, exactly as D63 (memory notes and section 20.12). Pass: no `gate_passed` between `recovery_owed` and `recovery_writer_free`; client "keyframe arrived … after the last request" in the order of tens of ms in both rounds; no new missing frames after recovery |
| ES-032 | V17: two devices attached, the chosen one used | Medium | Yes | Emulator and tablet both attached; choose each on the Windows card in turn |
| ES-033 | V18: an unauthorised device | Medium | Yes | Revoke USB debugging authorisations on the tablet; the Windows card shows "Allow USB debugging…"; Connect stays disabled; accept and it becomes ready |
| ES-034 | V6 remainder: screen off for 2 and 10 minutes, another app in front | Medium | Yes | Expected by design: the session ends after the 30 s grace (D6) and the tablet shows "Session ended" |
| ES-035 | V7 remainder: stop with the peer unplugged; client-side stop times | Medium | Yes | |
| ES-036 | V11 remainder: the host frozen | Medium | Yes | |
| ES-037 | V14 remainder: cancelling a blocked connect or accept on the device | Low | No | Covered deterministically by socket tests |
| ES-038 | V12: glass-to-glass latency on the production path | Low | No | The experiment measured about 46 ms |
| ES-039 | V1 remainder: cold start and the software encoder | Low | No | |

## Open: reliability risks

| ID | Title | Sev. | Beta | Evidence | Plan |
| --- | --- | --- | --- | --- | --- |
| ES-050 | Capture can intermittently deliver almost no frames while a browser plays video full screen (the picture freezes) | High | **Accepted for the beta** as a known issue | Experiment 01 Phase 2.4; section 19.7 | **Partly addressed (D67):** the host now shows "No new picture from Windows for …" and the Diagnostics table. Telling starvation from a still screen (layer 2) still needs the experiment in section 8.4, which needs the owner (elevated trace). Listed in the testers' known issues; H10 collects evidence |
| ES-051 | A `MediaCodec` call that really hangs has never been reproduced; only the scripted platform holds one open | Low | No | Sections 20.9–20.10 | Keep the D58/D59 tests; observe in Phase 7 |
| ES-052 | Whether other vendors' codecs tolerate their surface being destroyed during `configure` (D59) | Medium | Yes | Section 20.10 | Phase 7 |
| ES-053 | Only one decoder and device measured (Pad 7, `c2.qti.avc.decoder`); D60's 1 s allowance and D61's supersede are tuned on it | High | Yes | Sections 20.11–20.12 | Phase 7 |
| ES-054 | Only one PC, GPU and hardware encoder measured | High | Yes | Section 20.5 | Phase 7 |
| ES-055 | D51: a wireless-debugging adb device is auto-chosen when it is the only usable one | Low | No | Section 20.6 | Shown in the list since D65; decide in Phase 7 |
| ES-056 | A client that notices a backlog only after the post-stall keyframe was passed still waits for the rest of the 500 ms window | Low | No | Protocol section 10; D64 residual | Accept, or request ids (protocol change) later |
| ES-057 | A monitor size or resolution change ends the session (D17) | Medium | No | L7 | Phase 5 |
| ES-058 | No foreground service: Home or screen-off longer than 30 s ends the session (D5, D6) | Medium | No | Section 20.3 (45 s run) | Phase 6 decision |
| ES-059 | When one teardown step overruns, the steps after it are recorded as overran even if they return at once | Low | No | Section 20.9 | Diagnostics wording; Phase 6 |

## Open: UX gaps

| ID | Title | Sev. | Beta | Plan |
| --- | --- | --- | --- | --- |
| ES-043 | No tray or windowless streaming on Windows (D10) | Low | No | Phase 6 |
| ES-045 | The WinUI page and the Android Compose screens have no UI automation; their logic is tested through the view model and `sessionOverlay` only. One smoke launch of the Windows host read the device card through UI Automation (1 device found, Connect enabled) | Medium | No | Covered by ES-030 by hand; consider UI tests in Phase 6 |

## Open: release and engineering

| ID | Title | Sev. | Beta | Plan |
| --- | --- | --- | --- | --- |
| ES-060 | The Windows package is unsigned (SmartScreen warns) | Medium (was High: no installer) | No | **Installer done (D72):** ZIP with per-user installer, validated in scratch folders. Signing needs a certificate: owner action ([release process](docs/release/release-process.md)) |
| ES-061 | The Android release APK is unsigned and so cannot be installed | High | Yes | **Configuration done (D72)** and proven with a throwaway key; the owner creates the release key and `keystore.properties`, then runs `Build-Apk.ps1` (exact steps in the release process) |
| ES-062 | No CI for either app | Medium | No (was Yes) | Not added: no CI service is configured and choosing one is the owner's; the commands are in the release process |
| ES-063 | The user must install Android Platform-Tools (adb) themselves | Medium (was High) | No | **Decided (D73):** external prerequisite, detected with its version, explained in the app and the install guide. Bundling is an owner decision (licence) |
| ES-064 | Lint reports newer AGP, Compose BOM and lifecycle versions (online notices) | Low | No | Upgrade deliberately, not as part of a fix |
| ES-065 | The experiments still hold copies of production code (D42, D52) | Low | No | After the beta; they stay the measured baseline until the production path is accepted on hardware |
| ES-101 | `PlaceholderPage` is no longer used by the navigation | Low | No | Delete when convenient (left in place: deleting source is the owner's call) |
| ES-102 | The release APK is not minified (23 MB) | Low | No | Enable R8 only together with a device run of the minified build |
| ES-103 | The stream-health thresholds (500 ms, 3 s, 2 s, 2.5 s) are design values, not yet seen on hardware (D8) | Medium | No | Check during H4/H5/H10; adjust from evidence |
| ES-104 | An encoder stall is reported, not made terminal (deviation from section 8.3, D67) | Low | No | Revisit once a stall has been observed |
| ES-105 | The Windows installer adds no Apps & Features entry; uninstall is from the Start menu | Low | No | Accepted for the beta (keeps the installer registry-free) |
| ES-106 | No physical test of build 0.2.0-beta.1 exists | High | Yes | Run H1–H13 ([beta acceptance](docs/quality/beta-acceptance.md)); ES-030 and ES-031 first |

## Deferred features (not v1)

| ID | Feature | Why deferred |
| --- | --- | --- |
| ES-090 | ~~Wi-Fi transport and pairing~~ | Implemented in the Phase 9–11 mission (see above) |
| ES-091 | ~~Extend mode~~: foundation implemented with the community driver (see the Extend section above); an own IddCx driver stays deferred | Own driver: EV certificate and Hardware Developer Program (owner decision) |
| ES-092 | Input forwarding: pen and keyboard (touch implemented, see above) | Not in v1 |
| ES-093 | Audio | Not in v1 |
| ES-094 | Several Android devices at once | One session per host by design (D1) |
| ES-095 | Stale-picture notice to the client (D9), request ids on keyframe requests | Protocol changes; no evidence yet that they are needed |
