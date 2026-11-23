# ExtendSpace project status

The verified state of the code. Plan: [ROADMAP.md](ROADMAP.md). Open work: [ISSUES.md](ISSUES.md).
Decisions: [docs/architecture/decision-log.md](docs/architecture/decision-log.md). Acceptance and
the gate: [docs/quality/beta-acceptance.md](docs/quality/beta-acceptance.md). How it works:
[architecture overview](docs/architecture/overview.md) and
[production session architecture, sections 21–22](docs/architecture/production-session-architecture.md#21-phases-14-flows-boundaries-and-what-changed).

As of 2026-11-23, branch `main`, HEAD `8a9669f` plus all uncommitted work from stage 3 through the
**Phase 9–11 mission** (D74–D86). Nothing of it has been committed.

## Windows completion and Linux host (2026-11-23)

| Platform | Classification | Why |
| --- | --- | --- |
| Windows | **Beta candidate** (Mirror, USB, Wi-Fi, pairing, touch, mouse); Extend **Prototype → developer preview** | Modes and 60 Hz verified on hardware; mouse accuracy measured; lifecycle mitigated, not solved (driver-owned monitor, third-party stability) |
| Linux | **Prototype** (Lab-proven end to end with the real tablet) | Mirror + pairing + Wi-Fi + mouse ran from a real Linux host (WSL/Xvfb) to the Pad 7; GNOME/KDE portal paths, USB and X11 Extend are hardware pending |
| Android | Unchanged protocol; host-neutral wording | 307 tests |
| Project overall | **Beta candidate on Windows; prototype on Linux** | Not production-ready |

Next: native Linux hardware testing — Phase A on CachyOS (external SSD, temporary), then Phase B on
Ubuntu (same SSD; primary Linux validation afterwards). Results go in
[platform-acceptance.md](docs/quality/platform-acceptance.md#native-linux-test-phases-planned); Linux
desktop acceptance (ES-150), Linux USB (ES-154) and X11 Extend (ES-152) remain pending until then.

| Check (2026-11-23, end of mission) | Result |
| --- | --- |
| Windows tests | **1,063: 1,035 passed, 0 failed, 28 skipped** (gated). Start: 1,025 / 999 / 26 |
| Linux tests | 21: on Linux 20 passed, 1 skipped (tablet E2E, run separately: passed); on Windows 16 passed, 5 skipped |
| Android | 307 tests, 0 failed, 1 skipped; lint, Debug and Release build |
| Builds | Windows and Linux, Debug and Release: 0 warnings, 0 errors |

## Extend mode (2026-11-23): **Prototype, hardware-proven on one PC and one tablet**

ExtendSpace streams a genuine extra Windows display to the tablet: the community Virtual Display
Driver (installed by the user, D87) creates the monitor, ExtendSpace finds it read-only (D88), captures
it through the unchanged Mirror pipeline, streams it in its own shape (D89) over USB or Wi-Fi, and maps
touch onto it alone.

| Check (2026-11-23) | Result |
| --- | --- |
| Windows tests | **1,025 total: 999 passed, 0 failed, 26 skipped** (gated). Mission start: 995 / 969 / 0 / 26 |
| Android tests | **307, 0 failures** (start: 305); lint at the pre-existing notices; Debug and Release build |
| Windows Debug and Release builds | 0 warnings, 0 errors |
| E-H1 virtual monitor appears, identified | **PASS-HW** (VDD `\\.\DISPLAY9`, MTT/0x1337, extended automatically, physical monitors unchanged) |
| E-H2 Extend over USB | **PASS-HW** (800 × 600 stream, first picture 227 ms, the virtual desktop not the main one) |
| E-H3 window on the virtual display reaches the tablet | **PASS-HW**; resolution change not run |
| E-H4 Extend over Wi-Fi | **PASS-HW** (first picture 116 ms, 30 fps = the mode's 30 Hz) |
| E-H5 touch onto the virtual display | **PASS-HW** (native synthetic touch; exact points; nothing on physical displays) |
| E-H6/E-H7 lifecycle and removal | Disconnect PASS-HW; the rest NOT RUN (ES-144) |

Classification: **Prototype** — proven end to end on hardware, but the default 800 × 600/30 Hz monitor
(ES-140), the persistent monitor (ES-141) and the third-party driver's stability (ES-143) keep it from a
developer preview until custom tablet-shaped modes and the lifecycle are addressed.

## Phases 9–11 (2026-11-22/23): wireless Mirror and touch

| Phase | State |
| --- | --- |
| 9 Research and architecture | **Complete**: [research report](docs/research/phase-9-research.md) (initial plan, critique, revised plan), four architecture documents, D74–D85 |
| 10 Transport and wireless Mirror | **Complete in code; hardware partly verified** (W1 pairing and W2 Mirror over Wi-Fi PASS on this PC and the Pad 7; W3–W8 owner-run) |
| 11 Touch input | **Complete in code; hardware partly verified** (tablet touches reach Windows over Wi-Fi; after the ES-132 fix the injector hits the exact point, and a tablet tap went to the mirrored monitor (inferred, not logged); T1–T8 owner-run) |

| Check (2026-11-23) | Result |
| --- | --- |
| `dotnet build ExtendSpace.slnx`, Debug and Release | 0 warnings, 0 errors |
| `dotnet test --solution ExtendSpace.slnx` | **995 total: 969 passed, 0 failed, 26 skipped** (gated device, LAN, input and interop tests). Mission start: 878 / 856 / **1 failed** (flaky port test, ES-125) / 21 |
| Android `gradlew testDebugUnitTest lintDebug assembleDebug assembleRelease` | **305 tests, 0 failures** (start: 268); lint only the pre-existing `AndroidGradlePluginVersion` and `GradleDependency` notices; release APK 24.1 MB, no debug or test classes, no Bouncy Castle, permissions `INTERNET` and `CHANGE_WIFI_MULTICAST_STATE` |
| Deliberate breakage of security and lifecycle rules (pin check, trust check, commitment check, pairing window, trust on decline, release on end, input without offer, stale frames, cancel-all) | **10 of 10 caught**; the one survivor of the first pass (trust stored when the tablet declines after the PC accepted) exposed a missing test, now added |
| Cross-implementation interop on one PC (gated): real Windows pairing/transport/controller/input against the real Android endpoint/link on the JVM, loopback | **PASS**: same pairing code on both sides, TLS 1.3 session, keyframe delivered, touch injected at the expected point, clean end |
| DNS-SD browse against a real advertisement on this PC (gated) | **PASS** |
| **Hardware, Pad 7 over USB** (V19a, 20 s, real capture and NVIDIA encoder) | **PASS**: 818 received, 0 missing, 818 rendered |
| **Hardware W1**: pairing through both apps' screens over Wi-Fi | **PASS**: both showed 536 629; both stored the other; AndroidKeyStore + Conscrypt as TLS server worked (ES-123 closed) |
| **Hardware W2**: Mirror over Wi-Fi from the app screens | **PASS**: "Mirroring over the local network, streaming at 58 fps"; first picture 192 ms after the stream started |
| **Hardware touch** | Touches reached Windows; **defect ES-132** (they landed on the laptop panel) reported by the owner, found and fixed; **ES-133** (DPI) found and fixed; after the fixes the real touch and mouse injectors hit the exact point on a test window; T1–T8 not run |
| Discovery on hardware | Intermittent (ES-134): add-by-address and last-address fallbacks built and used |

Evidence (git-ignored): `artifacts/phase10-11-20261123/` (device logs, tablet screenshots of the
pairing code and the paired state).

## Where it stands: **Beta Candidate 0.2.0-beta.1** (Phase 5–8 record below)

- **Code:** Phases 1–8 complete as far as they can be without hardware. Both apps start, show and end
  a USB Mirror session from their own screens; the Windows app chooses the device and the display and
  shows stream health; the Android screen fits the picture to the stream.
- **Packages:** a validated Windows ZIP with a per-user installer; an Android release APK that is
  **unsigned** until the owner creates the release key.
- **Hardware:** the session path was accepted on one PC and one Xiaomi Pad 7 in October–November 2026
  on earlier builds. **No physical test of 0.2.0-beta.1 has been made** (ES-106). D64 is unverified on
  hardware (ES-031).
- **Not Beta Ready** until the owner-run acceptance (H1–H13) and Android signing are done.

## Release artifacts (git-ignored, `artifacts/release/`)

| File | Version | Signed | SHA-256 | Validated |
| --- | --- | --- | --- | --- |
| `ExtendSpace-0.2.0-beta.1-win-x64.zip` (89.9 MB) | 0.2.0-beta.1 | No | `b627e55f47ba9b1e17aec1ef3244ccb2ae309f730fb5395e2692345a10ce7e3b` | Extracted, installed, launched (device listed, Diagnostics correct), clean exit, upgrade, uninstall, in scratch folders on this PC, not a clean machine |
| `ExtendSpace-0.2.0-beta.1-android-unsigned.apk` (22.9 MB) | 0.2.0-beta.1 (code 20001) | No | `f9f2e1db03d9fcaa34e77f74c572326d7a3fa2a969403062cdaeced97f707048` | No debug-only classes; minSdk 29, targetSdk 37; **cannot be installed until signed** |

`artifacts/release/superseded-*` hold three earlier Windows builds of the same version kept as
evidence: two that crashed at start (ES-110) and one without the tester guides. Do not distribute them.

## Feature matrix

**Verified HW** = run on hardware on an earlier build of the same path; **Implemented** = tested
automatically, not on hardware in this build.

| Area | Feature | Status |
| --- | --- | --- |
| Windows | Device discovery and choice, adb detection and version | Implemented (D65, D73) |
| Windows | Display choice by device name, main display by default | Implemented (D68) |
| Windows | USB connection, forward, handshake, keepalive, reconnection | Verified HW |
| Windows | Capture and H.264 encoding, output check | Verified HW (main display) |
| Windows | Send queue, D64 recovery | Implemented (D64); previous form verified HW |
| Windows | Stream health on Home, figures on Diagnostics | Implemented (D67) |
| Windows | Closing the app ends the session cleanly | Implemented (D70) |
| Windows | Errors with plain advice | Implemented (ES-111) |
| Windows | Installer, upgrade, uninstall | Implemented and validated in scratch folders (D72) |
| Android | Wait for PC, session screen, waiting/reconnecting/ended/failed states, Back, Close | Implemented (D66) |
| Android | Picture fitted to the stream, rotation | Implemented (D69); rotation verified HW with the harness |
| Android | Link, decoder lifecycle, backlog and recovery | Verified HW (D58–D61) |
| Android | Background beyond 30 s ends the session | By design (ES-058) |
| Both | Wi-Fi Mirror with pairing (D74–D80) | Implemented; W1, W2 verified on hardware |
| Both | Touch input, Touch and Mouse modes (D81–D84) | Implemented; touch reaching Windows seen on hardware; T1–T8 pending |
| Both | Extend with the community Virtual Display Driver (D87–D91) | Prototype; E-H1, E-H2, E-H4, E-H5 verified on hardware |
| Both | Audio, pen, several devices | Not built |

## Tests and builds (final run, 2026-11-22)

| Check | Result |
| --- | --- |
| `dotnet build ExtendSpace.slnx` (Debug) and `-c Release` | 0 warnings, 0 errors each |
| `dotnet test --solution ExtendSpace.slnx` | **878 total: 857 passed, 0 failed, 21 skipped** (gated device, adb and real-media tests). Start of this mission: 836 / 815 / 21 |
| Focused Windows classes repeated 5 times (controller, queue, health, display/health, device selection, shutdown/advice: 125 tests; media suite: 73) | 5 of 5 passed each |
| Android `gradlew testDebugUnitTest lintDebug assembleDebug assembleRelease` | **268 tests, 0 failures** (start: 260); lint: only `AndroidGradlePluginVersion` and three online `GradleDependency` notices; both variants build |
| Deliberate breakage, Phase 5–6 logic | 9 of 10 caught; the survivor is equivalent (the list is sorted main-first, so the dropped fallback is redundant) |
| Deliberate breakage, D64 (previous mission) | 5 of 5 caught; its tests still pass |
| Android instrumented / UI tests | **None exist**; not run |
| Windows UI | UI Automation reads of the debug build and of the packaged app (no clicks beyond selecting the Diagnostics page; no session started) |
| Physical device tests of this build | **None** |

Suite composition: `ExtendSpace.Core.Tests` 474 (6 gated), `ExtendSpace.Media.Tests` 73 (15 gated),
experiment tests 331; Android JVM tests 268. What they prove and do not prove is unchanged from the
previous mission: no UI automation of the screens, one PC and one tablet ever used.

## Risk register

| # | Risk | Likelihood | Impact | Mitigation | Blocks beta |
| --- | --- | --- | --- | --- | --- |
| R1 | Other Android decoders behave differently | High | High | H12 on two more vendors (ES-052, ES-053) | Yes |
| R2 | The new screens or D70 fail on hardware | Medium | High | H1, H2 (ES-030, ES-106) | Yes |
| R3 | D64 behaves differently on the device | Low | Medium | H5 (ES-031) | Yes |
| R4 | Other PCs/GPUs/encoders fail | Medium | High | H12 (ES-054) | Yes |
| R5 | Testers cannot install the APK (unsigned) | Certain until signed | High | Owner creates the key (ES-061) | Yes |
| R6 | Capture starvation freezes the picture | Medium | Medium | Shown as "no new picture"; documented known issue (ES-050) | No (accepted) |
| R7 | Health thresholds wrong in practice (false "stalled") | Medium | Low | Report-only except a bounded keyframe; check in H4 (ES-103) | No |
| R8 | adb set-up defeats testers | Medium | Medium | In-app advice, guide, Diagnostics version (D73) | No |
| R9 | SmartScreen deters testers | High | Low | Guide explains; checksum; signing later (ES-060) | No |

## Known limitations

USB only; one display mirrored (chosen); 1080p60 or 720p30; a resolution change ends the session;
no input or audio; one device; 30 s without the session screen ends the session; adb installed by
the user; capture starvation shown but not distinguished from a still screen; both packages unsigned.

## Next

1. Owner: Android release key → `Build-Apk.ps1` (signed APK).
2. Owner: H1, H2, H3, H5 on this PC and the Pad 7 ([procedures](docs/quality/beta-acceptance.md#owner-run-hardware-procedures)).
3. Then H12/H13 on other hardware and a clean machine, and the remaining Beta Gate rows.
