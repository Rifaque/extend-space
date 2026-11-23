# ExtendSpace beta: privacy

- **Your screen goes only to your device, over the cable.** ExtendSpace has no servers, accounts,
  telemetry, analytics, crash upload or update checks. Neither app connects to the internet.
- **Nothing listens on your network.** On the PC, adb's port forward exists only on the loopback
  address (`127.0.0.1`) for the length of a session, and is removed when the session ends. On the
  device, the app accepts a connection only from adb (it checks the caller is the adb shell user).
- **Nothing is recorded.** Pictures are encoded, sent and shown, and never written to disk on either
  side.
- **Logs stay local.** The PC writes diagnostic lines to the Windows debug output only; the device
  writes to Android's log (`logcat`). They contain device names, versions and timings, never screen
  content. Device serial numbers are not logged by the PC.
- **USB debugging** is an Android developer setting. While it is on and the device is unlocked, a PC
  you have trusted can do much more to the device than ExtendSpace does. Trust only your own PC, and
  turn USB debugging off when you stop testing if you prefer.
- **Permissions:** the Android app requests none. The Windows app runs without administrator rights.
