# ExtendSpace beta: tester checklist

Tick what you tried and note anything odd. Report with the [templates](reporting.md).

| # | Try this | Expected |
| --- | --- | --- |
| 1 | Install on Windows; open the Diagnostics page | adb found, with a version |
| 2 | Connect the device without USB debugging authorised | PC says to allow USB debugging; Connect is disabled |
| 3 | Authorise, tap Wait for PC, Connect | Picture within a couple of seconds |
| 4 | Move windows, scroll a page, play a video in a window | Smooth, no smearing or blocks after a moment |
| 5 | Leave the desktop still for a minute | Picture stays; PC says "No new picture… normal" |
| 6 | Rotate the device | Picture comes back in the new orientation, not stretched |
| 7 | Press Home on the device and return within 30 s | Picture comes back |
| 8 | Pull the cable for 5 s and plug it back | Device says Reconnecting, then the picture returns |
| 9 | Click Disconnect on the PC | Device says Session ended |
| 10 | Wait again on the device, Connect again on the PC; repeat a few times | Works every time |
| 11 | Press Back on the device during a session | Both sides end the session |
| 12 | Close ExtendSpace on the PC during a session | Device says Session ended within a few seconds |
| 13 | With two monitors, choose the other one | That monitor is shown |
| 14 | Stream for 30 minutes or more | No slowdown, no growing delay, device not too hot |
| 15 | Play a video full screen in a browser | Report whether the picture freezes (known issue) |
