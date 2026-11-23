# Beta acceptance and the Beta Gate (Phase 8)

Status on 2026-11-22, build **0.2.0-beta.1** (Windows ZIP and Android APK in `artifacts/release/`).

Each row has three results, because they are different evidence:

- **Auto:** automated tests of this build (unit, loopback-socket, scripted platform).
- **HW earlier:** a physical run on the Xiaomi Pad 7 and the development PC during stages 4–4.1 and
  D59–D63 (October–November 2026), through the debug harness and gated host tests, on an earlier build
  of the same session path. It shows the path works; it is not a run of this build or its screens.
- **HW 0.2.0-beta.1:** a physical run of this build. **None has been made yet**; every such cell is
  NOT TESTED.

PASS / FAIL / NOT TESTED / BLOCKED / N/A. Nothing below is inferred.

## Acceptance matrix

### Connection

| # | Case | Auto | HW earlier | HW 0.2.0-beta.1 | Procedure |
| --- | --- | --- | --- | --- | --- |
| C1 | Device detected and listed | PASS | PASS (V16) | NOT TESTED (the packaged app listed one device as Ready in a UI read; no session) | H1 |
| C2 | Single device chosen automatically | PASS | PASS (V2) | NOT TESTED | H1 |
| C3 | Several devices: none guessed, the chosen one used | PASS | BLOCKED (V17: one device only) | NOT TESTED | H6 |
| C4 | Unauthorised device: told what to do, Connect disabled | PASS | NOT TESTED (V18) | NOT TESTED | H7 |
| C5 | adb missing: told what to do | PASS | N/A | NOT TESTED | H8 |
| C6 | Connect and disconnect from Windows | PASS | PASS (V19a, V19b) | NOT TESTED | H1 |
| C7 | Connect repeated ten times | PASS | PASS (V19b, ten sessions) | NOT TESTED | H2 |
| C8 | Device removed while connecting or streaming | PASS | PASS (V16d, V19c, V19h) | NOT TESTED | H3 |
| C9 | Display choice, including a secondary display | PASS | N/A | NOT TESTED | H9 |
| C10 | App not open on the device: clear advice | PASS | NOT TESTED (the advice text is new) | NOT TESTED | H1 |

### Streaming

| # | Case | Auto | HW earlier | HW 0.2.0-beta.1 | Procedure |
| --- | --- | --- | --- | --- | --- |
| S1 | Moving picture, 1080p60 | PASS | PASS (V10, 60 min, none lost) | NOT TESTED | H1, H4 |
| S2 | Still desktop: picture kept, "No new picture… normal" | PASS | PASS (V3, V4 picture kept); message NOT TESTED | NOT TESTED | H1 |
| S3 | Fast movement | N/A | PASS (moving pattern) | NOT TESTED | H4 |
| S4 | Full-screen browser video | N/A | FAIL in the experiment (capture starvation, ES-050) | NOT TESTED | H10 |
| S5 | Short device freeze recovered (D64) | PASS | PASS before D64 (5–417 ms) | NOT TESTED | H5 |
| S6 | Decoder failure handled | PASS | NOT TESTED | NOT TESTED | — (needs a faulty decoder) |
| S7 | Rotation | PASS (fit) | PASS (V5, V19e) | NOT TESTED | H2 |
| S8 | Aspect: no stretching on tablet and phone shapes | PASS (PictureFitTest) | N/A | NOT TESTED | H2 |
| S9 | Resolution change on the PC during a session | PASS (ends with a reason) | NOT TESTED | NOT TESTED | H9 |
| S10 | Long session (60 min) | N/A | PASS (V10) | NOT TESTED | H4 |
| S11 | Health line and Diagnostics figures correct | PASS | N/A | NOT TESTED | H4 |

### Lifecycle

| # | Case | Auto | HW earlier | HW 0.2.0-beta.1 | Procedure |
| --- | --- | --- | --- | --- | --- |
| L1 | Back on the device ends both sides | PASS | PASS (section 20.9) | NOT TESTED | H2 |
| L2 | Home and return within 30 s | PASS | PASS (V19d) | NOT TESTED | H2 |
| L3 | Screen lock/off: short resumes, beyond 30 s ends | PASS | PARTLY (V6: 3 s, 15 s, 45 s) | NOT TESTED | H2 |
| L4 | Session ended by the PC: "Session ended", Wait again works | PASS | PASS (harness rewait) | NOT TESTED | H2 |
| L5 | Windows app closed during a session (D70) | PASS | N/A (new) | NOT TESTED | H2 |
| L6 | Windows sleep and wake during a session | N/A | NOT TESTED | NOT TESTED | H11 |
| L7 | Windows shut down during a session | N/A | NOT TESTED | NOT TESTED | H11 |
| L8 | Frozen app on the device | PASS | PASS (V19i) | NOT TESTED | H5 |
| L9 | Frozen host | N/A | NOT TESTED (V11) | NOT TESTED | H11 |

