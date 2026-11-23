# Input architecture: touch from the tablet to Windows

Status: designed in Phase 9 (D81–D84), implemented in Phase 11. Verification state:
[PROJECT_STATUS.md](../../PROJECT_STATUS.md). Research: [Phase 9 research](../research/phase-9-research.md),
sections 8–11.

## 1. Pipeline

```
SurfaceView (sized to the picture, D69)
  → TouchCapture (pure: MotionEvent samples → contact frames, normalised 0..65535)
  → link writer queue (same connection, client → host direction)
  → host Link reader → HostInput (validation, lifecycle, release)
  → InputMapper (picture → encoder letterbox → monitor rectangle, virtual-screen pixels)
  → Touch mode: SyntheticTouchInjector (InjectSyntheticPointerInput, PT_TOUCH, 10 contacts)
    Mouse mode: MouseGestureInterpreter → MouseInjector (SendInput)
```

Input is part of the session (it lives and dies with a streaming connection) and independent of
video (it never touches the pipeline, the queue or frames). It is identical over USB and Wi-Fi.

## 2. Wire format (protocol section 13)

**Offer.** `stream_config` gains an optional `input` object: `{"touch": true}` when the PC user has
input on. A client sends input only on a connection whose `stream_config` offered it. Older clients
ignore the field; older hosts never offer it, so they never receive input.

**Frame.** Channel 2, type 1 `touch`, flags 0, payload (big-endian):

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `sequence`: 0 for the first frame on a connection, then +1 |
| 4 | 1 | `count`: contacts in this frame, 1–10 |
| 5 | 3 | reserved, 0 |
| 8 | 8 × count | contacts |

Contact: `id` (u8, 0–31, stable while the finger is down), `phase` (u8: 0 down, 1 move, 2 up,
3 cancel), `x` (u16), `y` (u16), reserved (u16, 0; for pressure later). `x = 0` is the picture's left
edge, `65535` its right edge; likewise `y`. A frame lists **every contact that is down**, plus those
lifting in it, as Windows' injection model requires.

**Host validation:** a touch frame on a connection where input was not offered is a fatal
`unexpected_message` (it was before too). Offered but currently turned off: ignored and counted. A
count outside 1–10, a repeated id, an unknown phase or a payload of the wrong length is a fatal
`malformed_message`. A sequence not greater than the last is dropped (stale).

## 3. Coordinate mapping (D83)

Two letterboxes exist and both are removed:

1. **Tablet:** the picture is fitted into the screen (D69) and the `SurfaceView` is exactly the
   picture, so `x = round(touchX / viewWidth × 65535)`, clamped. Touches in the tablet's bars never
   reach the view.
2. **PC:** the encoded frame (e.g. 1920 × 1080) holds the monitor scaled to fit (`OutputGeometry`):
   `picture = fit(monitorW, monitorH → streamW, streamH)`. A normalised point is first a frame pixel
   `fx = x / 65535 × streamW`, then `mx = (fx − picture.X) / picture.Width × monitorW`, and the screen
   point is `monitor.Left + mx`, `monitor.Top + my`, clamped to the monitor.

**Virtual-screen origin (ES-132, found on hardware).** `InjectSyntheticPointerInput` takes points
relative to the *virtual screen's* top-left corner, which is not the main display's origin when a
display sits to its left or above it. The touch injector subtracts that origin; mouse input already
normalises over the virtual desktop. Both read metrics in physical pixels on their own thread (ES-133).

A contact that **goes down** in the PC-side bars is ignored for its whole life; a contact that moves
into them is clamped to the picture's edge. The monitor rectangle is read from Windows (per-monitor
DPI aware, physical pixels, virtual-screen coordinates) by device name at each first touch-down of a
gesture, so a monitor moved or re-arranged is followed and the main display is followed by role.

## 4. Touch mode (D82)

- One synthetic touch device, created at the first touch and destroyed when the session ends.
- Each touch frame becomes one injected frame with every active contact:
  down → `DOWN|INRANGE|INCONTACT`, move → `UPDATE|INRANGE|INCONTACT`, up → `UP` at the contact's last
  position (an extra `UPDATE` is injected first if the up arrived elsewhere), cancel → `UP|CANCELED`.
- While any contact is down and nothing was injected for 100 ms, the same frame is injected again as
  `UPDATE`, so that Windows does not cancel a held finger (press-and-hold needs it).
- Windows then supplies tap, pan-to-scroll, press-and-hold right-click and pinch in touch-aware
  applications; legacy applications get Windows' own touch-to-mouse promotion.

## 5. Mouse mode (fallback)

For applications that mishandle touch, and for PCs where synthetic touch fails:

| Gesture on the tablet | Windows receives |
| --- | --- |
| Tap (lift within 500 ms, moved < 12 px) | move, left down, left up |
| Drag (moved ≥ 12 px) | move, left down, moves, left up |
| Press and hold 600 ms without moving | right click at that point (rest of the contact ignored) |
| Two fingers moving | vertical and horizontal wheel, 120 units per 48 px of centroid movement |

Thresholds are on screen pixels of the target monitor. This is labelled "Mouse" everywhere and is
never described as touch.

## 6. Lifecycle and safety (D84)

Contacts are released (Touch: `UP|CANCELED` for each; Mouse: any pressed button up) whenever:

- the connection ends for any reason (lost, closed by either side, fatal error);
- a new stream starts on a connection (a stale connection's input cannot reach the next one: input is
  bound to the `Link` object that read it, and the dispatcher's generation changes at every stream);
- the session stops or the app closes;
- the PC user turns input off;
- the target monitor cannot be found.

On the tablet, every live contact is sent as `cancel` on `ACTION_CANCEL`, when the view is detached,
and when the activity pauses; a frame is never sent after the stream is gone. A lost `up` cannot
happen without the connection being lost (TCP), and losing the connection releases everything on the
PC; a hung tablet is detected by the 5 s keepalive.

## 7. Session screen (tablet)

- Input offered: touches on the picture go to the PC. A small translucent handle (top right) opens the
  controls (stream info, End); they hide after 4 s. **Back** first shows the controls; Back while they
  show leaves (and ends the session).
- Input not offered: unchanged (a tap shows the controls).

## 8. Limitations

- Elevated (administrator) windows and the secure desktop (UAC, lock screen) ignore injected input
  (UIPI); the host stays unelevated by design.
- No pen pressure, tilt, hover or palm rejection; a stylus acts as a finger.
- In Mirror mode the mirrored monitor is the target; touching the tablet moves the real pointer on
  that monitor.
- A resolution or arrangement change during a session: Windows cancels active contacts
  (documented behaviour of the API); the next touch maps to the new rectangle.
