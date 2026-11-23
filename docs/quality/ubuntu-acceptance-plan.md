# Ubuntu acceptance plan (Phase B)

2026-11-23. The procedure for the first native Ubuntu session on the external SSD, real Xiaomi Pad 7.
Results go into [platform-acceptance.md](platform-acceptance.md) as "Phase B" in the same row format as
Phase A. Research: [ubuntu-platform-strategy.md](../research/ubuntu-platform-strategy.md); design:
[ubuntu-host-architecture.md](../architecture/ubuntu-host-architecture.md).

## Evidence rules

- Every row records **PASS-HW / FAIL-HW / NOT RUN / BLOCKED** with the evidence path under
  `artifacts/phaseB-<date>/` (git-ignored): logs, `journalctl --user` excerpts, tablet screenshots
  (`adb exec-out screencap -p`), tracer output.
- Requested values (60 Hz, 1616 × 1080) are written as *requested*; only measured values count.
- A row that needs the owner's hands (cable pull, dialog click, sleep) is announced first.
- Baseline failures are recorded **before** any fix. Fixes get an ES id and a regression test.

## Stage 0 — environment record (save as `env.txt`)

```bash
lsb_release -a; uname -r
echo "$XDG_SESSION_TYPE $XDG_CURRENT_DESKTOP"; gnome-shell --version
lspci -nnk | grep -A3 -Ei 'vga|3d|display'
nvidia-smi --query-gpu=name,driver_version --format=csv 2>/dev/null
vainfo 2>&1 | grep -Ei 'driver|EncSlice'          # apt install vainfo
dpkg -l | grep -E 'xdg-desktop-portal|pipewire|wireplumber|gstreamer1.0|mutter|adb|platform-tools'
gst-inspect-1.0 --version
for e in pipewiresrc nvh264enc vah264enc vaapih264enc qsvh264enc x264enc h264parse; do
  gst-inspect-1.0 $e >/dev/null 2>&1 && echo "$e yes" || echo "$e no"; done
busctl --user get-property org.freedesktop.portal.Desktop /org/freedesktop/portal/desktop org.freedesktop.portal.ScreenCast AvailableSourceTypes
busctl --user get-property org.freedesktop.portal.Desktop /org/freedesktop/portal/desktop org.freedesktop.portal.ScreenCast version
busctl --user get-property org.freedesktop.portal.Desktop /org/freedesktop/portal/desktop org.freedesktop.portal.RemoteDesktop AvailableDeviceTypes
busctl --user get-property org.freedesktop.portal.Desktop /org/freedesktop/portal/desktop org.freedesktop.portal.RemoteDesktop version
dotnet --info | head -20
adb version; ls /usr/lib/udev/rules.d/ | grep -i android; id
sudo ufw status
gsettings get org.gnome.desktop.interface scaling-factor; gsettings get org.gnome.mutter experimental-features
```

Expected: Wayland, GNOME, `AvailableSourceTypes` 7 (VIRTUAL present), RemoteDesktop version ≥ 2.
**If VIRTUAL is absent, Stage 5 is BLOCKED; continue with the rest.**

Setup (owner, with sudo): `sudo apt install dotnet-sdk-10.0 gstreamer1.0-plugins-bad gstreamer1.0-plugins-ugly gstreamer1.0-pipewire adb vainfo git`
(names to confirm with `apt-cache policy`); NVIDIA: the "Additional Drivers" proprietary driver.

## Stage 1 — baseline (no fixes)

| # | Check | Pass |
| --- | --- | --- |
| B1 | `git clone`/`pull`; `git rev-parse HEAD` = the expected commit | recorded |
| B2 | `dotnet build apps/linux/... -c Debug` and Release | 0 errors; warnings recorded |
| B3 | Linux tests (`apps/linux/tests`) | counts recorded vs CachyOS 34/0/5 |
| B4 | Shared Core tests on Linux | counts vs 628/0/13 (ES-177 may flake) |
| B5 | Launch the app from the build; window appears on XWayland; text sharp at the system scale | screenshot |
| B6 | Home: Touch offered or explained; Extend text names GNOME, not Hyprland | screenshot |
| B7 | Diagnostics: desktop, input paths, adb, encoder list | screenshot |
| B8 | Tarball `install.sh` dependency check on a fresh 26.04 | lists only what is really missing |

## Stage 2 — connectivity

| # | Check | Pass |
| --- | --- | --- |
| C1 | USB: tablet authorises the host key; listed with model | listed within 5 s |
| C2 | USB Mirror start/stop; then cable pull and replug | recovers without user action; time recorded |
| C3 | Discovery with UFW **off** | tablet listed; time recorded |
| C4 | Discovery with UFW **on** (`sudo ufw enable`, restore afterwards) | record; FAIL expected per hypothesis |
| C5 | Manual address (add by IP) | tablet added, connects |
| C6 | Pairing: codes match on both; decline on PC; decline on tablet | trust stored only on accept |
| C7 | Forget on PC, then connect | asks to pair again |
| C8 | Forget on tablet, then connect | refused with plain message |
| C9 | Wi-Fi Mirror start/stop | picture; fps recorded |
| C10 | Network change: tablet Wi-Fi off/on mid-session | reconnects or ends with a clear message |

