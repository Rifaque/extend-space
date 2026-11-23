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

## Native Linux test phases

Earlier Linux results above are **Lab** (WSL2 + Xvfb), not a native desktop. Native results are recorded
per phase, per distribution, so a distribution-specific failure is not mistaken for a general Linux one.
Fill the environment block first; a result without it does not count.

### Phase A: CachyOS — run 2026-11-23 (native, real Pad 7)

Native results on real hardware, recorded separately from the WSL Lab results above. Baseline commit
`46f98e2`; fixes from this run are in the commit that follows it ("validate and stabilize CachyOS
support"). **The desktop is Hyprland, not KDE**: nothing here says anything about KDE or GNOME (ES-150
stays open).

| Environment | Value |
| --- | --- |
| Distribution / version | CachyOS (rolling, Arch-based), booted from the external SSD |
| Kernel | 7.3.5-2-cachyos |
| CPU | Intel Core Ultra 7 255HX |
| Desktop and version | **Hyprland 0.57.2** (Lua configuration), dwindle layout |
| Display server | Wayland (XWayland present; Avalonia runs through it) |
| Monitors | eDP-1 2560 × 1600 at 165 Hz, scale 1.6; HDMI-A-1 2560 × 1440 at 143.9 Hz, scale 1.25 |
| Portals | xdg-desktop-portal 1.22.1; backend **xdg-desktop-portal-hyprland 1.5.0** (ScreenCast, Screenshot, GlobalShortcuts, InputCapture — **no RemoteDesktop**); ScreenCast AvailableSourceTypes 7 |
| PipeWire / WirePlumber | 1.6.11 / 0.5.19 |
| GStreamer | 1.28.9: base-libs, bad, ugly, libav, plugin-va, plugin-pipewire (no gst-plugins-good: ximagesrc absent, not needed on Wayland) |
| GPU and driver | Intel Arrow Lake iGPU (i915, mesa 26.3.1, intel-media-driver 26.3.5) + NVIDIA RTX 5060 Laptop (driver 620.73.04) |
| H.264 encoders present | nvh264enc, vah264enc, x264enc (encoder used after ES-165: **NVENC**) |
| .NET | SDK 10.0.114, runtime 10.0.14 (Arch `dotnet-sdk`); Avalonia 11.3.23 |
| adb | 1.0.41 (android-tools 37.0.0p1), `android-udev` rules (uaccess; no group change, no root) |
| Network | Host wired (2.5 GbE USB adapter), tablet on Wi-Fi, same private /24 home LAN; ufw firewall active |
| Tablet | Xiaomi Pad 7 (2410CRP4CI), Android 16, 3200 × 2136, ExtendSpace 0.2.0-beta.1 (versionCode 20001) |

