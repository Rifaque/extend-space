# Windows completion: audit, research, plan, critique, revised plan

Mission of 2026-11-23 (after the Extend prototype). Evidence: `artifacts/windows-completion-20261123/`
(git-ignored). Decisions: D92–D95 in the [decision log](../architecture/decision-log.md).

## 1. Verified baseline

| Item | Value (measured at mission start) |
| --- | --- |
| Git | `main`, HEAD `8a9669f`, nothing staged; 39 modified and 74 untracked paths (owner's uncommitted work, preserved) |
| Version | `0.2.0-beta.1` (Directory.Build.props, D71) |
| Windows build | Debug 0 warnings, 0 errors |
| Windows tests | 1,025: 999 passed, 0 failed, 26 skipped (gated hardware/network). Core 606/7 skipped, Media 88/19, Experiments 331/0 |
| Android | `testDebugUnitTest` BUILD SUCCESSFUL (307 tests at the previous count) |
| Architecture | `ExtendSpace.Core` is plain `net10.0`: protocol, sessions, transports (USB via adb, Wi-Fi TLS), pairing, trusted devices, input model, view models. Windows lives only in `ExtendSpace.Media` (capture, encode, display, input, DNS-SD, DPAPI) and the WinUI host. Eight interfaces form the platform seam (section 5) |

## 2. Feature audit (W1–W10)

| Area | State found | Gap |
| --- | --- | --- |
| W1 Mirror | Display choice by device name (D68), WGC + MF H.264, backpressure D63/D64, diagnostics D67 | Resolution change ends the session (ES-057); browser full-screen freeze (ES-050) |
| W2 Extend | VDD detected read-only (D87–D91), streamed in its shape; E-H1/2/4/5 passed | 800 × 600 at 30 Hz default (ES-140); monitor persists (ES-141); stability third-party (ES-143) |
| W3 Modes | None | No mode model; no tablet-aware choice |
| W4 Lifecycle | Documented only | Windows and pointer get lost on an invisible display |
| W5 Touch | ES-132/133 fixed; E-H5 passed | T1–T8 owner checklist partly run |
| W6 Mouse | Gestures in `MouseGestures` | ES-135 unexplained missed click |
| W7 USB | adb forward, device list, advice texts | Repeated-session soak not run on hardware |
| W8 Wi-Fi | DNS-SD + add by address + paired-at-last-address | ES-134 discovery unreliable on this network |
| W9 UX | Home, Diagnostics, Settings; plain advice | No mode or lifecycle actions for Extend |
| W10 Packaging | ZIP + per-user installer (D72), unsigned (ES-060) | Signing is an owner action |

## 3. Research findings (measured on this PC unless marked)

1. **VDD's modes** (EnumDisplaySettings on `\\.\DISPLAY9`): 800 × 600, 1366 × 768, 1920 × 1080,
   2560 × 1440, 3840 × 2160, each at 30, 60, 90, 120, 144, 165, 244 Hz. **60 Hz exists at every size**;
   no 3:2 mode exists. 3:2 needs a custom resolution in `C:\VirtualDisplayDriver\vdd_settings.xml`
   and a driver reload.
2. **That folder is writable by every signed-in user** (`icacls`: Authenticated Users: Modify). Any
   unelevated program can change the driver's configuration (modes, EDID) — a driver attack surface
   ExtendSpace should not use or widen. Recorded as ES-146.
3. **Changing the virtual display's mode** with `ChangeDisplaySettingsEx` on that device alone worked:
   800 × 600 at 30 Hz → 1920 × 1080 at 60 Hz, the virtual display stayed at (2560, 0), and a diff of the
   topology before and after showed **both physical monitors unchanged**
   (`topology-before.txt`, `topology-after.txt`).
4. **60 Hz Extend is real**: at 1920 × 1080 at 60 Hz, an Extend session over USB streamed **60 fps**
   (readings 60, 60, 60, 60, 59) with a window animating every frame on the virtual display; first
   picture 160 ms after the stream started (227 ms at 800 × 600). On an idle virtual desktop the rate
   is 30–42 fps because capture delivers frames only on change; that is not a defect.
5. **ES-135 (missed click)**: the product injector put the cursor on the exact aimed pixel at **32,333
   of 32,333** points over the three displays (including the laptop panel's negative coordinates), and
   a test window received **40 of 40** clicks. The injector is not the cause. The gesture code had a
   **dead zone**: a still press lifted between 500 ms (tap time) and 600 ms (long press) produced
   nothing; and as press durations are measured on the PC's arrival clock, Wi-Fi jitter can stretch a
   tap into it. Fixed (D93). The original single observation cannot be re-run, so its cause is
   *probable*, not proven. Two instrument errors were found and corrected on the way: an ad-hoc
   PowerShell probe with an unreliable result, and the gated test reading the cursor DPI-virtualised
   (0.8 × at 125 %) — both in test code, not the product.
6. During this mission the virtual display **disappeared once and the owner reconnected it**; the cause
   is not known (ExtendSpace only reads the topology). It fits ES-143 and is recorded there.
7. Upstream VDD: newest release 25.7.23 (unchanged since the Extend mission; open BSOD/black-screen
   reports #468, #474). No safer signed alternative with dynamic monitors appeared (Parsec VDD's
   licence is still unclear; SudoVDA still unsigned).

## 4. Initial plan

| # | Milestone | Content |
| --- | --- | --- |
| P1 | Correctness | ES-135 investigation; mouse normalisation fix (suspected off-by-one) |
| P2 | Modes | Write 3:2 modes into `vdd_settings.xml` and reload the driver through its pipe so the Pad 7 gets 2400 × 1600 at 60 Hz automatically |
| P3 | Lifecycle | Disable the virtual display (SetDisplayConfig) when a session ends; enable it at the next |
| P4 | Wi-Fi | Rewrite DNS-SD discovery |
| P5 | UX, packaging | Extend card actions; docs |

## 5. Critique

| Perspective | Objection |
| --- | --- |
| Display-driver engineer | P2 writes a driver's config and reloads the driver: a reload restarts every virtual monitor (windows jump) and is the path of the upstream BSOD reports. Solving a driver limitation inside the app. |
| Security engineer | P2 relies on a folder any user can write (finding 2): ExtendSpace would normalise an unsafe configuration channel. |
| Windows engineer | P3 is topology manipulation; SetDisplayConfig re-lays out the desktop and can move physical monitors' positions — the exact risk the brief forbids. |
| QA | P1's "off-by-one" was a guess; measurement refuted it (finding 5). Fix only what is measured. |
| Maintainer | P4: ES-134 showed the tablet not answering mDNS for *any* service — a network/Android power issue, not our browser; the manual address fallback already resolves the user problem. |
| Encoder view | 2400 × 1600 is larger than the D89 envelope; it would be scaled to 1620 × 1080 anyway, so "native 3:2" buys desktop room, not sharpness, at unmeasured cost. |
| End user | What helps today: one button to pick the right existing mode (60 Hz), clear guidance for a 3:2 custom mode, and a way to get lost windows back. |

## 6. Revised plan (implemented)

| Initial | Critique | Revised (decision) |
| --- | --- | --- |
| P1 mouse normalisation fix | Refuted by 32,333-point measurement | No change to the injector; **fix the measured gesture dead zone** (D93); gated accuracy test kept |
| P2 write XML + reload driver | Driver reload risk; world-writable folder | **Generic mode model + advisor** (D92); switch the virtual display between modes the driver already offers, through `ChangeDisplaySettingsEx` on that device only, refused for any display not classified virtual; suggest the tablet-shaped custom size (1616 × 1080 for the Pad 7) for the user to add in VDD Control |
| P3 disable display on session end | Topology manipulation | **"Bring windows back"** button (D94): moves only top-level windows centred on the virtual display to the main display, on request; plus a Display settings link. Persistence stays the driver's (D91) |
| P4 rewrite discovery | Not our defect; fallback works | Keep; Linux gets a portable mDNS querier (Linux plan) which can be tried on Windows later |
| P5 | — | Extend card shows the advice, "Use 1920 × 1080 at 60 Hz", "Bring windows back", "Display settings" |

## 7. Results

| Milestone | Result | Evidence |
| --- | --- | --- |
| W-M1 dead zone | Fixed; 5 new tests (fail on the old code at 500/550/599 ms) | `HostInputMouseTests` |
| W-M1 cursor accuracy | 32,333/32,333 exact; 40/40 clicks | gated `The_cursor_lands_on_the_exact_pixel_aimed_at`, `es135-click-log.txt` |
| W-M2 modes | Advisor (8 tests), view model (3), guard (6 incl. the real PC); applied on hardware; 60 fps measured | `extend-1920x1080-60-usb.png`, `extend-1920x1080-60-animated.png` |
| W-M3 lifecycle | Window rescue (3 plan tests, view-model test); button verified present in the app; not pressed on hardware | — |
| W-M4 Wi-Fi | No change (critique) | — |
| W-M6 packaging | Unchanged (D72); signing owner action | — |

**The virtual display was left at 1920 × 1080 at 60 Hz** (it was 800 × 600 at 30 Hz); change it back in
Display settings if wanted.
