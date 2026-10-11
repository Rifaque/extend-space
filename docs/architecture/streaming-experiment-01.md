# Experiment 01 — USB mirroring, end to end

Status: **planned. Not started. No part of this pipeline exists.**

Goal: prove the riskiest assumptions of Mirror mode with the least code — that a Windows display can
be captured, hardware-encoded to H.264, carried over ADB, hardware-decoded on Android and shown on a
`Surface`, with latency and frame rate good enough to keep going.

This is an experiment, not the product pipeline. Its code is throwaway unless it turns out clean; its
findings are the deliverable.

```
Windows display ─► capture ─► H.264 encode ─► framing ─► adb forward ─► framing ─► MediaCodec ─► Surface
```

## Boundaries and proposed APIs

| Boundary | Proposed API | Carries | Notes |
| --- | --- | --- | --- |
| Display → capture | `Windows.Graphics.Capture`: a `GraphicsCaptureItem` for a monitor, `Direct3D11CaptureFramePool.CreateFreeThreaded`, `GraphicsCaptureSession` | BGRA8 Direct3D 11 textures with a QPC timestamp (`SystemRelativeTime`) | Creating an item for a monitor without the system picker uses the `IGraphicsCaptureItemInterop` COM interface. Frame-pool size must track the monitor's resolution (`Recreate`). |
| Capture → encoder | Media Foundation H.264 encoder transform, hardware and asynchronous, given the same D3D11 device through a DXGI device manager | NV12 textures in, H.264 samples out | BGRA → NV12 conversion is needed first (D3D11 video processor). Low-latency mode on, no B-frames, long GOP, keyframe on request. |
| Encoder → framing | In-process call | One access unit, keyframe flag, timestamp | Annex B with SPS/PPS in front of every IDR. |
| Framing → transport | A duplex byte stream | Frames per the [protocol draft](../../protocol/specification/protocol.md) | For this experiment only `stream_config`, `request_keyframe`, `ping`/`pong`, `stats` and video frames are needed. |
| Transport | `adb forward tcp:<port> localabstract:extendspace`, then a TCP connection to `127.0.0.1:<port>` | Bytes | Android listens on a `LocalServerSocket`; no network socket is opened on either side. The app should accept a connection only if its peer is the ADB shell user (`LocalSocket.getPeerCredentials`); confirm this works as expected during step 2. |
| Transport → decoder | `MediaCodec` in asynchronous mode, configured from the SPS/PPS, output to a `Surface`, `KEY_LOW_LATENCY` on API 30+ | Access units with presentation timestamps | Decoder chosen with `MediaCodecList`; record whether it is hardware-backed. |
| Decoder → display | `SurfaceView` | Rendered frames | `releaseOutputBuffer(index, true)` immediately; no pacing. |

## Smallest thing to build

**Windows: a console app** (`experiments/` or a throwaway branch — not the WinUI app). Arguments:
monitor index, frame rate, bitrate. It captures, encodes, forwards the port and streams until Ctrl+C,
printing one statistics line per second.

**Android: one extra screen in the companion app**, reachable only in debug builds, containing a
`SurfaceView` and a statistics overlay. It listens on the local socket, decodes whatever arrives and
shows it.

Build it in three steps, each verifiable alone, so a failure is attributable:

1. **Capture + encode to a file.** Write the Annex B stream to `out.h264` and play it with a desktop
   player. Proves capture, colour conversion and the encoder without any Android code.
2. **File → Android.** Push that file through the socket and decode it on the device. Proves framing,
   transport and decoding with known-good input.
3. **Live.** Connect step 1's encoder to step 2's sender.

## Prerequisites

| Prerequisite | State on the development machine |
| --- | --- |
| .NET 10 SDK | **Missing** — see [setup](../development/setup.md). |
| Windows 11 with a GPU that exposes a hardware H.264 encoder | Not yet checked. The experiment must enumerate encoders and report which one it got. |
| A way to call Media Foundation and Direct3D 11 from C# | Decision needed: generated P/Invoke (CsWin32), a managed binding library, or a small native helper. Pick the one that gets step 1 working fastest; revisit afterwards. |
| `adb` | Installed (platform-tools 37.0.1) but not on `PATH`; found through `ANDROID_HOME`. |
| A physical Android device with USB debugging enabled by its owner | **None connected during this phase.** An emulator can exercise steps 2 and 3 functionally, but its decoder and its display path say nothing about real latency. The existing emulator image also has too little free storage to install the app. |
| Android app builds | Yes. |

## What counts as success

- The Android screen shows the live Windows desktop, recognisably and continuously, for ten minutes
  without a crash, a freeze or unbounded memory growth on either side.
- Unplugging the cable ends both sides cleanly; replugging and restarting works without restarting
  the apps.
- Rotating the device or backgrounding the app and returning recovers the picture (keyframe request
  after the surface is recreated).
- The run records which encoder and decoder were actually used. Until that line says "hardware",
  nothing may be described as hardware-accelerated.

No numeric target is set before the first measurement; the experiment produces the baseline that
later targets are set against.

## Measurements

| Metric | How |
| --- | --- |
| Resolution | Reported by the capture item and confirmed by the decoder's output format. |
| Capture frame rate | Count of frames delivered by the frame pool per second. |
| Encoded frame rate and bitrate | Count and bytes of access units leaving the encoder per second. |
| Encode time | QPC time from texture submitted to sample received. |
| Dropped frames (host) | Frames skipped because the send queue was full. |
| Dropped frames (client) | Gaps in `sequence`, plus decoder outputs discarded. |
| Decode time | Time from `queueInputBuffer` to the matching output buffer. |
| Transport round trip | `ping`/`pong`. |
| **Glass-to-glass latency** | Display a running millisecond counter on the Windows monitor, photograph or film the monitor and the phone together with a high-frame-rate camera, and subtract. This is the only measurement that includes capture, display scan-out and panel delay, and the only one to quote. |

Per-stage timings locate a problem; only the camera measurement says how the product feels.

## What must stay transport-independent

Written once and reused when Wi-Fi arrives:

- Frame encoding and decoding, including size limits.
- The control messages and the session sequence.
- The encoder, the decoder, and the keyframe-request logic.
- Backpressure policy.
- Statistics.

Only "how do I get a connected, authenticated byte stream" may know that ADB exists.

## Questions this experiment should answer

1. Does Windows.Graphics.Capture deliver a full-rate monitor capture without a visible capture
   border, from an unpackaged app, on Windows 11?
2. Which interop approach to Media Foundation is workable from C#?
3. What is the baseline glass-to-glass latency over ADB, and which stage dominates it?
4. Is one TCP stream over ADB adequate at the bitrates needed, or does throughput cap quality?
5. How do common Android hardware decoders behave with a long-GOP, no-B-frame stream and
   low-latency mode?

## Known product-level concern, out of scope here

ADB requires the user to enable Developer options and USB debugging. That is acceptable for an
experiment and for early adopters, and is a real usability and security cost for a general release.
Alternatives (Android Open Accessory, USB tethering with the Wi-Fi transport) should be weighed once
the pipeline itself is proven.
