# ExtendSpace beta: known issues and limits

Version 0.2.0-beta.1. Tracked in the repository's `ISSUES.md` (IDs in brackets).

## Limits of this beta

- **Mirror only.** The device shows an existing Windows display; it does not become an extra display
  (Extend needs a signed driver, not available yet) [ES-091].
- **USB** through adb, which you install yourself [ES-063, ES-090], or **Wi-Fi** with a tablet you
  have paired (both on the same network; guest networks that isolate devices do not work). Wi-Fi is new
  and has not yet been tested on hardware [ES-121].
- **Touch input** is new and off by default: choose Touch or Mouse on the PC. Windows that run as
  administrator, and the sign-in and UAC screens, ignore it. No pen pressure, keyboard or audio
  [ES-092, ES-093, ES-124]. One device at a time [ES-094].
- **Two stream sizes:** 1920 × 1080 at 60 fps, or 1280 × 720 at 30 fps if the device cannot decode
  1080p. The picture is scaled down from larger displays and letterboxed on screens of another shape.
- **Leaving the app for more than 30 s ends the session** (no background service yet) [ES-058].
- **Changing the PC display's resolution during a session ends it**; connect again [ES-057].
- **Unsigned:** Windows and Android warn when installing [ES-060, ES-061].

## Known problems

- **Frozen picture during full-screen video in a browser.** Windows has been seen to stop delivering
  new pictures of a display while a browser plays video full screen; the cause is unknown. ExtendSpace
  now shows "No new picture from Windows for …" on the PC, but it cannot tell this from a still
  screen, so it does not fix it. Exiting full screen usually helps [ES-050].
- **Tested on very little hardware:** one Windows 11 PC and one Android tablet (Xiaomi Pad 7). Other
  devices, especially other decoders, may behave differently [ES-053, ES-054]. Please report your
  device model even when everything works.
- A recovery from a short freeze of the device may still take up to half a second in some cases
  [ES-056].