| # | Check | Result | Notes, evidence |
| --- | --- | --- | --- |
| A1 | Install from the tarball, launch; session named correctly | **PASS** (after fixes) | Package built on Linux with the new `build-package.sh` (12 s), SHA-256 verified; installed to a throwaway prefix; launched: "ExtendSpace 0.2.0 on Hyprland (Wayland)"; icon and menu entry installed. The PowerShell builder could not run on Linux and the installer reported `ximagesrc` missing on Wayland: fixed |
| A2 | Wi-Fi discovery lists the tablet | **PASS** | Portable mDNS through ufw: answer from the tablet in 1.1 s; "Not paired · ready to pair" |
| A3 | Add by address (manual IP) | NOT RUN | Discovery found the tablet; the address path was not exercised natively |
| A4 | Pairing (codes match), trusted device stored; Forget | **PASS** / Forget NOT RUN | Codes 070 819 matched on both sides; TLS pairing stored `trusted-devices.json`; host refused Connect before pairing ("Pair … first") |
| A5 | Mirror over Wi-Fi | **PASS** | eDP-1, 60 fps sent/received/shown; tablet render latency mean 33–42 ms (p95 41–55 ms); round trip 2.7–8.9 ms; one Wi-Fi stall recovered by itself in about 4 s; 467 mouse frames handled |
| A6 | Display selection (Wayland: dialog) | **PASS** (after fixes) | List from Hyprland's outputs, named as the portal names them (`mapping_id`); per-output restore tokens skip the dialog; a different output picked in the dialog is refused (ES-167) |
| A7 | Extend: virtual display created, window moved onto it, removed on disconnect | **PASS** (new on Hyprland) | Headless output `EXTENDSPACE-1`, **1616 × 1080 at 60 Hz** (the Pad 7's shape), right of the physical outputs, which did not move; windows moved onto it and shown on the tablet; output and its workspace removed on disconnect (600 ms) |
| A8 | Mouse: tap, drag, right-click (hold), two-finger scroll | **PASS** (owner-confirmed after fixes) | Through the compositor's virtual-pointer protocol on the shared output (no portal exists): click/drag over USB and Wi-Fi; scrolling smooth by the pixel after D107 |
| A9 | Touch | **BLOCKED by the desktop** | xdph has no RemoteDesktop portal and Hyprland no virtual-touch protocol; ExtendSpace does not use uinput. The option now says so and cannot be chosen (D106) |
| A10 | USB Mirror | **PASS** | adb authorised on the tablet; 58 fps captured/sent, 52–53 shown; render latency mean 25 ms; round trip 2.8 ms; forward removed on disconnect |
| A11 | Resolution and refresh | **PASS** | Mirror 2560 × 1600 → 1920 × 1080 stream; Extend 1616 × 1080 at 60 Hz, 60 fps end to end; first Extend after a restart is tablet-shaped (ES-162) |
| A12 | Disconnect / reconnect | **PASS** | USB cable pulled for 5 s: lost 15:04:14, streaming again 15:04:26 without action; Disconnect leaves no forward, no capture node, CPU 0.5 % |
| A13 | Permission handling (decline the dialog) | NOT RUN | — |
| A14 | Logs and Diagnostics | **PASS** (after fixes) | Diagnostics labels overlapped their values: fixed; input paths shown; exit and crash causes now logged (ES-163) |
| A15 | Crash recovery | **PARTIAL** | A failed acquisition left the Extend output until exit: fixed (ES-161), verified live (removed 5 ms after the failure); leftover outputs are removed at start (unit-tested); a killed process mid-session was not run |
| A16 | Uninstall (data kept; `--purge` removes it) | **PASS** | Upgrade over an install kept data; uninstall removed binary, menu entry and icon, kept data; `--purge` removed data |

**Measured host pipeline latency** (GStreamer latency tracer, capture → appsink, moving content on the
real PipeWire stream, 1080p60): before — videorate duplicating, VA-API first — median 80 ms, p95
537 ms, CPU 47 %; VA-API alone holds 67 ms; after (ES-165, ES-166) — **NVENC p95 5.5 ms**, 59.9 fps,
CPU 27 %. x264 zerolatency: p95 6.0 ms at 69 % CPU.

**Not run here:** Windows build and tests (not buildable on Linux; shared Core tests ran), Android build
and tests (no JDK on this machine), sleep/wake, tablet restart, host network change, decline dialog,
Forget/revocation, 10-minute soak, KDE, GNOME, X11.

### Phase B: Ubuntu (same SSD, reinstalled; primary validation environment afterwards)

Same environment block and checks B1–B16 as A1–A16. Record Ubuntu's results here only; compare with
Phase A afterwards to separate distribution-specific problems from general ones.

Superseded in detail (2026-11-23) by the staged GNOME procedure in
[ubuntu-acceptance-plan.md](ubuntu-acceptance-plan.md) (Stages 0–9); record its row ids here. Target:
Ubuntu 26.04 LTS, GNOME 50, Wayland (no Xorg session exists there).

| # | Result | Notes |
| --- | --- | --- |
| B1–B16 | NOT RUN | |
