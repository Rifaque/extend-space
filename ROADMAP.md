# ExtendSpace roadmap

The master plan from the current code to a friends beta. Status is kept here; the verified state of
the code is in [PROJECT_STATUS.md](PROJECT_STATUS.md), open work in [ISSUES.md](ISSUES.md), and the
decisions in [docs/architecture/decision-log.md](docs/architecture/decision-log.md).

Last updated 2026-11-23, at the end of the Phase 9–11 mission.

## Progress

| Phase | Goal | Status |
| --- | --- | --- |
| 1 | Project audit and scope lock | **Complete** (2026-11-22) |
| 2 | Architecture and technical decision closure | **Complete** (2026-11-22) |
| 3 | Core application completion | **Complete in code; owner-run hardware acceptance outstanding** (ES-030) |
| 4 | Streaming reliability and recovery | **Complete in code; D64 device rerun outstanding** (ES-031) |
| 5 | Display experience and stream observability | **Complete in code** (D67–D69); hardware checks in H1, H2, H4, H9 |
| 6 | UX, compatibility, performance, reliability, security | **Complete as far as possible without hardware** (D70, ES-100, ES-111, reviews); compatibility hardware is owner-run (H12) |
| 7 | Packaging, installation, release engineering | **Complete** for Windows (validated ZIP + installer, D72); Android build configuration complete, **signing is owner action** (ES-061) |
| 8 | Beta candidate, QA and Beta Gate | **Beta Candidate** reached; Beta Gate awaits owner hardware acceptance (ES-106) and signing |
| 9 | Research, architecture, plan for wireless, touch and Extend | **Complete** (2026-11-23): [research](docs/research/phase-9-research.md), D74–D85 |
| 10 | Transport modernisation and wireless Mirror | **Complete in code**; W1 and W2 PASS on hardware; W3–W8 owner-run (ES-121) |
| 11 | Touch input and tablet interaction | **Complete in code**; touch reaching Windows seen on hardware; the ES-132 fix verified with the real injector, the full path after it only inferred; T1–T8 owner-run (ES-122) |
| Extend | Extend mode foundation (E-M1 to E-M5) | **Prototype, hardware-proven** (2026-11-23): [research](docs/research/extend-mode-driver-research.md), [architecture](docs/architecture/extend-mode-architecture.md), [acceptance](docs/quality/extend-mode-beta-acceptance.md) |
| — | **Friends-beta gate** | **Not reached**: see [beta acceptance](docs/quality/beta-acceptance.md#beta-gate) |

Phase definitions changed at the start of the Phase 5–8 mission: the owner's brief made Phase 5
"display experience and observability", 6 "UX, compatibility, performance, reliability and
security", 7 "packaging and release engineering" and 8 "beta candidate and gate". The earlier
plan's separate "compatibility" phase is now Phase 6's review plus the owner-run hardware matrix
(H12); nothing was dropped.

The stages of the [production session architecture](docs/architecture/production-session-architecture.md#12-migration-plan)
(1 to 8) are the engineering history behind Phases 1–4; stages 1–5 and 4.1 are done, stage 6 (stall
detection) moves into Phase 5, stage 7 (hardware validation) is spread over Phases 3–7, and stage 8
(retiring the experiments) into Phase 8.

## v1 scope (Phase 1 decision)

**ExtendSpace v1 is USB Mirror:** the main Windows display shown live on one Android device
connected by a USB cable, started from the Windows app with the device chosen there, and shown
full screen on the device until either side ends it, with automatic recovery from short
interruptions (cable pulls, a frozen app, rotation, Home).

Evidence for this reading: the production session architecture is scoped to "USB Mirror mode only"
(its section 1); everything built and measured so far is that path; the README lists Extend and
wireless as planned, not built; Extend needs an IddCx driver whose public distribution needs an EV
certificate and a Hardware Developer Program account, which only the owner can obtain
([IddCx feasibility](docs/architecture/iddcx-feasibility.md#5-packaging-signing-installation-and-removal)).

| In v1 | After v1 (deferred, documented, not started) |
| --- | --- |
| USB transport through adb (the user's Platform-Tools) | Wi-Fi transport and pairing |
| Mirror of a chosen Windows display (the main one by default, D68), 1080p60 or 720p30 by the client's decoder (D16) | Extend mode (virtual display driver) |
| Device discovery and choice on Windows; session screen on Android | Input forwarding (touch, pen, keyboard) |
| Recovery from interruptions; bounded teardown on both sides | Audio |
| Clear status and errors on both sides | Several Android devices at once |
| Diagnostics sufficient to report a fault | Cloud relay, accounts, anything needing the internet |

**Mirror is enough for v1.** A second *extended* screen is the product's long-term promise, but it is
blocked on signing that engineering cannot solve, and Mirror is useful alone (presenting, watching,
a second view of a working window). Extend does not block the beta; the README keeps it as planned.

## Phase definitions and exit gates

### Phase 1: Project audit and scope lock (complete)

Inventory of both apps, the protocol, tests, builds and experiments; production composition traced;
feature and test audit; v1 scope above; risk register (PROJECT_STATUS.md); issue ledger (ISSUES.md).

Exit gate, met: inventory complete; composition traced (`App.xaml.cs` → `HostComposition` →
`SessionCoordinator` + `HostConnectionController` + `UsbTransport` + `DesktopVideoPipeline`;
`ExtendSpaceApplication` → `ClientSessionController` + `UsbHostLink` + `MediaCodecVideoReceiver`);
release builds known to compile and to exclude the debug experiments; v1 scoped; issues prioritised.

### Phase 2: Architecture and decision closure (complete)

The four production flows and their ownership boundaries were traced
([architecture section 21](docs/architecture/production-session-architecture.md#21-phases-14-flows-boundaries-and-what-changed));
the D63 recovery defect was analysed against four options and resolved by D64; Connect enablement
(D65) and the Android session screen (D66) were decided.

Exit gate, met: no architectural uncertainty left that makes Phases 3–4 unsafe; the protocol is
unchanged.

### Phase 3: Core application completion (complete in code)

Windows: device discovery and choice, Connect enabled for USB Mirror, status and errors (D65).
Android: Wait for PC, the full-screen session screen with waiting, reconnecting, ended and failed
states, Back and Close ending the session (D66).

Exit gate: primary flow implemented — **yes**; device selection — **yes, unit-tested**; lifecycle —
**yes, unit-tested, and the underlying session path accepted on hardware in stages 4–4.1**; no
developer workaround needed — **yes in code**; core tests pass — **yes**; release builds compile —
**yes**. Outstanding: the UI flow itself on hardware (ES-030), which needs the owner's PC screen and
tablet.

### Phase 4: Streaming reliability and recovery (complete in code)

D58–D62 verified in place; D63 resolved (D64) with regression tests that fail against the old code;
transport, decoder and pipeline lifecycle reviewed.

Exit gate: D63 addressed — **yes**; regression tests — **yes, with deliberate breakage**; decoder and
transport lifecycle reviewed — **yes** (section 21.4); repeated recovery in automated tests — **yes**;
Windows and Android suites pass — **yes**; release builds — **yes**; no critical reliability defect
open without acceptance — **yes** (capture starvation, ES-050, is High and explicitly accepted into
Phase 5); hardware-only checks identified — **yes** (ES-031 to ES-039).

### Phase 5: Display experience and stream observability (complete in code)

Stream health layer 1 (D67): each second classified as flowing, quiet, encoder/transport/client
stalled or no client report, shown on Home and on Diagnostics; a still screen is never called a fault;
one bounded keyframe when the client stalls. Display choice by device name (D68). Picture fitted to
the stream's real size (D69). Stream figures on the Diagnostics page, each named where it is measured.
D64 not regressed (its tests run in every suite). Deferred with reason: layer 2 starvation detection
(needs the owner-run experiment, ES-050); following a resolution change (ES-057).

### Phase 6: UX, compatibility, performance, reliability, security (complete without hardware)

Journey audit (install → connect → stream → end → reconnect): fixed closing the app mid-session
(D70, ES-100), plain advice for fixable failures (ES-111), placeholder pages removed (ES-041), a
first-picture controls hint on Android, adb version on Diagnostics. Reviews:
[compatibility and performance](docs/quality/compatibility-and-performance.md),
[security and privacy](docs/quality/security-privacy-review.md). No optimisation (no measured
bottleneck). Hardware compatibility (other vendors, other PCs) is owner-run (H12).

### Phase 7: Packaging and release engineering (complete; signing is owner action)

Windows: self-contained ZIP with a per-user installer and uninstaller (D72), validated in isolated
folders, which found ES-110. Android: versioned release build, debug-class check, signing from a
git-ignored file (proven with a throwaway key). One version on both (D71). adb external (D73).
[Release process](docs/release/release-process.md); tester guides in [docs/beta](docs/beta/).

### Phase 8: Beta candidate (reached)

[Acceptance matrix, hardware procedures H1–H13 and the Beta Gate](docs/quality/beta-acceptance.md).
Verdict: **Beta Candidate**: code, Windows package and tester materials ready; Android needs the
release key; no physical test of 0.2.0-beta.1 yet.

### Friends-beta gate

All of: Phases 5–8 exited; every High issue in ISSUES.md closed or explicitly accepted by the owner;
the full V-list (architecture section 18.12) PASS on the release artifacts on at least two PCs and
three Android devices; no open Critical issue.

## Phases 9–11 (summary)

Phase 9 chose the shape (the tablet listens on Wi-Fi as on USB, the PC connects; mutual TLS 1.3 with
pinned keys; commit-reveal numeric-comparison pairing; DNS-SD; touch as contact frames on channel 2,
injected as Windows synthetic touch or as mouse) after critiquing a first plan that had the PC listen,
an insecure PIN scheme, a copied connection controller and a second input socket. Phase 10 and 11
implemented it on both apps without changing the USB path's behaviour. Exit criteria and their
evidence: research sections 16–17 and PROJECT_STATUS.md.

**Current direction (2026-11-23, Ubuntu preparation):** Windows primary; Ubuntu 26.04 LTS GNOME Wayland
the primary Linux target; CachyOS frozen at `7c24c3f`. Next: milestone **U0** (Ubuntu install and
baseline), then U1–U5, with the host and tablet UI redesign (R1–R3, A1–A3) in parallel on Windows. Plan:
[ubuntu-and-ui-redesign-roadmap.md](docs/roadmap/ubuntu-and-ui-redesign-roadmap.md).

Earlier recommendation (after the CachyOS mission): Phase B on Ubuntu (GNOME, Wayland and
X11): the portal RemoteDesktop/VIRTUAL paths that CachyOS + Hyprland could not exercise (ES-150), the
procedures not run on CachyOS (ES-179), and the VA-API latency question (ES-173). CachyOS Phase A is
done: [platform-acceptance.md](docs/quality/platform-acceptance.md#phase-a-cachyos--run-2026-11-23-native-real-pad-7).

Earlier recommendation (after the Windows completion and Linux mission): run the Linux owner
procedure on a GNOME and a KDE machine (ES-150), then Flatpak (ES-153) and a Secret Service identity
store (ES-151). Windows: owner checks of right-click/scroll and sleep/wake; signing (ES-060).

| Track | State |
| --- | --- |
| Windows completion (W-M1 to W-M6) | Done where measurable: [windows-completion.md](docs/research/windows-completion.md) |
| Linux host (L-M1 to L-M10) | Prototype: [linux-host-research.md](docs/research/linux-host-research.md), [architecture](docs/architecture/linux-host-architecture.md) |
| Linux native Phase A (CachyOS, Hyprland) | **Done 2026-11-23**: Mirror, Extend, Mouse, USB, Wi-Fi verified on hardware (D104–D107) |
| Linux native Phase B (Ubuntu) | Next |

Earlier: **Recommended next milestone (updated after the Extend mission): tablet-shaped virtual display modes
(ES-140) with a lifecycle that does not leave an invisible monitor (ES-141)**, then the remaining W, T and
E hardware checks. Extend E1 itself is done.

Earlier recommendation: hardware acceptance of Wi-Fi and touch (W3–W8, T1–T8), then Extend E1.
The code is in place and its two riskiest assumptions (AndroidKeyStore as a TLS server; synthetic
touch on a multi-monitor PC) have already been met on hardware, one of them only after a fix; the
remaining procedures are what stand between this and a beta that includes Wi-Fi and touch. Extend E1
(a user-installed Virtual Display Driver plus the existing capture and input paths) follows.

## Next milestone: Beta Gate

1. Owner: create the Android release key and build the signed APK (release process).
2. Owner: run H1, H2, H3 and H5 on this PC and the Pad 7 (about an hour); record results in
   beta-acceptance.md.
3. Fix whatever they find; rerun.
4. Owner: H12 on at least one more Android device and one more PC; H13 on a clean machine.
5. Then the gate's remaining rows; if all pass, hand 0.2.0-beta.(n) to friends.

Estimate after this mission: one to two weeks, dominated by hardware access and any defects the first
hardware runs find.

## Deferred work (not scheduled)

Extend mode (research and milestones E1–E5 done, D85); pen and keyboard input; audio; multiple simultaneous devices; protocol changes (D9 stale-picture notice,
request ids).
