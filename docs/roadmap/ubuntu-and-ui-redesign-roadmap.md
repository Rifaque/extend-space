# Ubuntu and UI redesign roadmap

2026-11-23. Order of work after the CachyOS freeze. Inputs:
[ubuntu-platform-strategy.md](../research/ubuntu-platform-strategy.md),
[ubuntu-host-architecture.md](../architecture/ubuntu-host-architecture.md),
[ubuntu-acceptance-plan.md](../quality/ubuntu-acceptance-plan.md),
[host-ui-redesign.md](../ui/host-ui-redesign.md),
[android-tablet-redesign.md](../ui/android-tablet-redesign.md). Status lives in the root
[ROADMAP.md](../../ROADMAP.md).

## 1. Desktop support tiers

| Tier | Environment | Commitment | Gate to claim it |
| --- | --- | --- | --- |
| **1 Primary** | Ubuntu 26.04 LTS, GNOME 50, Wayland | Supported; every release tested | Acceptance plan Stage 8 "Supported" |
| 1b | Ubuntu 24.04 LTS, GNOME 46, Wayland | Supported after Tier 1 passes | Abbreviated rerun (M1–M4, I1–I3, E1–E6) |
| **2 Secondary** | Kubuntu 26.04, Plasma 6.6, Wayland | Supported with limitations (Extend fixed 1920 × 1080 until Plasma 6.8) | Stage 9 |
| 3 Experimental | X11 desktops: Xubuntu, Lubuntu, Ubuntu MATE, Ubuntu Cinnamon, Ubuntu Unity | Mirror + Mouse via existing X11 code; Extend experimental; no Touch | Community reports; no release gate |
| Frozen | CachyOS / Hyprland (`7c24c3f`) | Code kept, not developed | Regression tests only |
| Research only | Ubuntu Budgie (wlroots, no RemoteDesktop portal), Sway | Mirror may work via portal-wlr | — |
| Not evaluated | Ubuntu Kylin / UKUI | — | — |
| Not separate | Ubuntu Studio (Plasma) | Covered by Tier 2 | — |
| Unsupported | Snap/Flatpak sandboxes of ExtendSpace (not built), headless sessions, GNOME on Xorg in 26.04 (does not exist) | — | — |

## 2. Initial roadmap (v1, before critique)

1. Ubuntu baseline → 2. refactor Linux into desktop backends → 3. GNOME portal fixes → 4. KDE →
5. X11 desktops → 6. `.deb` + Flatpak → 7. Secret Service → 8. host UI redesign → 9. Android redesign.

## 3. Review of v1

| Question | Answer | Change |
| --- | --- | --- |
| Is GNOME Wayland the right first target? | Yes: Ubuntu's default, Wayland-only in 26.04, and the portal path CachyOS never exercised (ES-150) | Keep |
| Are the portals sufficient? | For Mirror and pointer: yes (spec). Touch: likely, unconfirmed. Persistence for input: needs the SelectDevices fix | Add the persistence fix to the first fixes |
| Can Extend truly work? | On GNOME, by portal design; size and refresh unmeasured. On Kubuntu 26.04: only 1920 × 1080 | Stage 5 decides; plan a "size differs" UI state |
| Realistic virtual-display architecture? | Yes for GNOME/KDE; X11 RandR remains a gamble | X11 Extend stays experimental, no new work |
| Touch and mouse safe? | Yes, portal only, no uinput | — |
| Too many desktops? | v1 had KDE and X11 as steps 4–5 | **X11 desktops dropped from the plan** (Tier 3, no work); KDE only after GNOME is accepted |
| Packaging too complex? | `.deb` + Flatpak at once is two formats nobody tested | **`.deb` only**; Flatpak after the beta |
| Encoder policy robust? | A fixed order is not | Runtime latency guard (architecture §7) |
| Windows unaffected? | Only if Core changes are additive and Windows tests run on Windows | Every Core change gated by a Windows run |
| Android redesign compatible with streaming? | Yes if overlays never resize the `SurfaceView` and the protocol is untouched | Protocol items deferred |
| Host redesign without a huge rewrite? | Yes: views over the existing view model, plus additive VM state | — |
| Milestones correctly ordered? | Refactoring before the baseline would test new code instead of the validated code | **Refactor moved after acceptance** |
| Biggest risks? | Section 6 | — |
| Cut from initial Linux support? | X11 Extend, Flatpak, KDE before GNOME, Secret Service before beta, uinput anything, tray icon on GNOME | Cut |

## 4. Revised roadmap (authoritative)

