# Extend mode: hardware acceptance

Status legend: **PASS-HW** run on hardware in this mission; **PASS-PC** PC-only integration (no
tablet); **PASS-AUTO** automated tests only; **NOT RUN**; **OWNER** needs the owner. Results are filled
in the [results table](#results) as they are obtained; nothing is marked passed that was not run.

Preconditions: the Virtual Display Driver installed (VDD Control → Install Driver; one admin prompt),
the virtual monitor part of the desktop (Settings → System → Display → *Extend these displays*),
ExtendSpace on both devices, Mode = Extend on the PC.

## Checklist

| ID | Area | Steps | Pass |
| --- | --- | --- | --- |
| E-H1 | Display | After installing VDD: Display settings lists a new display; ExtendSpace's Extend card names it and its size | Identified as VDD (EDID MTT/0x1337 or friendly name); physical monitors unchanged |
| E-H2 | Capture | Mode Extend, USB, Connect | The tablet shows the **virtual** display's desktop (empty wallpaper), not a physical one |
| E-H3 | Desktop | Drag a window onto the virtual display; change its resolution in Display settings, reconnect | The window appears on the tablet; the new size streams (session ends on the change, ES-057) |
| E-H4 | Transports | E-H2 over Wi-Fi with the paired tablet | Same |
| E-H5 | Input | Input = Touch, tap and drag on the tablet | Lands on the virtual display; nothing on the physical monitors |
| E-H6 | Lifecycle | Disconnect; quit ExtendSpace; disable the virtual display in Display settings; reconnect | Clean end each time; Extend card says what to do; no change to physical monitors |
| E-H7 | Removal | VDD Control → uninstall (or Device Manager) | Virtual monitor gone; ExtendSpace reports "needs the driver"; physical layout intact |

## Results

Run 2026-11-23 on the development PC (RTX 5060 Laptop + Intel, Windows 11, 125 % scaling, main
monitor Acer VG271U M3 2560 × 1440, laptop panel 2560 × 1600 to its left) and the Xiaomi Pad 7, with
VDD 25.7.23 (driver 11.30.4.434) installed by the owner from VDD Control. Evidence (git-ignored):
`artifacts/phase10-11-20261123/` (`eh2-tablet-extended-usb.png`, `eh3-window-on-virtual.png`,
`extend-device-log.txt`, `extend-touch-log.txt`).

| ID | Result | Evidence |
| --- | --- | --- |
| E-H1 | **PASS-HW** | Driver present 55 s after the owner's install; ExtendSpace's reader: `\.\DISPLAY9` "VDD by MTT", EDID MTT/0x1337, active, 800 × 600 at (2560, 0), right of the main monitor; Windows extended the desktop onto it by itself; physical monitors unchanged before and after. Extend card: "The tablet will be Display 9, 800 x 600". **Finding:** VDD reports output technology **HDMI (5)**, not INDIRECT_VIRTUAL: only the EDID rule identifies it |
| E-H2 | **PASS-HW** (USB) | "Extending over USB"; stream 800 × 600 (the monitor's shape, D89), first keyframe 31 ms and first picture 227 ms after the stream started; the tablet showed the virtual display's own empty desktop and taskbar, not the main monitor |
| E-H3 | **PASS-HW** (window) / NOT RUN (resolution change) | A test window placed at (2610, 50) on the virtual display appeared on the tablet; changing the virtual display's resolution was not tried (it would need the owner's Display settings) |
| E-H4 | **PASS-HW** | "Extending over the local network" with the paired tablet; first picture 116 ms; 30 fps, the VDD mode's own 30 Hz |
| E-H5 | **PASS-HW** (Touch mode, native synthetic touch) | Tap at the picture's centre landed at exactly (2960, 300), the virtual display's centre; a swipe dragged from (2887, 258) to (3044, 337), its expected end; every point inside the virtual display; over Wi-Fi a tap landed at (2988, 308) as computed. Mouse mode on the virtual display: NOT RUN |
| E-H6 | Partly: Disconnect PASS-HW | Disconnect ended both sides ("Session ended"); quitting ExtendSpace and disabling the virtual display: NOT RUN |
| E-H7 | NOT RUN | Uninstalling VDD is the owner's choice; VDD is still installed |
