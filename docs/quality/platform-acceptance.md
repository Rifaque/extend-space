# Hardware acceptance: Windows completion and Linux host

Legend: **PASS-HW** run on real hardware in this mission (2026-11-23); **PASS-LAB** run on real Linux in
the lab (WSL2 Ubuntu 24.04, Xvfb, real tablet where stated) — not a desktop acceptance; **PASS-AUTO**
automated tests only; **NOT RUN**; **OWNER**. Earlier results: [beta-acceptance.md](beta-acceptance.md),
[extend-mode-beta-acceptance.md](extend-mode-beta-acceptance.md). Evidence (git-ignored):
`artifacts/windows-completion-20261123/`, `artifacts/linux-20261123/`.

## Windows

| Check | Result | Evidence |
| --- | --- | --- |
| USB Mirror | PASS-HW (earlier missions) | beta-acceptance |
| Wi-Fi Mirror | PASS-HW (W2, earlier) | beta-acceptance |
| USB Extend | **PASS-HW** at 800 × 600 (earlier) and 1920 × 1080 at 60 Hz (this mission) | `extend-1920x1080-60-usb.png` |
| Wi-Fi Extend | PASS-HW (E-H4, earlier) | extend acceptance |
| Display modes | **PASS-HW**: "Use 1920 × 1080 at 60 Hz" changed only the virtual display; physical monitors unchanged (topology diff) | `topology-before.txt`, `topology-after.txt` |
| 60 Hz | **PASS-HW**: 60 fps streamed with moving content | `extend-1920x1080-60-animated.png` |
| Resolution change during a session | NOT RUN (ends the session by design, ES-057) | — |
| Touch | PASS-HW (E-H5, earlier) | — |
| Mouse positioning | **PASS-HW**: 32,333/32,333 points exact on three displays | gated `The_cursor_lands_on_the_exact_pixel_aimed_at` |
| Mouse clicks | **PASS-HW**: 40/40 received | `es135-click-log.txt` |
| Right-click, scroll from the tablet | PASS-AUTO; NOT RUN on hardware | `HostInputMouseTests` |
| Bring windows back | PASS-AUTO; button shown in the app; NOT pressed on hardware | — |
| Disconnect / reconnect | PASS-HW (earlier and this mission) | — |
| Sleep/wake, long session | NOT RUN | — |
| Display removal / driver recovery | Observed once: the virtual display disappeared and the owner reconnected it; ExtendSpace kept reading the layout correctly afterwards. Deliberate test NOT RUN | — |

## Linux

| Check | Result | Evidence |
| --- | --- | --- |
| Fresh install, upgrade, uninstall (data kept) | **PASS-LAB** | `es-pkg` run log in the mission report |
| Launch (Avalonia UI renders, lists X11 monitors, explains missing adb) | **PASS-LAB** | `ui-home.png` |
| Capture + H.264 (x264) of an X region; first unit is a keyframe; forced keyframe in ≤ 1 s; encoding off/on | **PASS-LAB** | `LabTests` |
| Wi-Fi: add by address, pair with the real tablet (codes matched 988342) | **PASS-LAB** (real tablet) | `tablet-pair-code.png`, `e2e-wifi-log.txt` |
| Mirror over Wi-Fi to the real tablet | **PASS-LAB** (real tablet): 1920 × 1080 at 60 fps configured, first keyframe 77 ms, first picture 206 ms | `tablet-linux-mirror-wifi.png` |
| Mouse (XTest) from the real tablet | **PASS-LAB**: swipe end landed at (1379, 798), expected (1380, 799) | `e2e-wifi-log.txt` |
| Disconnect | **PASS-LAB** | `e2e-wifi-log.txt` |
| mDNS discovery of the real tablet | **PASS-HW** (run from Windows on the LAN; WSL's NAT blocks multicast) | gated `The_waiting_tablet_is_found_on_the_real_network` |
| USB | NOT RUN (no USB in WSL) | — |
| GNOME Wayland: Mirror, Extend (portal VIRTUAL), Touch, Mouse | **OWNER** (hardware pending) | — |
| KDE Plasma Wayland: same | **OWNER** | — |
| Real X11 desktop: Extend (RandR), window manager behaviour | **OWNER** (experimental) | — |
| Sleep/wake, capture recovery, compositor restart, long session | NOT RUN | — |

### Owner procedure for a GNOME or KDE machine

1. Install per [install-linux.md](../beta/install-linux.md); start ExtendSpace; About shows "(Wayland)".
2. Pair over Wi-Fi (or add by address); Mirror: allow sharing in the dialog; the tablet shows the screen.
3. Mode Extend → Connect: the dialog offers a **virtual** display; Display settings shows a new monitor;
   drag a window onto it; it appears on the tablet. Disconnect: the monitor disappears.
4. Input Touch: tap and drag on the tablet act on the virtual display only. Input Mouse: same as a mouse.
5. Record failures with `~/.local/share/ExtendSpace/host.log`.

## Native Linux test phases (planned)

Earlier Linux results above are **Lab** (WSL2 + Xvfb), not a native desktop. Native results are recorded
per phase, per distribution, so a distribution-specific failure is not mistaken for a general Linux one.
Fill the environment block first; a result without it does not count.

### Phase A: CachyOS (external SSD, temporary, about 2–3 days)

| Environment | Value |
| --- | --- |
| Distribution / version | |
| Kernel | |
| Desktop (KDE Plasma / GNOME / other) and version | |
| Display server (Wayland / X11) | |
| xdg-desktop-portal and backend versions | |
| PipeWire, GStreamer versions; H.264 encoder used (Diagnostics shows it) | |
| GPU and driver | |
| ExtendSpace commit / package | |

| # | Check | Result (PASS / FAIL / NOT RUN) | Notes, log excerpt |
| --- | --- | --- | --- |
| A1 | Install from the tarball, launch; About names the session correctly | | |
| A2 | Wi-Fi discovery lists the tablet | | |
| A3 | Add by address (manual IP) | | |
| A4 | Pairing (codes match), trusted device listed; Forget works | | |
| A5 | Mirror over Wi-Fi: consent dialog, picture, 10-minute stability | | |
| A6 | Display selection (X11: list; Wayland: dialog) | | |
| A7 | Extend: virtual display created, window moved onto it, removed on disconnect | | |
| A8 | Mouse: tap, drag, right-click (hold), two-finger scroll | | |
| A9 | Touch (Wayland portal): tap, drag; nothing on other displays | | |
| A10 | USB (adb installed, device authorised): Mirror | | |
| A11 | Resolution and refresh (Diagnostics: stream size, frames per second) | | |
| A12 | Disconnect / reconnect; tablet "Wait again" | | |
| A13 | Permission handling: decline the dialog → clear message; allow → works | | |
| A14 | Logs (`~/.local/share/ExtendSpace/host.log`) and Diagnostics figures present | | |
| A15 | Crash recovery: kill the app mid-session; tablet ends cleanly; restart and reconnect | | |
| A16 | Uninstall (data kept; `--purge` removes it) | | |

### Phase B: Ubuntu (same SSD, reinstalled; primary validation environment afterwards)

Same environment block and checks B1–B16 as A1–A16. Record Ubuntu's results here only; compare with
Phase A afterwards to separate distribution-specific problems from general ones.

| # | Result | Notes |
| --- | --- | --- |
| B1–B16 | NOT RUN | |
