# ExtendSpace beta: installing on Android

## What you need

Android 10 or later. Tested so far on one device: a Xiaomi Pad 7 (Android tablet). Phones should
work but have not been tried.

## Install

The beta is an APK file, not a Play Store app.

- **From the PC (easiest if Platform-Tools is set up):** connect the device with USB debugging on
  and run `adb install -r ExtendSpace-<version>.apk`.
- **On the device:** copy the APK to it, open it in a file manager and allow "Install unknown apps"
  for that app when asked. Some devices (Xiaomi, for example) also need **Install via USB** turned
  on in Developer options.

ExtendSpace asks for **no permissions**. It does not use the internet. It shows only what your PC
sends over the cable.

## Use

1. Open ExtendSpace and tap **Wait for PC**. A full-screen page says "Waiting for your PC".
2. On the PC, choose the device and click **Connect**. The picture appears.
3. Tap the picture to show **End session** for a few seconds.
4. **Back** ends the session. Rotating the device is fine; the picture follows.
5. If you switch to another app or turn the screen off for more than 30 seconds, the session ends;
   open ExtendSpace again and tap **Wait for PC**.

When the PC ends the session the device shows **Session ended** with **Wait again** and **Close**.
If the connection fails, it says why, with **Try again**.

## Remove

Long-press the app icon → Uninstall. You can also turn USB debugging off again afterwards.