## Stage 3 — Mirror and performance

| # | Check | Pass |
| --- | --- | --- |
| M1 | First Mirror: GNOME dialog appears, pick monitor, picture on tablet | PASS |
| M2 | Second Mirror (same monitor) | **no dialog** (restore token) |
| M3 | Two monitors: pick the second; picture is the second | correct monitor |
| M4 | "Stop sharing" from GNOME's top bar | session ends with a clear message, no reconnect loop |
| M5 | Latency matrix: for each available encoder (`EXTENDSPACE_ENCODER=…`), 60 s moving content at 1080p60, tracer p50/p95, delivered fps, CPU % | table; compare to CachyOS (NVENC p95 5.5 ms) |
| M6 | Still screen 60 s, then a mouse move | update visible; latency of first change recorded |
| M7 | Pointer-only motion produces frames (26.04 damage report) | recorded; if not, try `MUTTER_DEBUG_DISABLE_HW_CURSORS=1` |
| M8 | 10-minute soak, Wi-Fi | no stall; drops recorded |

## Stage 4 — input

| # | Check | Pass |
| --- | --- | --- |
| I1 | Mouse mode: consent dialog includes remote control | dialog text recorded |
| I2 | Tap = click at the exact point (test window with crosshair) | within 2 px |
| I3 | Drag, long-press right-click, two-finger smooth scroll | each PASS |
| I4 | Touch mode offered? (AvailableDeviceTypes includes 4) | if yes: tap, drag, pinch in a touch-aware app (e.g. GNOME Maps, Firefox) |
| I5 | Decline remote control in the dialog | Mirror continues without input, UI says why |
| I6 | Second connection with input on | **no dialog** after the persistence fix; before the fix: dialog every time (baseline) |

## Stage 5 — Extend

| # | Check | Pass |
| --- | --- | --- |
| E1 | Extend over USB: virtual monitor appears in Settings → Displays | screenshot |
| E2 | Its size equals the tablet's 1616 × 1080 (requested) | measured size recorded |
| E3 | Delivered fps with moving content on it | measured |
| E4 | Drag a window onto it; maximise there | shown on tablet, fills it |
| E5 | Mouse and touch land on the virtual monitor, not physical | PASS |
| E6 | Disconnect: monitor removed; windows return | PASS |
| E7 | `kill -9` ExtendSpace mid-Extend | monitor removed within 5 s |
| E8 | Extend over Wi-Fi | as E1–E6 |
| E9 | Rearranging in Settings while connected | stream continues or ends cleanly |

## Stage 6 — packaging

| # | Check | Pass |
| --- | --- | --- |
| P1 | Build the tarball on Ubuntu (`build-package.sh`) | builds |
| P2 | Install, launcher in Activities with icon, start from launcher | PASS |
| P3 | Upgrade over the top; data kept | trusted tablets still listed |
| P4 | Uninstall; data kept unless purge | as documented |
| P5 | (If built) `.deb` install pulls dependencies via APT on a clean user | PASS |

## Stage 7 — recovery and lifecycle

Sleep/wake mid-session; tablet reboot mid-session; ExtendSpace killed mid-Mirror; log out/in then
connect (token survives); second instance refused and first window raised (ES-178 open).

## Stage 8 — acceptance verdict for Ubuntu GNOME 26.04

| Level | Requires |
| --- | --- |
| **Supported** | B, C1–C9, M1–M6, M8, I1–I3, I5–I6, E1–E6, P1–P4 PASS-HW |
| **Supported with limitations** | As above but named gaps (e.g. touch unavailable, Extend size differs) stated in UI and docs |
| **Experimental** | Mirror works; Extend or input fails |

## Stage 9 — KDE decision

Only after Stage 8 is at least "Supported with limitations". Install `kubuntu-desktop` on the same SSD
(or a second partition) and repeat C, M1–M4, I1–I4, E1–E6. Expect E2 = 1920 × 1080 on Plasma 6.6.

## Regression strategy across platforms

| Suite | Where | When |
| --- | --- | --- |
| Core tests | Windows and Linux | every Core change, both OSes |
| Windows Media/Host tests (+ gated device tests) | Windows | every shared change; before any commit touching Core |
| Linux unit + Hyprland tests | Linux (and Windows where they run) | every Linux change; Hyprland tests guard the frozen backend |
| Linux lab tests (Xvfb) | Ubuntu or WSL | X11 backend changes |
| Android JVM tests, lint, Debug/Release builds, release-APK class check | Windows | any Android or protocol change |
| Interop test (Windows host ↔ Android endpoint on JVM) | Windows | protocol/pairing changes |
| Hardware: Windows H-list, Ubuntu Phase B rows, Android on Pad 7 | owner | before a beta build |
| Portal tests | Ubuntu GNOME (owner) | portal code changes: M1–M4, I1, I6, E1, E6 minimum |
| Packaging | each OS | before a release build |