| # | Milestone | Content | Depends on | Exit gate |
| --- | --- | --- | --- | --- |
| U0 | **Ubuntu install and baseline** (tomorrow) | Stage 0–1: environment record, build, tests, launch, baseline failures logged as ES items | Ubuntu 26.04 installed | `env.txt`, baseline table, no fixes yet |
| U1 | **Connectivity and Mirror on GNOME** | Stage 2–3 incl. latency matrix; fix blockers found | U0 | C1–C9, M1–M6 PASS-HW |
| U2 | **Input on GNOME** | RemoteDesktop persistence on SelectDevices; granted-device availability; Stage 4 | U1 | I1–I3, I5–I6 PASS-HW; touch result recorded either way |
| U3 | **Extend on GNOME** | Stage 5; size-differs state; crash cleanup verified | U1 | E1–E6 PASS-HW, or Extend classified with the measured limits |
| U4 | **Ubuntu packaging** | `install.sh` adds `-bad`; `.deb` with declared dependencies; Stage 6 | U1 | P1–P5 PASS |
| U5 | **Ubuntu verdict** | Stage 7–8; docs and UI limits text final | U1–U4 | Tier 1 classification recorded |
| U6 | Linux backend refactor | `IDesktopBackend` (architecture §4), no behaviour change | U5 | All Linux + Hyprland tests unchanged and green; Phase B smoke rerun |
| U7 | Kubuntu (Tier 2) | Stage 9 | U5 | Classification recorded |
| R1 | Host UI: fixes | Stale About text, developer wording, consistent order on both | none (can start any time on Windows) | UIA smoke reads, Avalonia headless tests |
| R2 | Host UI: Home + Devices | Section 7.1–7.2 on both platforms, remembered choices (ES-175) | R1 | Owner walkthrough on Windows and Ubuntu |
| R3 | Host UI: Display, Diagnostics, Settings, first run | 7.3–7.7 | R2, U5 (Linux limits known) | same |
| A1 | Android UI | Adaptive Home, Settings, wording | none | JVM state tests; Pad 7 walkthrough |
| A2 | Android session bar | Handle, bar, info sheet, Pause input | A1 | Pad 7 hardware run (USB + Wi-Fi) |
| A3 | Android ready-by-default | Listener lifecycle (decision needed) | A1 | Tests + hardware: PC connects without tablet taps |
| S1 | Secret Service identity key | Research §10 | U5 | Unit tests + GNOME and KDE runs |
| W | Windows (parallel, primary) | Beta Gate items in ROADMAP.md (signing, H1–H13) continue; redesign R1–R3 lands on Windows first | — | Windows suite unchanged count; owner H-runs |

Parallelism: U-track needs the owner's Ubuntu machine; R1 and A1 can be built on Windows the same week.
**Tomorrow is U0, then as much of U1–U3 as the day allows.** No redesign work tomorrow.

## 5. Acceptance gates (summary)

- No milestone is "done" on lab evidence alone; Linux gates are PASS-HW rows.
- Any shared Core change: Windows Core + Media tests on Windows, Linux tests on Ubuntu, Android tests if
  protocol-adjacent.
- Windows regression: the Windows suite count must not fall (last: 1,063 total, 1,035 passed, 0 failed,
  28 skipped); gated hardware rows for touched areas rerun by the owner.

## 6. Risks

| Risk | Likelihood | Impact | Handling |
| --- | --- | --- | --- |
| GNOME virtual monitor ignores the tablet size or runs below 60 Hz | Medium | High (Extend quality) | Measure in E2/E3; show actual size; investigate Mutter negotiation; worst case 16:9 with letterbox |
| Portal-gnome does not offer TOUCHSCREEN | Medium | Medium | Touch "unavailable on this desktop" (existing D106 mechanism); Mouse remains |
| 26.04 pointer-only motion not producing frames | Medium | Medium (Mouse feels laggy) | M7; document the env-var workaround; report upstream with a reproducer |
| Consent dialog every connection with input on | High until fixed | Medium | U2 persistence fix |
| VA-API latency on Intel-only machines (ES-173) | High on Intel | High on those machines | Runtime guard falls back to x264; diagnostics say so |
| NVIDIA driver/Wayland issues on Ubuntu | Low-medium | High | Record driver version; test Mirror before Extend |
| UFW blocks mDNS replies | Low (UFW off by default) | Low | Add-by-address prominent; document `ufw allow` line if confirmed |
| Avalonia under XWayland blurry with fractional scaling | Medium | Low-medium | Record in B5; Avalonia 12.1 native Wayland is experimental — not adopted now |
| Core changes regress Windows | Low | High | Additive interfaces; Windows run before any commit |
| Redesign scope creep delays the Windows beta | Medium | High | R1 small and first; R2/R3 after the Windows Beta Gate items are scheduled |

## 7. What was cut, and why

| Cut | Reason |
| --- | --- |
| X11 desktop implementation work | Ubuntu's default has no X11; Tier 3 keeps the existing code |
| Flatpak, AppImage, Snap | One format done properly; `.deb` matches the system-dependency model |
| KDE before GNOME accepted | Splits attention; KDE 6.6 cannot do tablet-shaped Extend anyway |
| uinput, root helpers, udev rules from ExtendSpace | Security (D100) |
| Floating mouse buttons on the tablet | Covers the picture; gestures exist |
| Changing input mode from the tablet | Protocol change; after Phase B |
| Tray icon on Linux | GNOME does not show it without an extension |