### Performance

| # | Case | Auto | HW earlier | HW 0.2.0-beta.1 | Procedure |
| --- | --- | --- | --- | --- | --- |
| P1 | Frame rate holds at 60 | N/A | PASS (V10) | NOT TESTED | H4 |
| P2 | Host CPU/GPU | N/A | NOT TESTED | NOT TESTED | H4 |
| P3 | Memory steady over 60 min | N/A | PASS (V10) | NOT TESTED | H4 |
| P4 | Latency | N/A | PASS (render 19–21 ms mean); glass-to-glass experiment only | NOT TESTED | H4 |
| P5 | Recovery time after a cable pull | N/A | PASS (3.3 s after replug, experiment; V19c) | NOT TESTED | H3 |
| P6 | Tablet battery and temperature | N/A | PARTLY (36–37 °C; battery not measured) | NOT TESTED | H4 |

### Compatibility and packaging

| # | Case | Auto | HW earlier | HW 0.2.0-beta.1 | Procedure |
| --- | --- | --- | --- | --- | --- |
| X1 | This PC (Windows 11, one GPU) | PASS | PASS | NOT TESTED (smoke launch only) | H1 |
| X2 | Xiaomi Pad 7 | PASS | PASS | NOT TESTED | H1 |
| X3 | Another Android vendor or a phone | PASS (fit) | NOT TESTED | NOT TESTED | H12 |
| X4 | Another PC/GPU | N/A | NOT TESTED | NOT TESTED | H12 |
| X5 | Display scaling 150 %/200 % | N/A | NOT TESTED | NOT TESTED | H9 |
| K1 | Windows ZIP: install, launch, upgrade, uninstall | PASS (scratch folders, 2026-11-22) | N/A | PASS (scratch folders, not a clean machine) | — |
| K2 | Windows ZIP on a clean machine or VM | N/A | N/A | NOT TESTED | H13 |
| K3 | Android APK installs | N/A | N/A | BLOCKED (unsigned: needs the owner's key, ES-061) | H13 |
| K4 | Release builds exclude debug code | PASS | N/A | PASS (dexdump; no experiment assemblies in the ZIP) | — |

## Owner-run hardware procedures

Common preconditions: the release key exists and `Build-Apk.ps1` produced a **signed** APK (or use
the debug APK, which also contains the debug screens and is fine for these runs); the Windows ZIP is
installed (`Install.cmd`); the tablet is unlocked with USB debugging authorised; screen-on timeout
long. Capture for every run: the PC Diagnostics "Stream" table (copy) and `adb -s <serial> logcat -d -v
threadtime -s ExtendSpaceSession > H<n>-device.txt`. Cleanup: Disconnect; if the tablet app is stuck,
`adb -s <serial> shell am force-stop com.rsxdevelops.extendspace`.

| ID | Steps | Pass | Fail if | Inconvenience |
| --- | --- | --- | --- | --- |
| H1 | Tablet: Wait for PC. PC: device Ready → display → Connect. Move windows for 30 s; leave the desktop still for 30 s; Disconnect | Picture < 2 s after Connect; Home shows "Streaming at ~60 fps" while moving and "No new picture… normal" while still; tablet "Session ended" after Disconnect | No picture, stretched picture, health line wrong, either side stuck | Takes over the tablet ~2 min |
| H2 | During a session: rotate twice; Home and back within 10 s; screen off 10 s and on; tap picture → End session; reconnect (Wait again + Connect) ten times; finally close the Windows app mid-stream | Picture back each time, not stretched; End and Back end both sides; ten sessions all stream; closing the PC app gives "Session ended" on the tablet within ~1 s and `adb forward --list` is empty | Any session left half-open, stretched picture, a stuck "Reconnecting" after the PC app closed | ~10 min |
| H3 | During a session pull the cable for 5 s and replug; then pull for 40 s | 5 s: Reconnecting then picture; 40 s: both sides end with a reason | Session never recovers or never ends | Needs hands |
| H4 | 60-minute 1080p60 session with moving content (the experiment's `pattern --fullscreen`, which covers the PC screen) ; record Task Manager CPU/GPU at 5 and 55 min, tablet battery % at start and end | ≥59 fps sent and shown throughout; no growth in latency; CPU/GPU noted | Drops, rising latency, crash | Covers the PC screen for an hour |
| H5 | **ES-031 (D64).** With the harness open on the tablet and the pattern running: `EXTENDSPACE_DEVICE_TESTS=1`, `EXTENDSPACE_STALL_MILLIS=300`, run `ExtendSpace.Media.Tests.exe -method "ExtendSpace.Media.Tests.Device.ProductionVideoAcceptanceTests.V19l_a_short_stall_of_the_app_is_recovered_from_while_streaming"` | Host log: `recovery_owed` then `recovery_writer_free action=request` then `gate_passed source=host_queue_full`, no `gate_passed` in between; tablet "keyframe arrived … after the last request" in tens of ms in both rounds | A round over ~100 ms with the old shape (a pass during the freeze) | Freezes the tablet app twice for 300 ms |
| H6 | Start the Android emulator beside the tablet; choose each in turn on the PC card | Each choice streams to that device only | The other device is used, or Connect works with none chosen | Needs the emulator |
| H7 | On the tablet: Developer options → Revoke USB debugging authorisations; replug | Card shows "Allow USB debugging…", Connect disabled; after accepting, Ready | Connect possible, or a technical error | Re-authorise afterwards |
| H8 | Temporarily rename the platform-tools folder (or run from an account without it) and open ExtendSpace | Card and Diagnostics say adb was not found, with the guide's advice | Crash or a blank list | Restore the folder |
| H9 | With two monitors: choose the secondary, Connect; during the session change the PC display's resolution; set scaling to 150 % and connect again | Secondary shown; resolution change ends with "The display could not be captured…"; at 150 % the picture is the whole display | Wrong monitor, crash, cropped picture | Needs a second monitor |
| H10 | Play a video full screen in a browser for 2 minutes during a session | Record whether the tablet picture freezes and what Home says (ES-050) | — (evidence for Phase 5 follow-up) | — |
| H11 | Sleep the PC during a session and wake it; shut down Windows during a session; freeze the host (suspend `ExtendSpace.exe` in Resource Monitor for 10 s) | Tablet ends or recovers with a clear state each time; no crash on wake | Tablet stuck forever, PC app crash | Interrupts work |
| H12 | Repeat H1 and H2 on another Android vendor's device and on another PC | As H1/H2 | As H1/H2 | Needs hardware |
| H13 | Clean Windows 11 VM or a second PC with no SDK: follow `docs/beta/install-windows.md` exactly; install the signed APK on a device by file manager | Works by the guide alone | Any step the guide does not cover | Needs a VM and the signed APK |

### Wi-Fi (Phase 10) and touch (Phase 11)

Preconditions: PC and tablet on the **same Wi-Fi network** (not a guest network that isolates
clients); a build of 0.2.0 with Phases 10–11 on both. Capture as above, plus the PC's log lines
starting `wireless`/`input` (DebugView or the debugger output). Until these run, Wi-Fi and touch are
**complete in code, hardware acceptance pending**.

| ID | Steps | Pass | Fail if | Inconvenience |
| --- | --- | --- | --- | --- |
| W1 | Tablet: turn on *Allow Wi-Fi connections*, tap *Pair with a PC*. PC: choose *Wireless*, select the tablet ("Not paired · ready to pair"), *Pair…*. Compare codes, choose Pair on both | Both show the same six digits; both say paired; the tablet lists the PC under *Paired PCs*; the PC lists the tablet "Paired" | Different codes, a crash, a TLS error (**this is the first run of AndroidKeyStore + Conscrypt as a TLS server; capture logcat in full**) | Hands on both devices |
| W2 | Tablet: *Wait for PC*. PC: select the paired tablet, Connect; move windows 30 s; Disconnect | Picture < 3 s after Connect; tablet status "Over Wi-Fi"; Home shows streaming at ~60 fps while moving | No picture; `authentication_failed`; stalls | — |
| W3 | Repeat W2 with the cable also attached and the PC set to USB | USB session as before (regression); Wi-Fi not used | Either route used wrongly | — |
| W4 | Mismatch: on the PC start pairing, and on the tablet choose Cancel; then open pairing on the tablet and on the PC choose Cancel | Nothing stored on either side; each says who cancelled | Either side stores the other | — |
| W5 | During a Wi-Fi session: walk to the edge of Wi-Fi range for 30 s; turn the tablet's Wi-Fi off for 5 s and on; then off for 40 s | Picture degrades but stays current (no replay of old frames); 5 s: Reconnecting then picture; 40 s: both end with a reason | Growing delay; never recovers; never ends | Needs moving around |
| W6 | Forget the tablet on the PC, then try to connect; pair again; then forget the PC on the tablet and try to connect | PC: Connect disabled ("Pair … first"); after forgetting on the tablet, the PC fails with "does not recognise this PC" and does not retry | A session starts with a forgotten device | — |
| W7 | Reinstall the Android app (new key) and connect from the PC without re-pairing | PC fails with "identity has changed … forget it and pair again", not retried | Connects, or retries for 30 s | Reinstall |
| W8 | DHCP: renew the tablet's address (toggle airplane mode) between sessions, then connect | Found under its new address | Stuck on the old address | — |
| T1 | PC: *Input from the tablet* = Touch; connect (USB). Tap a desktop icon; double-tap to open it; drag a window by its title bar; two-finger scroll a browser page; press and hold on the desktop | Tap selects; double-tap opens; window follows the finger; page scrolls; press-and-hold opens the context menu (Windows touch) | Taps land elsewhere, nothing happens, stuck press | **Acts on the PC's screen** |
| T2 | Coordinate accuracy: open Paint full screen on the mirrored display and touch the four corners and centre on the tablet | Marks within ~1 % of the touched spots, including with a 16:10 or portrait monitor (letterbox) | Offset or scaled marks | Acts on the PC |
| T3 | Input = Mouse: tap, drag, hold 0.6 s, two-finger scroll | Click, drag, right-click menu, wheel scroll | Wrong button, stuck button | Acts on the PC |
| T4 | T1 over Wi-Fi | As T1 | As T1 | — |
| T5 | Put a finger down and hold; pull the USB cable (or turn Wi-Fi off) | No stuck touch or button on the PC after the connection is lost (try clicking with the real mouse) | A contact or button stays down | Needs hands |
| T6 | Finger down and hold; on the PC, Disconnect; then set Input = Off during a held touch | Released at once both times | Anything stays down | — |
| T7 | Finger down and hold; press Home on the tablet; rotate the tablet during a drag | Released; after return, touch works again | Stuck contact | — |
| T8 | With input on: swipe from the tablet's screen edge (Back gesture) | First Back shows the controls; a second Back ends the session | Session ends on the first accidental Back | — |

## Beta Gate

| Criterion | Status | Evidence |
| --- | --- | --- |
| Scope stable (v1 = USB Mirror) | PASS | ROADMAP v1 scope; nothing added beyond it |
| No critical defects open | PASS | ISSUES.md: none open at Critical |
| High-severity defects resolved or accepted | **FAIL** | ES-030, ES-031, ES-053, ES-054, ES-060, ES-061 open; ES-050 accepted as a known issue; ES-063 accepted with guidance |
| Windows application functional | PASS (automated) / NOT TESTED (hardware) | Suites; smoke launches of the debug build and the packaged app |
| Android application functional | PASS (automated) / NOT TESTED (hardware) | 268 JVM tests; builds |
| Core USB connection flow tested on hardware | NOT TESTED for this build | H1 |
| Core mirroring flow tested on hardware | NOT TESTED for this build (PASS on earlier builds) | H1, H4 |
| Session shutdown and reconnect tested | NOT TESTED for this build | H2, H3 |
| D64 recovery validated on hardware | NOT TESTED | H5 (ES-031) |
| Display experience acceptable | PASS (automated) / NOT TESTED (hardware) | D67–D69 |
| Performance characterised | PARTLY | Earlier-build figures; CPU and battery not measured (P2, P6) |
| Compatibility risks understood | PASS | compatibility-and-performance.md |
| Release builds available | PASS (Windows) / BLOCKED (Android installable build needs signing) | artifacts/release |
| Installation process documented | PASS | docs/beta/ |
| Prerequisites clear | PASS | install-windows.md; in-app messages |
| Privacy and security review completed | PASS | security-privacy-review.md |
| Automated test suite passing | PASS | PROJECT_STATUS.md |
| Hardware acceptance completed | **NOT TESTED** | All "HW 0.2.0-beta.1" cells |
| Beta tester instructions prepared | PASS | docs/beta/ |

### Verdict: **Beta Candidate**

The code, the Windows package and the tester materials are ready for the owner's acceptance; the
Android package needs the owner's signing key. It is **not Beta Ready**: no physical test of this
build has been made, D64 is unverified on hardware, and only one PC and one tablet have ever been
used. It is not "Not Beta Ready" either: no known defect prevents responsible testing by the owner.
