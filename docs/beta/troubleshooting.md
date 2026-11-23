# ExtendSpace beta: troubleshooting

Find what the PC's Home page or the device says, and do what the right-hand column says.

| You see | What it means | What to do |
| --- | --- | --- |
| PC: "adb was not found" | Platform-Tools is not installed, or Windows does not know where it is | Install Platform-Tools and add it to `PATH` (installing guide, step 1). Sign out and in again |
| PC: "No Android device found" | The device is not connected, the cable is charge-only, or USB debugging is off | Use a data cable, turn on USB debugging, unplug and plug in again |
| PC: device "Allow USB debugging on the device…" | The device has not trusted this PC yet | Unlock the device; accept **Allow USB debugging?** with **Always allow** ticked. If no prompt appears, in Developer options tap **Revoke USB debugging authorisations**, then reconnect |
| PC: device "Not responding: reconnect the cable" | adb sees the device as offline | Unplug and plug in. If it persists, restart the device |
| PC: "Choose the device to connect to." | More than one device is connected | Click the right one in the list |
| PC: "ExtendSpace is not waiting on the device" | The app is not open on the device, or Wait for PC was not tapped | Open ExtendSpace on the device, tap **Wait for PC**, click Connect again |
| PC: "Connecting… Waiting for ExtendSpace on the device" for a long time | Same as above | Same as above |
| PC: "The display you chose … is no longer connected" | The monitor was unplugged or turned off | Choose another display |
| PC: "The display could not be captured…" | The monitor changed resolution, or was disconnected, during the session | Connect again |
| PC: "No new picture from Windows for … This is normal…" | Nothing on that screen is changing | Nothing. Move a window to check. If the picture really is stuck while something plays, see known issues (full-screen video) |
| PC: "The USB connection is not keeping up" | The cable or USB port is too slow | Use another cable or port, preferably directly on the PC |
| Device: "Reconnecting" | The cable was pulled or the PC app stopped answering | Reconnect the cable within 30 s; otherwise the session ends |
| Device: "Session ended" after you left the app | The app was in the background for more than 30 s | Tap **Wait again**, then Connect on the PC |
| Device: "This device has no video decoder…" | The device cannot decode the stream | Report it, with the device model (bug report template) |
| Black screen on the device, PC says Connected | The device is receiving but not showing | Wait a few seconds (ExtendSpace asks it to restart the picture). If it stays black, press Back and connect again, and report it |

## Collecting information for a report

- **PC:** open **Diagnostics** while the problem is happening and copy the "Stream" table (the
  values can be selected).
- **Device:** with Platform-Tools: `adb logcat -d -s ExtendSpaceSession > device-log.txt`.
- Note the time, what you were doing, and the versions (Diagnostics page on the PC; About on the device).

Logs contain device names, never what was on your screen.
