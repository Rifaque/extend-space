# Experiment 01 — USB mirroring, end to end

Status: **all three steps have run, and a reliability phase after them; one success criterion
is still unmet and one fault is unexplained.** In short: a controlled 1080p60 stream ran for ten
minutes without losing a frame, a pulled USB cable is recovered from automatically, and the lag
between the two screens was measured once with a camera at about 46 ms. Not done: rotation, and an
explanation for the capture API occasionally delivering almost nothing while a browser plays video
full screen. See [Phase 2.4](#phase-24--reliability-and-latency-validation). The steps in order: a real
Windows display was captured, encoded to H.264 by a hardware encoder and written to a `.h264` file
that an independent decoder reads without error; see [Step 1](#step-1--capture-and-encode-to-a-file).
Files from that pipeline were copied to a physical Android tablet and decoded and shown by its
hardware decoder; see [Step 2a](#step-2a--decode-a-captured-file-on-android). Then the live desktop
was streamed over USB through `adb forward` and shown on the tablet, for up to three minutes at a
time, with frame counts and a latency figure measured as far as the tablet's display surface; see
[Step 3](#step-3--live-stream-over-adb-forward). What step 3 left open (a ten-minute run,
glass-to-glass latency, a controlled 60 fps source, a cable actually pulled) is what
[Phase 2.4](#phase-24--reliability-and-latency-validation) then did.

Goal: prove the riskiest assumptions of Mirror mode with the least code — that a Windows display can
be captured, hardware-encoded to H.264, carried over ADB, hardware-decoded on Android and shown on a
`Surface`, with latency and frame rate good enough to keep going.

This is an experiment, not the product pipeline. Its code is throwaway unless it turns out clean; its
findings are the deliverable.

```
Windows display ─► capture ─► H.264 encode ─► framing ─► adb forward ─► framing ─► MediaCodec ─► Surface
└──────────── step 1 ───────────────────┘                                        └────── step 2a ──────┘
                                        └────────── .h264 file, copied with adb push ──────────┘
└──────────────────────────────── step 3: live, all of it exists ──────────────────────────────┘
```

## Boundaries and proposed APIs

Every row has now been implemented in the experiment, as proposed, across steps 1 to 3. The notes
are the original proposals; where a step found otherwise, its section says so.

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
   **Done; see below.**
2. **File → Android.** Push that file through the socket and decode it on the device. Proves framing,
   transport and decoding with known-good input. **Done in two parts, not as planned:** the file was
   decoded on a device from storage ([step 2a](#step-2a--decode-a-captured-file-on-android)); the
   socket and the framing were then proven with live video in step 3, without a file replay.
3. **Live.** Connect step 1's encoder to step 2's sender. **Done; see
   [step 3](#step-3--live-stream-over-adb-forward).**

## Step 1 — capture and encode to a file

Everything in this section describes code that exists and runs that happened. Where something was
not measured or not tested, it says so.

### What was built

A console application, `apps/windows/experiments/ExtendSpace.Experiments.CaptureEncode`, which builds
`ExtendSpace.CaptureEncode.exe`. It is in `ExtendSpace.slnx` so that it keeps building, and it
references nothing from `ExtendSpace.Core` or `ExtendSpace.Host`; neither of those references it.
The production app is unchanged and still cannot connect, capture or stream.

```
monitor ─► Windows.Graphics.Capture ─► newest-frame slot ─► D3D11 video processor ─► H.264 encoder MFT ─► .h264 file
           BGRA8 texture, timestamp     (holds one frame)    BGRA → NV12 on the GPU    NV12 texture in,      Annex B,
           free-threaded frame pool                           BT.709, 16–235            access unit out       no container
```

| Part | File | Does |
| --- | --- | --- |
| Capture | `Capture/MonitorCapture.cs` | Creates a capture item for a monitor through `IGraphicsCaptureItemInterop`, runs a three-buffer free-threaded frame pool, and keeps only the newest accepted frame. Counts every frame it is given. |
| Pacing | `Measurement/FramePacer.cs` | Decides which delivered frames go on, so that accepted frames average the target rate. |
| Device | `Graphics/GraphicsDevice.cs` | One Direct3D 11 device on the default adapter, shared by capture, conversion and the encoder through a DXGI device manager. |
| Conversion | `Graphics/Nv12Converter.cs`, `Graphics/OutputGeometry.cs` | `ID3D11VideoProcessor` blit from the captured BGRA texture into an NV12 texture. Scales in the same pass when `--output-size` is given (added in step 2a). |
| Input surfaces | `Encoding/EncoderInputPool.cs` | `IMFVideoSampleAllocatorEx`: a bounded pool (4 to 16) of NV12 textures wrapped as samples, because an asynchronous encoder holds each frame until it is done with it. |
| Encoder choice | `Encoding/EncoderCatalog.cs`, `ExperimentRunner.cs` | Enumerates hardware encoders *for the capture adapter* (`MFTEnum2` with `MFT_ENUM_ADAPTER_LUID`), then software encoders, and opens the first that accepts the configuration. Every rejected one is listed in the report with its reason. |
| Encoder | `Encoding/H264Encoder.cs` | Drives the transform directly (`ProcessInput`/`ProcessOutput`), for both asynchronous (event-driven) and synchronous transforms. |
| Acceleration verdict | `Encoding/EncoderAcceleration.cs` | Hardware, software or unknown, from evidence only. |
| Output | `Bitstream/ElementaryStreamWriter.cs`, `Bitstream/AnnexB.cs`, `Bitstream/H264StreamStatistics.cs` | Writes encoder output samples back to back and scans their NAL units on the way. Refuses to overwrite a file; deletes the file if nothing was written. |
| Report | `RunReport.cs` | Prints what was measured, what was only requested, and what is unavailable. |

Threads: capture callbacks arrive on Windows thread-pool threads and only swap the newest frame into
the slot; one pipeline thread converts, submits and writes; for an asynchronous encoder one more
thread turns the transform's blocking event stream into a queue. Shutdown is in reverse order of
setup and runs on every exit path: stop capture, drain the encoder, shut the transform down, release
the pool, converter and device, close the file, shut Media Foundation down.

### Choices and the evidence for them

| Choice | Why | Evidence on this machine |
| --- | --- | --- |
| `Windows.Graphics.Capture` | The API the plan proposed; delivers GPU textures and timestamps without a driver. Desktop Duplication was not needed, so it was not tried. | `GraphicsCaptureSession.IsSupported()` is true. Frames arrived as often as every 6.06 ms (the panel's 165 Hz refresh) when the rate limit allowed it. |
| Media Foundation encoder transform, used directly | Supported Windows API with both a hardware and a software H.264 encoder behind one interface. The Sink Writer was not used because it writes containers, not an elementary stream. | Media Foundation lists three H.264 encoders here; see [Encoder identity](#encoder-identity-and-acceleration-evidence). |
| CsWin32 (generated P/Invoke) for Direct3D 11 and Media Foundation | Answers "which interop approach": build-time only, nothing extra ships, declarations come from Microsoft's metadata rather than being hand-written. | Worked. Notes in [Interop findings](#interop-findings). |
| D3D11 video processor for BGRA → NV12 | Keeps the frame on the GPU from capture to encoder. | Queueing the conversion costs about 0.3 ms of CPU per frame (median 0.30 ms). |
| Same pipeline feeds the software encoder | One code path to maintain. The software encoder reads the same GPU-backed sample; Media Foundation copies it to system memory when the encoder locks the buffer. | Software output decodes to a correct picture (no stride or colour-plane corruption seen). |
| 1 ms timer resolution while capturing | See [the timer finding](#finding-the-hardware-encoder-is-held-to-the-windows-timer-tick). | Dropped frames fell from 37–45 % of delivered frames to 0–0.2 %. |

### Prerequisites and how to run

- Windows 11 (the project's floor is build 22000; this was run on build 26300 only).
- The .NET 10 SDK, installed normally. Nothing else: the Direct3D and Media Foundation declarations
  are generated at build time from the `Microsoft.Windows.CsWin32` NuGet package.
- To validate the output: `ffmpeg`/`ffprobe` on `PATH` (already installed on the development
  machine; not installed by this work).

```powershell
cd apps/windows
dotnet build ExtendSpace.slnx

# What can be captured and which encoders exist
dotnet run --project experiments/ExtendSpace.Experiments.CaptureEncode -- list

# Ten seconds of the primary monitor, hardware encoder if there is one
dotnet run --project experiments/ExtendSpace.Experiments.CaptureEncode -- capture

# All options
dotnet run --project experiments/ExtendSpace.Experiments.CaptureEncode -- --help
```

| Option | Default | Meaning |
| --- | --- | --- |
| `--monitor <index>` | primary monitor | Monitor as numbered by `list`. |
| `--duration <seconds>` | `10` | 0.5 to 600. The run always ends. |
| `--max-frames <count>` | none | Also stop after this many frames have been submitted to the encoder. |
| `--fps <rate>` | `60` | Target frame rate, 1 to 240. |
| `--output-size <WxH>` | the monitor's own size | Encode at this size, for example `1920x1200`. Both numbers even, 128 to 4096. The picture is scaled on the GPU to fit, keeping its shape; any space left over is black. Added in step 2a. |
| `--bitrate <bits>` | `12M` | Target bitrate; `k` and `M` suffixes. |
| `--gop-seconds <seconds>` | `2` | Keyframe interval. Converted to frames at the target rate. |
| `--profile <name>` | `high` | `baseline`, `main` or `high`. |
| `--encoder <choice>` | `auto` | `auto` (hardware, then software), `hardware`, `software`. |
| `--output <path>` | `.\extendspace-capture-<UTC timestamp>.h264` in the current directory | Must end in `.h264`. An existing file is never overwritten. |
| `--no-cursor` | cursor captured | Leave the mouse cursor out. |
| `--no-border` | Windows' default | Ask Windows for capture without its on-screen border, through `GraphicsCaptureAccess.RequestAccessAsync`. |
| `--default-timer` | 1 ms requested | Leave the Windows timer resolution alone, for comparison. |

Ctrl+C or Ctrl+Break stops early; the encoder is still drained and the file closed. Exit codes: 0
success, 1 the run failed, 2 invalid arguments.

The output is a recording of whatever is on the screen. `*.h264` and `artifacts/` are git-ignored
for that reason.

### Capture and encoding configuration

| Setting | Value | Applied through | Confirmed how |
| --- | --- | --- | --- |
| Pixel format in | BGRA8 from capture; NV12 into the encoder | Frame pool format; video processor | Decoded pictures are correct |
| Colour | BT.709 matrix, limited range (16–235) | Video processor colour spaces; `MF_MT_YUV_MATRIX` and related attributes on the input type | Range is in the hardware encoder's stream; the matrix is in neither encoder's stream (see limitations) |
| Resolution | The monitor's own, rounded down to even numbers, unless `--output-size` asks for another | — | 2560 × 1600 in the SPS |
| Profile | High | `MF_MT_MPEG2_PROFILE` | `profile_idc` 100 in the SPS; `--profile baseline` gave 66 |
| Level | Not set; the encoder chooses | — | 5.1 at 60 fps, 5.2 at 165 fps |
| Rate control | CBR at the target bitrate | `CODECAPI_AVEncCommonRateControlMode`, `CODECAPI_AVEncCommonMeanBitRate` | Accepted and read back by both encoders. Measured output stayed under the target. |
| Low-latency mode | On | `CODECAPI_AVLowLatencyMode`, `MF_LOW_LATENCY` | Accepted and read back |
| B-frames | None | `CODECAPI_AVEncMPVDefaultBPictureCount = 0` | ffprobe: `has_b_frames=0`; only I and P pictures |
| Keyframe interval | 120 frames | `CODECAPI_AVEncMPVGOPSize` | I pictures at frames 1, 121, 241 … 1561 of the reference run |
| Parameter sets | In-band | Encoder default | SPS and PPS precede every IDR picture, from both encoders |
| Sample times | Capture timestamps, starting at zero | `IMFSample::SetSampleTime` | Not present in the file: a raw elementary stream carries no timestamps |
| Capture rate limit | `GraphicsCaptureSession.MinUpdateInterval` = one frame interval, then the pacer | — | See results |
| Capture frame pool | 3 buffers | — | — |

"Accepted and read back" means `ICodecAPI::SetValue` succeeded and `GetValue` returned the value. It
does not by itself prove the encoder honours it; where the stream shows the effect, the last column
says so.

### Results

Run on 2026-10-12. These are single runs on one machine, with whatever the person using it had on
screen. They establish that the pipeline works and what it did here; they are not a benchmark.

**Machine and conditions**

| | |
| --- | --- |
| OS | Windows 11 Home, build 26300, x64 |
| CPU | Intel Core Ultra 7 255HX, 20 logical processors |
| GPU | Intel(R) Graphics, driver 32.0.101.8331 (the adapter driving the display) |
| Display | One monitor, `\\.\DISPLAY1`, 2560 × 1600 at 165 Hz |
| Power | **On battery** (85 %), "High performance" power scheme |
| Build | Debug, .NET SDK 10.0.401 |
| Screen content | **Not controlled.** A web browser playing video and being used normally. The capture API only delivers a frame when the screen changes, so the capture rate below is as much a property of the content as of the pipeline. |

**Reference run** — `capture --duration 30 --no-border`, output
`artifacts/experiment-01/hw-1ms-30s.h264`:

| | Value | Kind |
| --- | --- | --- |
| Source | `\\.\DISPLAY1`, 2560 × 1600 | measured |
| Encoder | Intel® Quick Sync Video H.264 Encoder MFT — hardware | identified, see below |
| Duration | 30.000 s (requested 30 s); 29.982 s between first and last delivered frame | measured |
| Frames delivered by the capture API | 1617 | measured |
| Skipped by the pacer | 0 | measured |
| Dropped because the encoder was not ready | 2 | measured |
| Dropped for lack of an input surface | 0 | measured |
| Submitted to the encoder | 1615 | measured |
| Encoded and written | 1615 | measured |
| Average capture rate | 53.90 fps | delivered ÷ duration |
| Average encoded output rate | 53.83 fps | encoded ÷ time to the last encoded frame |
| Interval between delivered frames | median 18.18 ms, p95 18.18 ms, max 36.37 ms | capture API timestamps |
| File size | 38,878,208 bytes (37.077 MiB) | measured |
| Average bitrate | 10.367 Mbit/s against a 12 Mbit/s target | size ÷ duration |
| Stream | 14 IDR + 1601 non-IDR access units, 0 malformed; SPS and PPS ahead of all 14 IDR pictures; High profile, level 5.1 | scanned while writing |
| Encoder turnaround | median 10.80 ms, mean 11.20, p95 16.74, max 30.04 | frame handed to the encoder → encoded sample received |
| Conversion call | median 0.30 ms, p95 0.41, max 9.82 | CPU side only; the GPU's time is not measured |
| Drain at the end | 0.006 s | measured |
| Processor time, this process | 2.578 s over 30.007 s = 8.6 % of one logical processor (0.43 % of all 20) | measured |
| Peak working set | 198.2 MiB | measured |

The capture rate is 53.9 fps, not 60, and the reason is visible in the intervals: with a 16.67 ms
minimum update interval on a 165 Hz panel, Windows delivers on the next refresh after that, every
third one, 18.18 ms apart — 55 fps at most. The same rounding gave 36.36 ms (27.5 fps) for
`--fps 30`. Reaching an exact 60 fps on this panel would need a different limit or none; that
trade-off was not explored here. Step 3 changed it; see
[Changes to capture pacing](#changes-to-capture-pacing).

**All runs.** Every run used `--no-border`, 2560 × 1600, 12 Mbit/s CBR, High profile and a 120-frame
keyframe interval. "Dropped" is frames replaced before the encoder was ready for them. Files are in
`artifacts/experiment-01/` with their full reports beside them.

| Run | Extra options | Encoder | Timer | Duration s | Delivered | Dropped | Encoded | Capture fps | Encoded fps | Turnaround ms, median / p95 | Size, bytes | Mbit/s | CPU, % of one LP | Peak MiB |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: |
| `hw-1ms-30s` | `--duration 30` | Quick Sync | 1 ms | 30.000 | 1617 | 2 | 1615 | 53.90 | 53.83 | 10.80 / 16.74 | 38,878,208 | 10.367 | 8.6 | 198.2 |
| `hw-1ms-a` | — | Quick Sync | 1 ms | 10.000 | 525 | 1 | 524 | 52.50 | 52.39 | 10.00 / 14.86 | 12,012,802 | 9.610 | 11.2 | 196.1 |
| `hw-default-a` | `--default-timer` | Quick Sync | default | 10.008 | 515 | 230 | 285 | 51.46 | 29.16 | 15.31 / 30.91 | 6,905,878 | 5.520 | 9.7 | 193.1 |
| `hw-1ms-b` | — | Quick Sync | 1 ms | 10.000 | 53 | 0 | 53 | 5.30 | 5.64 | 12.96 / 18.30 | 737,020 | 0.590 | 3.1 | 192.7 |
| `hw-default-b` | `--default-timer` | Quick Sync | default | 10.011 | 199 | 73 | 126 | 19.88 | 12.66 | 15.23 / 30.74 | 2,750,679 | 2.198 | 5.6 | 193.8 |
| `hw-1ms-fps165` | `--fps 165 --duration 8` | Quick Sync | 1 ms | 8.001 | 677 | 237 | 439 | 84.62 | 54.85 | 10.67 / 17.71 | 3,639,442 | 3.639 | 11.3 | 195.8 |
| `sw-1ms` | `--encoder software` | Microsoft software | 1 ms | 10.001 | 189 | 0 | 188 | 18.90 | 18.81 | 12.18 / 15.57 | 3,502,703 | 2.802 | 75.6 | 280.6 |
| `sw-1ms-15s` | `--encoder software --duration 15` | Microsoft software | 1 ms | 15.007 | 818 | 2 | 816 | 54.51 | 54.38 | 12.63 / 16.15 | 20,051,900 | 10.689 | 217.5 | 284.8 |

Reading the table:

- Frames are fully accounted for in every run: delivered = skipped + dropped + discarded at stop +
  submitted, and submitted = encoded. `hw-1ms-fps165` and `sw-1ms` each discarded one frame that was
  waiting when the run stopped.
- `hw-1ms-b`, `hw-default-b` and `sw-1ms` caught the screen while it was mostly still (one gap of
  6 s in `hw-1ms-b`). Their low frame rates say that about the screen, not about the pipeline. This
  is why rates from different runs must not be compared with each other.
- Neither encoder filled the 12 Mbit/s target; both produced less for the content they were given.
- In `hw-1ms-fps165` the capture API delivered 84.6 fps and the hardware encoder took 54.9 fps of
  it. An earlier 8 s run of the same command took 59.5 of 70.7 fps. Those two figures are the only
  measurements of this encoder's throughput when it is the bottleneck, at this resolution, on
  battery, with the GPU also decoding and presenting the browser's video.
- The software encoder kept up with a 54.5 fps screen, at 2.2 logical processors' worth of CPU,
  against 0.09 for the hardware path. The hardware path's CPU figure leaves out the GPU.

#### Finding: the hardware encoder is held to the Windows timer tick

With Windows' default timer resolution, the Intel encoder transform's events came in steps of the
15.6 ms timer tick. A temporary trace (since removed) showed the "have output" event arriving about
15 or 30 ms after a frame was submitted, and the "need input" event about 15.6 ms after the output
was collected, so each frame cost 30 to 45 ms end to end. With `timeBeginPeriod(1)` in effect for
the process, the same trace showed output after about 7 to 11 ms and the input request 1 to 2 ms
later.

The paired runs in the table show the effect in the frame counts, alternating the setting:

| | 1 ms timer | Default timer |
| --- | --- | --- |
| Dropped ÷ delivered | 1 of 525 (0.2 %), 0 of 53, 2 of 1617 (0.1 %) | 230 of 515 (44.7 %), 73 of 199 (36.7 %) |
| Turnaround, median | 10.00, 12.96, 10.80 ms | 15.31, 15.23 ms |

An earlier set of four alternating 10 s runs, made before a cosmetic change to the report and not
kept, gave the same picture: 0 of 441 and 5 of 402 dropped with the 1 ms timer, 180 of 438 and 146
of 446 without.

So the experiment requests a 1 ms timer for its own process while it captures, by default, and says
so in the report. `--default-timer` turns that off. The request writes no setting and is released
when the run ends. What is behind the dependence inside the transform was not investigated; the
software encoder, which is called synchronously, was not compared with and without it.

Two statements about `timeBeginPeriod` are from memory of Microsoft's documentation and could not be
checked when this was written, because the documentation site was unreachable from the machine:
that since Windows 10 version 2004 the request no longer changes the timer for other processes, and
the Windows 11 behaviour described under [limitations](#known-limitations-and-unresolved-risks).
Confirm both before relying on them.

An informal check of `CODECAPI_AVEncCommonQualityVsSpeed` at 0 and at 100, one short run each with
the default timer, left the median turnaround at about 15 ms. It is not exposed as an option.

### Validation

| Check | Method | Result |
| --- | --- | --- |
| File is an Annex B elementary stream | NAL units scanned as they were written; `ffprobe` identifies the file as raw `h264` with no container | Every output sample starts with a start code and holds one picture; 0 malformed in every run |
| Stream starts decodably | Same scan | First access unit is an IDR picture preceded by SPS and PPS, in every run |
| **Decodes completely** | `ffmpeg -v error -xerror -framerate <fps> -i <file> -f null -` (software decoder, stops on the first error), ffmpeg build 2026-01-26-git-fe0813d6e2 | All 8 files: exit code 0, no messages |
| Nothing lost between encoder and file | `ffprobe -count_frames` against the experiment's own "encoded and written" count | Equal in all 8 files (1615, 524, 285, 53, 126, 439, 188, 816) |
| Decodes on a GPU decoder | `ffmpeg -hwaccel d3d11va -hwaccel_output_format d3d11 …` on `hw-1ms-30s` and `sw-1ms-15s`; ffmpeg's log shows the decoder context in the `d3d11` pixel format | 1615 and 816 frames decoded, 0 decode errors |
| Stream parameters | `ffprobe -show_entries stream=…` | `profile=High`, 2560 × 1600, `yuv420p`, `has_b_frames=0`, progressive, level 51 (52 for the 165 fps run) |
| Picture is the real desktop | One frame from each of a hardware and a software run decoded to PNG and looked at | The desktop as it was: browser window, readable text, taskbar, cursor; colours plausible; no tearing or corruption |

Without `-framerate`, ffmpeg printed "non monotonically increasing dts" messages for the 165 fps
file. They come from ffmpeg inventing timestamps for a raw stream and disappear when the rate is
given; they are not decode errors.

Not verified:

- Playback in an interactive player. The plan said "play it with a desktop player"; the decode and
  the inspected frames stand in for that. `ffplay -framerate 60 <file>` would do it.
- Colour accuracy, beyond looking right.
- Any Android decoder. That is step 2.

### Encoder identity and acceleration evidence

`list` on this machine:

| Encoder | Listed by Media Foundation as | Used |
| --- | --- | --- |
| Intel® Quick Sync Video H.264 Encoder MFT, CLSID `{4be8d3c0-0515-4a37-ad55-e4bae19af471}`, vendor `VEN_8086` | Hardware, for the capture adapter (it appears twice in the enumeration) | Yes, by default |
| NVIDIA H.264 Encoder MFT, CLSID `{60f44560-5a20-4857-bfef-d29773cb8040}`, vendor `VEN_10DE` | Hardware, but not for the capture adapter | No; not tried |
| H264 Encoder MFT, CLSID `{6ca50344-051a-4ded-9779-a43305165e35}` | Software (Microsoft's `CLSID_MSH264EncoderMFT`) | With `--encoder software` |

The experiment reports **hardware** only when all of these hold: the transform came from an
enumeration restricted to `MFT_ENUM_FLAG_HARDWARE`, it carries a hardware device URL, and it accepted
the Direct3D 11 device manager so that it was fed GPU textures. It reports **software** only for
Microsoft's known software encoder CLSID. Anything else is **unknown**. For the Quick Sync runs all
three conditions held (asynchronous: yes; Direct3D 11 aware: yes; device manager accepted: yes).

That is Media Foundation's and the transform's own account. As a check from outside the process,
Windows' per-process GPU engine counters (`\GPU Engine(pid_<pid>_*)\Utilization Percentage`) were
sampled once a second during two runs:

| GPU engine type | `hw-1ms-30s` (Quick Sync), mean / max % | `sw-1ms-15s` (software), mean / max % |
| --- | --- | --- |
| VideoDecode | 15.3 / 36.5 | 0 / 0 |
| VideoProcessing | 17.8 / 20.0 | 22.4 / 25.4 |
| 3D | 0 / 0 | 5.8 / 6.5 |
| Copy, Compute, GSC | 0 | 0 |

The process decodes nothing, and the only difference between the two runs is the encoder, so the
VideoDecode load is taken to be the encoder's: Windows evidently accounts this adapter's codec engine
under that name. VideoProcessing is the colour conversion, present in both. The 3D load in the
software run is consistent with copying frames to system memory. This corroborates hardware encoding;
it is an inference from a control, not a counter that says "encode".

### Interop findings

For the open question "which interop approach to Media Foundation is workable from C#":
`Microsoft.Windows.CsWin32` 0.3.360 in its default (COM-marshalling) mode is workable. Things worth
knowing before reusing it:

- Results that are normal control flow (`MF_E_TRANSFORM_NEED_MORE_INPUT`, `MF_E_SHUTDOWN`,
  `MF_E_SAMPLEALLOCATOR_EMPTY`, attribute-not-found) must be listed under `preserveSigMethods` in
  `NativeMethods.json`, or they arrive as exceptions.
- The convenient overloads are extension methods in the `Windows.Win32` namespace.
- `VARIANT` is not generated in this mode; `ICodecAPI` takes boxed .NET values. The `CODECAPI_*`
  GUIDs are generated as types, read with `typeof(...).GUID`. The media event type enum has an
  unusable generated name, so the four values needed are constants in `H264Encoder.cs`.
- COM objects that are pooled — captured frames, encoder input samples, encoder output samples —
  must be released explicitly. Waiting for the garbage collector would stall the pools.
- It relies on the runtime's built-in COM interop, which is not compatible with trimming or Native
  AOT. That matters only if the host is ever published that way.

### Known limitations and unresolved risks

Of the experiment:

- **No scaling**, when these runs were made: everything here was encoded at the monitor's
  2560 × 1600, High profile, level 5.1. Scaling was added in step 2a, and the one Android decoder
  tried there accepted these files as they are.
- **Content decides the frame rate.** Nothing is delivered while the screen is still, and the
  experiment does not repeat frames. A sustained 60 fps load with controlled content was not run;
  doing so would have meant putting a test window over the screen of the person using the machine.
- **Throughput headroom is thin.** When the hardware encoder was the bottleneck it took 55 to 59 fps
  at 2560 × 1600. A lower resolution would presumably help, but that was not measured.
- **One resolution, one adapter, one monitor.** A second monitor, an odd-sized monitor, the NVIDIA
  encoder, capture across adapters, HDR displays and protected content were not tested. (Step 2a
  later ran on an external monitor with the NVIDIA encoder; the rest is still untested.)
- **Size changes and monitor removal stop the run.** The code detects both, reports them and
  finalises the file, but neither path was exercised: that would have meant changing the display
  configuration of a machine in use.
- **Cancellation** was exercised with Ctrl+Break, which takes the same path as Ctrl+C: the run
  stopped after 3.8 s of a 30 s request, drained, and left an 83-frame file that ffmpeg decodes
  without error. Ctrl+C itself could not
  be delivered from the automated session and was not tried from a terminal.
- **Colour signalling is incomplete.** The pixels are BT.709 limited range. The Quick Sync stream
  signals the range (`tv`) but not the matrix; the software encoder's stream signals neither. A
  decoder that assumes BT.601 will show slightly wrong colours.
- **A raw `.h264` has no timestamps.** The irregular spacing of captured frames is not in the file;
  a player shows it at a constant rate. The real pipeline carries timestamps in the protocol.
- **Keyframes are by count, not time.** 120 frames is 2 s only at a real 60 fps. Keyframe on request,
  which the pipeline needs, is not implemented.
- **Borderless capture** was requested and Windows answered "allowed" from this unpackaged process.
  Nobody looked at the screen to confirm the border was absent. An earlier run without the option
  also succeeded.
- **No soak.** The longest run is 30 s. Peak working set was 198 MiB there against 196 MiB at 10 s,
  which is no sign of growth and far short of the ten-minute criterion below.
- **Debug build.** Not re-measured in Release.
- **No mid-run encoder fallback.** If an encoder opens and then fails, the run fails; rerun with
  `--encoder software`.

For the product:

- **The timer.** The host will need a 1 ms timer while streaming, or another way around the
  encoder's dependence on it, and only then, because a fine timer costs power. As recalled from
  Microsoft's documentation (unverified, see the finding above), Windows 11 may not honour
  `timeBeginPeriod` for a process whose windows are all minimised or hidden, which is exactly how a
  tray-resident streaming app runs. Not tested here.
- **Encoder turnaround is not latency.** Roughly 11 ms from frame to encoded sample is one stage.
  Capture delay, transport, decode and display are unmeasured, and so is everything end to end.
- **`MinUpdateInterval` rounds up to whole refreshes**, so the achievable rate depends on the panel:
  55 fps for a 60 fps request at 165 Hz.
- **Battery.** All of this was measured on battery power. Mains power may differ.

### Deviations from the plan

| Plan | What was done | Why |
| --- | --- | --- |
| Console app takes "monitor index, frame rate, bitrate", streams until Ctrl+C, prints a line per second | Takes those and more, stops after a duration or frame count, prints one report at the end | Step 1 has nothing to stream to; a bounded run that reports once is easier to verify and to quote. |
| `experiments/` or a throwaway branch | `apps/windows/experiments/`, in the solution, with unit tests in `apps/windows/tests/` | So it builds and is tested with everything else. It stays isolated from the app. |
| "Play it with a desktop player" | Decoded with ffmpeg (software and GPU decoders), frames counted, pictures inspected | Checkable without a person watching. |
| "Long GOP, keyframe on request" | Fixed 2 s interval; no on-request keyframes | Nothing can request one yet, and several IDR pictures per file let the SPS/PPS placement be checked. |
| — | 1 ms timer resolution requested by default | Not foreseen. Found by measurement. |
| — | Windows' own capture rate limit (`MinUpdateInterval`) used in front of the pacer | Avoids copies that would be discarded. It turned out to round the rate down. |

### What followed

The next step recommended here was to give the experiment an output size, read a real device's
decoder limits, and decode a captured file on it. That is [step 2a](#step-2a--decode-a-captured-file-on-android),
below. The remaining part of that recommendation, replaying a file through `adb forward` with the
protocol's framing, was overtaken by [step 3](#step-3--live-stream-over-adb-forward), which sent
live video through it instead.

## Step 2a — decode a captured file on Android

Step 2 of the plan has two halves: getting a known-good stream decoded on a real device, and getting
it there through the socket with the protocol's framing. This section is the first half only. The
file reached the tablet with `adb push` and was read from storage; no socket, no framing and no live
video were involved, and no latency was measured.

Run on 2026-10-12, in a later session than step 1.

### What was built

**Windows.** The capture experiment gained `--output-size <WxH>`. The Direct3D 11 video processor
that already converts BGRA to NV12 now also scales in the same pass (`Graphics/OutputGeometry.cs`
works out the rectangles; `Graphics/Nv12Converter.cs` applies them). The picture keeps its shape: if
the requested size has a different aspect ratio, the picture is centred and the rest of the frame is
black (luma 16, neutral chroma, checked with ffmpeg's `signalstats`). Without the option nothing
changes: the monitor's own size is still the default.

**Android.** A decoder validation screen that exists only in debug builds:

| Part | File, under `apps/android/app/src/debug/…/decodertest/` | Does |
| --- | --- | --- |
| Access units | `h264/AnnexB.kt`, `h264/AccessUnitReader.kt` | Reads the file as a stream and hands out one whole picture at a time. Memory is bounded by the largest picture, not the file. |
| Parameters | `h264/SequenceParameterSet.kt` | Parses the SPS (emulation-prevention removal, Exp-Golomb, cropping, VUI timing) for size, profile, level and stated frame rate. |
| Capability mapping | `h264/AvcProfileLevel.kt`, `DecoderCatalog.kt` | Reads `MediaCodecList`, prints what each H.264 decoder reports, and compares a stream with those limits. |
| Decoder | `H264FileDecoder.kt` | One `MediaCodec` in asynchronous mode on its own thread, rendering to a `SurfaceView`. |
| Screen | `DecoderValidationActivity.kt`, `DecoderValidationViewModel.kt`, `DecodeSummary.kt` | File choice, decoder choice, options, the video, live diagnostics, and a result written to the log and to a file. |

The activity and its manifest entry are in the `debug` source set. A release build was made and
checked: its manifest lists only `MainActivity` and its code contains no reference to the screen.
The app's own screens, its connection state and its "not implemented" labels are unchanged.

**How raw Annex B is turned into decoder input.** A decoder input buffer has to hold one complete
picture, and a picture is not one NAL unit. The Microsoft software encoder's 2560 × 1600 stream has
16 slice NAL units per picture; every encoder puts an access unit delimiter in front, and SPS, PPS
and sometimes SEI ahead of keyframes. So the reader groups NAL units into access units by the
standard's rule (H.264 section 7.4.1.2.3): once the current unit holds slice data, a new one begins
at the first access unit delimiter, SPS, PPS, SEI or type 14–18 NAL unit, or at a slice whose
`first_mb_in_slice` is zero. That rule is exact for streams without arbitrary slice order or
redundant pictures, which none of these encoders produce; it would be wrong for a stream that uses
them. The decoder is configured before the first picture with the first SPS and PPS as `csd-0` and
`csd-1`, and the stream is then fed unmodified, parameter sets included, one access unit per input
buffer, with `BUFFER_FLAG_KEY_FRAME` on IDR pictures and timestamps counted up from the frame rate
the SPS states.

### The device

| | |
| --- | --- |
| Device | Xiaomi Pad 7, model `2410CRP4CI` (`uke`) |
| OS | Android 16 (API 36), HyperOS 3.0.304.0 |
| SoC | Qualcomm SM7675 |
| Display | 2136 × 3200 as Android reports it, running at 120 Hz (144 Hz available) |
| Connection | USB, ADB. Battery at 12 % and charging over USB during the runs. |

### What the device reports, and what was tested

**Reported** by `MediaCodecList`, read on the device by the validation screen. These are the
device's claims, not results. The first three lines also appear, with the block-rate limit, in
`adb shell dumpsys media.player`.

| Decoder | Kind | Profiles, highest level | Size | Frame rate | Bitrate | Low-latency feature |
| --- | --- | --- | --- | --- | --- | --- |
| `c2.qti.avc.decoder` (alias `OMX.qcom.video.decoder.avc`) | hardware, vendor | Baseline, Constrained Baseline, Main, High, Constrained High; all to 5.2 | 96–4096 in each dimension, multiples of 2 | 1–480 | to 100 Mbit/s | no |
| `c2.qti.avc.decoder.low_latency` (alias `OMX.qcom.video.decoder.avc.low_latency`) | hardware, vendor | the same | the same | 1–480 | **to 7 Mbit/s** | yes |
| `c2.android.avc.decoder` (alias `OMX.google.h264.decoder`) | software, platform | the same five, to 5.2 | 2–4096 | 0–960 | to 48 Mbit/s | no |

For the hardware decoder the device also publishes performance points (4096 × 2160 at 60,
3840 × 2160 at 60, 1920 × 1088 at 240, 1280 × 720 at 480 fps), a limit of 2,073,600 macroblocks per
second and 34,560 per frame, and per-size frame rates:

| Size | Reported as supported up to | Published as achievable |
| --- | --- | --- |
| 1280 × 720 | 480 fps | 328–722 fps |
| 1920 × 1080 | 254 fps | 187–412 fps |
| 1920 × 1200 | 230 fps | 170–374 fps |
| 2560 × 1440 | 144 fps | 106–233 fps |
| 2560 × 1600 | 130 fps | 95–210 fps |
| 3840 × 2160 | 64 fps (not 120) | 47–104 fps |

**Chosen for the test.** By the time of this session the PC was docked: its primary monitor was an
external 2560 × 1440 display. 1920 × 1080 at 60 fps was chosen as the conservative size because it
is the same shape as that source (so nothing is letterboxed), it is the size of one of the decoder's
published performance points at a quarter of that point's frame rate, it comes out of the encoder
as High profile level 4.2 against a reported 5.2, and at 489,600 macroblocks per second it uses
under a quarter of the reported rate limit. 1920 × 1200 was not assumed and was not used.

**Actually tested.** 1920 × 1080 first, then 1280 × 720, the monitor's own 2560 × 1440, and the two
2560 × 1600 files from step 1. Nothing above 2560 × 1600 and no frame rate above 60 fps was tested,
whatever the table above says.

### The samples

Captured with the real pipeline from the PC's primary monitor, each stopping at 600 frames:

```powershell
capture --no-border --duration 60 --max-frames 600 --output-size 1920x1080 --output …\sample-1920x1080.h264
capture --no-border --duration 60 --max-frames 600                         --output …\sample-2560x1440.h264
capture --no-border --duration 60 --max-frames 600 --output-size 1280x720  --output …\sample-1280x720.h264
```

| File | Encoded size | Encoder | SPS | Pictures | Size, bytes | Capture took |
| --- | --- | --- | --- | ---: | ---: | --- |
| `sample-1920x1080.h264` | 1920 × 1080, scaled from 2560 × 1440 | NVIDIA H.264 Encoder MFT | High, level 4.2 | 600 (5 IDR) | 14,608,580 | 44.9 s |
| `sample-2560x1440.h264` | 2560 × 1440, not scaled | NVIDIA H.264 Encoder MFT | High, level 5.1 | 600 (5 IDR) | 13,374,400 | 16.6 s |
| `sample-1280x720.h264` | 1280 × 720, scaled | NVIDIA H.264 Encoder MFT | High, level 3.2 | 600 (5 IDR) | 4,310,486 | 23.5 s |
| `hw-1ms-30s.h264` (step 1) | 2560 × 1600 | Intel Quick Sync | High, level 5.1 | 1615 (14 IDR) | 38,878,208 | 30.0 s |
| `sw-1ms-15s.h264` (step 1) | 2560 × 1600 | Microsoft software, 16 slices per picture | High, level 5.1 | 816 (7 IDR) | 20,051,900 | 15.0 s |

All five decode fully with ffmpeg (`-xerror`, exit code 0, frame counts as above). No frames were
dropped in the three new captures.

Three things differ from step 1 and should not be read past:

- **A different adapter and encoder.** With the external monitor primary, the default Direct3D
  adapter is the NVIDIA GeForce RTX 5060 Laptop GPU, and the experiment picked the NVIDIA encoder
  for it without being told to. It is classified hardware by the same three criteria as before. It
  was not cross-checked with GPU counters this time. It does not support the B-frame property the
  experiment sets, but its streams contain no B-frames. Its turnaround was 1.2 to 3.5 ms (median, by
  size), against about 11 ms for the Intel encoder in step 1; the two were measured on different
  days, displays and power states and are not a comparison.
- **The screen was mostly idle.** These captures averaged 13 to 36 fps, which is why 600 frames
  took 17 to 45 s. A raw stream has no timestamps, so a player, including the tablet in paced mode,
  shows each file as 10 s at the 60 fps its SPS states. That is faster than it happened.
- **Scaling quality was not assessed.** Text in the scaled samples is legible in the frames that
  were looked at. Which filter the video processor uses, and how it compares, was not examined.

### Transfer

`adb push` into the app's own external files folder,
`/sdcard/Android/data/com.rsxdevelops.extendspace/files/decoder-samples/`. The app needs no
permission to read it and creates it on first launch. The screen also has a "Choose file…" button
that opens the system document picker; that path was **not exercised**.

Installing was the one obstacle. HyperOS refused `adb install` with
`INSTALL_FAILED_USER_RESTRICTED` until the tablet's owner turned on *Developer options → Install via
USB*. Nothing was done to get around it.

### Results on the device

Every figure below was written by the app on the tablet to
`decoder-samples/last-result.txt` and read back with `adb shell cat`; the copies are in
`artifacts/experiment-01/android/device-results/`.

- **Pictures queued** is how many access units the reader found and gave the decoder.
- **Frames decoded** is how many output frames the decoder returned and the app released to the
  surface.
- **Paced** means each frame was released at its 60 fps timestamp. The frame rate then mostly
  restates the pacing; what it shows is that the decoder kept up.
- **Unpaced** means each frame was released as soon as it was decoded. That figure is throughput of
  decode plus hand-off to the surface. The panel refreshes 120 times a second, so most of those
  frames were never seen.

Elapsed time runs from starting the decoder to releasing the last frame.

| File | Decoder (how chosen) | Pacing | Pictures queued | Frames decoded | Elapsed | Frames per second | First frame after |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| `sample-1920x1080` | `c2.qti.avc.decoder` (platform's choice) | paced | 600 | 600 | 10,080.0 ms | 59.52 | 90.9 ms |
| `sample-1920x1080` | same | unpaced, 3 runs | 600 | 600 each | 1,863–1,986 ms | 302.14, 305.39, 322.01 | 94–109 ms |
| `sample-2560x1440` | same | paced | 600 | 600 | 10,094.3 ms | 59.44 | 105.0 ms |
| `sample-2560x1440` | same | unpaced, 3 runs | 600 | 600 each | 2,554–2,575 ms | 233.00, 234.50, 234.91 | 99–118 ms |
| `sample-1280x720` | same | paced | 600 | 600 | 10,092.4 ms | 59.45 | 101.1 ms |
| `sample-1280x720` | same | unpaced, 3 runs | 600 | 600 each | 1,777–2,529 ms | 237.25, 332.15, 337.63 | 91–110 ms |
| `hw-1ms-30s` (Intel, 2560 × 1600) | same | paced | 1615 | 1615 | 27,040.4 ms | 59.73 | 133.3 ms |
| `hw-1ms-30s` | same | unpaced | 1615 | 1615 | 7,213.6 ms | 223.88 | 132.7 ms |
| `sw-1ms-15s` (Microsoft, 16 slices) | same | paced | 816 | 816 | 13,698.7 ms | 59.57 | 110.0 ms |
| `sw-1ms-15s` | same | unpaced | 816 | 816 | 3,653.6 ms | 223.34 | 113.4 ms |
| `sample-1920x1080` | `c2.qti.avc.decoder.low_latency` (named), low-latency key set | paced | 600 | 600 | 10,079.8 ms | 59.52 | 91.4 ms |
| `sample-1920x1080` | same | unpaced | 600 | 600 | 1,874.8 ms | 320.03 | 82.1 ms |
| `sample-1920x1080` | `c2.qti.avc.decoder`, low-latency key set | unpaced | 600 | 600 | 1,926.0 ms | 311.53 | 89.1 ms |
| `sample-1920x1080` | `c2.android.avc.decoder` (named, software) | paced | 600 | 600 | 10,104.9 ms | 59.38 | 116.1 ms |
| `sample-1920x1080` | same | unpaced | 600 | 600 | 2,810.5 ms | 213.48 | 85.6 ms |
| `sample-2560x1440` | same | unpaced | 600 | 600 | 4,673.1 ms | 128.40 | 107.5 ms |

What this establishes:

- **The tablet's hardware decoder accepts ExtendSpace's streams as they are**, from all three
  Windows encoders, at 1280 × 720, 1920 × 1080, 2560 × 1440 and 2560 × 1600, High profile, levels 3.2
  to 5.1. In every run the number of frames decoded equals the number of pictures in the file, which
  is also the number ffmpeg decodes.
- **The 16-slice stream decodes as 816 pictures**, not 13,056 fragments, and the picture is whole.
  That is the access-unit grouping doing its job on real data.
- **The decoder reported its output as BT.709, limited range** (`colour standard 1, colour range
  2`), in a 1920 × 1088 buffer with 1920 × 1080 visible for the 1080-line sample.
- **Unpaced throughput was several times 60 fps** at every size tested. The 1280 × 720 runs spread
  from 237 to 338 fps, so treat single unpaced figures as rough.
- **The low-latency decoder also decodes these files.** Whether it lowers latency is not something a
  file test can show. Its reported bitrate ceiling is 7 Mbit/s. Played at 60 fps the 1080-line
  sample is 11.7 Mbit/s (14,608,580 bytes in 10 s), above that ceiling, and it decoded completely
  all the same; so on this file the reported limit did not bite, which is not a promise that it
  never will. The ordinary decoder accepted the low-latency key without complaint and does not
  report the feature, so that run says nothing about whether the key had any effect.

Rendering was checked by eye on screenshots taken from the tablet during playback
(`adb exec-out screencap`): the 1080-line sample, the Intel 2560 × 1600 file and the 16-slice file
all show the Windows desktop complete and correctly coloured, text readable. The system's own count of displayed frames (590 to
600 of 600 for the samples) is shown on the screen but Android documents that callback as informational, so it is
not used as a result.

**Bad input**, all on the tablet, all without a crash:

| Input | Outcome |
| --- | --- |
| Empty file | Failed: "The file is empty." |
| An MP4 renamed to `.h264` | Failed: "The file does not begin with an Annex B start code… an MP4 or MKV file will not work." |
| 200 kB of random bytes | Failed: "The file contains no Annex B start code…" |
| Stream with its first keyframe and parameter sets cut off | Failed: "No sequence parameter set (SPS) in the first 32 pictures; the decoder cannot be configured." |
| File cut off in the middle of a picture | Completed, 13 frames for 14 pictures, and says so |
| 30 kB overwritten with random bytes mid-file | Completed, 584 frames for 584 pictures found, **no error from the decoder** |
| File name that does not exist | Failed with the system's file-not-found message |
| Decoder name that does not exist | Failed: "This device has no H.264 decoder named…" |

The damaged-file row matters: this decoder conceals damage silently. A completed run with matching
counts shows the stream was accepted, not that every picture is right. For the real samples the
evidence that the pictures are right is ffmpeg's error-free decode of the same files plus the
screenshots.

**Leaving the screen** (Home key, mid-playback): the decoder stopped and was released before the
surface went away; the result read "Stopped" after 239 frames; nothing was logged as leaked and the
app did not crash. Returning and starting another file worked. Rotation and the document picker
were not tried.

### Build and test results

| Command | Result |
| --- | --- |
| `dotnet build ExtendSpace.slnx` (in `apps/windows`) | Succeeded, 0 warnings, 0 errors |
| `dotnet test --solution ExtendSpace.slnx` | 172 passed, 0 failed (24 for the app's core, 148 for the experiment, 32 of them new) |
| `.\gradlew.bat assembleDebug testDebugUnitTest lintDebug` (in `apps/android`) | Succeeded. 41 unit tests passed (9 existing, 32 new). Lint: 0 errors, 1 warning, the same Gradle-version notice as before |
| `.\gradlew.bat assembleRelease` | Succeeded; the decoder screen is absent from the release APK |
| `adb install -r app-debug.apk` | Succeeded once *Install via USB* was enabled on the tablet |

The new Android unit tests cover the parsing only. Nine real parameter sets, taken from files this
experiment wrote with all three encoders, parse to the sizes, profiles, levels and frame rates
ffprobe reports for those files. No test pretends to exercise `MediaCodec`.

### Limitations

- One tablet, one SoC, one Android version. "Common Android hardware decoders" is still one decoder.
- No transport, no framing, no live video. The plan's step 2 is only half done.
- No latency of any kind, and no per-frame decode time. The frames-per-second figures are counts
  over elapsed time.
- Timestamps are made up from the frame rate in the SPS, because a raw stream has none.
- The picture was judged by eye, on four screenshots. Colour was not measured.
- Paced playback sleeps on the decoder's thread. That is fine for a file and wrong for a live
  stream, where frames should be released as they arrive.
- The access-unit rule assumes no arbitrary slice order and no redundant pictures.
- The validation activity is exported, so that `adb` and the launcher can start it. Debug builds
  only.
- The tablet was at 12 % battery on USB power. Its thermal and power state were not recorded.
- On the Windows side: one new adapter and encoder, an idle screen, and scaling quality unassessed,
  as described under [the samples](#the-samples). `--monitor 1`, which would now capture a display
  on a different adapter from the one doing the encoding, was not tried.

### Reproduce

From a PowerShell prompt at the repository root, with the tablet connected, USB debugging on and,
on HyperOS, *Install via USB* on. In Git Bash, set `MSYS_NO_PATHCONV=1` first or the `/sdcard`
paths are rewritten.

```powershell
# 1. Capture a sample on Windows (the output file must not exist yet)
cd apps\windows
dotnet build ExtendSpace.slnx
dotnet run --project experiments\ExtendSpace.Experiments.CaptureEncode -- capture `
    --output-size 1920x1080 --duration 60 --max-frames 600 --no-border `
    --output ..\..\artifacts\experiment-01\android\sample-1920x1080.h264
ffmpeg -v error -xerror -framerate 60 -i ..\..\artifacts\experiment-01\android\sample-1920x1080.h264 -f null -

# 2. Build and install the Android debug build
cd ..\android
.\gradlew.bat assembleDebug testDebugUnitTest lintDebug
$adb = "$env:ANDROID_HOME\platform-tools\adb.exe"
& $adb devices
& $adb install -r app\build\outputs\apk\debug\app-debug.apk

# 3. Open the screen once so the app creates its samples folder, then push the sample
$activity = "com.rsxdevelops.extendspace/.decodertest.DecoderValidationActivity"
$samples  = "/sdcard/Android/data/com.rsxdevelops.extendspace/files/decoder-samples"
& $adb shell am start -n $activity
& $adb push ..\..\artifacts\experiment-01\android\sample-1920x1080.h264 "$samples/"

# 4. Decode it and read the result back (-S restarts the app so the arguments are applied)
& $adb shell am start -S -n $activity --es file sample-1920x1080.h264 --ez autostart true
& $adb shell cat "$samples/last-result.txt"        # written when the run ends, about 10 s later

# Variations
& $adb shell am start -S -n $activity --es file sample-1920x1080.h264 --ez autostart true --ez paced false
& $adb shell am start -S -n $activity --es file sample-1920x1080.h264 --ez autostart true --es decoder c2.android.avc.decoder
& $adb shell am start -S -n $activity --es file sample-1920x1080.h264 --ez autostart true --es decoder c2.qti.avc.decoder.low_latency --ez lowLatency true
& $adb shell cat "$samples/capabilities.txt"       # what MediaCodecList reports on this device
& $adb logcat -d -s ExtendSpaceDecoderTest         # the same result and capability report, from the log
```

The screen is also in the launcher as "ExtendSpace decoder test": tap a file name, then Decode.

### What was recommended next

This is the recommendation as it stood at the end of step 2a. [Step 3](#step-3--live-stream-over-adb-forward)
did the transport and the framing, but with the live encoder in place of a file sender.

**Finish step 2: send a captured file through `adb forward` with the protocol's video framing, into
this same decoder.**

Both ends of the pipe now work on their own. What is unproven is the middle. Concretely: a small
Windows sender that reads a `.h264` file, wraps each access unit in a protocol video frame with its
`KEYFRAME` and `CONFIG` flags and a real timestamp, and writes it to `127.0.0.1:<port>` behind
`adb forward tcp:<port> localabstract:extendspace`; and on Android a `LocalServerSocket` reader that
replaces the file reader in front of `H264FileDecoder`. The Windows experiment's access-unit scanner
and the Android decoder session carry over unchanged. That step is also where decode time per frame
and the low-latency decoder's real effect can first be measured, because frames will arrive with
timestamps instead of all at once.

## Step 3 — live stream over adb forward

Run on 2026-10-13. The Windows capture experiment was given a `stream` command and the Android debug
build a live screen; a live Windows desktop was carried over USB through `adb forward` and shown on
the tablet, decoded by its hardware decoder.

This section also finishes the second half of step 2. The plan was to replay a file through the
socket first and go live afterwards. The replay sender was not built: once the framing had unit
tests on both sides against the same byte-exact examples, the live encoder was connected directly,
and transport, framing and decoding were proven with live input instead. Nothing here was measured
with a file.

Everything below is the experiment, not the product. The WinUI host and the Android app's own
screens are unchanged and still cannot connect.

### Who listens, and why it is not Windows

The brief for this step said Windows should listen. It does not; the Android app does. That is the
design the [protocol draft](../../protocol/specification/protocol.md#3-transport-bindings) and the
[boundaries table](#boundaries-and-proposed-apis) above already had, and it was kept deliberately:

```
Windows                                             Android
ExtendSpace.CaptureEncode.exe ──TCP──► 127.0.0.1:27183 ══ adb ══► localabstract:extendspace ◄── the app listens
        connects                       (adb listens,               (LocalServerSocket, not a
                                        loopback only)              network socket)
```

- The only listening socket on the PC belongs to `adb`, on the loopback interface. The experiment
  opens no listener at all, so nothing is offered to the LAN and no firewall prompt appears.
- The Android app listens on an abstract local socket, which is not reachable from any network. It
  holds no network permission. It reads the peer's credentials and closes any connection whose
  user ID is not the ADB shell user (2000) or root (0). Every connection in these runs was accepted,
  so the check passes for `adb`; that it *refuses* another app was not tested on the device, because
  that needs a second app.
- The alternative, `adb reverse` with a Windows listener, would have put a listening socket in a
  process that also captures the screen, to save nothing.

The host sets the forward up itself (`adb forward tcp:27183 localabstract:extendspace`) and removes
it when it stops. `--no-adb-forward` skips both for a forward made by hand.

### What was built

**Windows** — `apps/windows/experiments/ExtendSpace.Experiments.CaptureEncode`:

| Part | File | Does |
| --- | --- | --- |
| Shared pipeline | `CapturePipeline.cs` | The capture → NV12 → encoder path of step 1, lifted out of `ExperimentRunner.cs` so that writing a file and streaming run the same code up to the encoded access unit. The file command was re-verified after the move. |
| Framing | `Streaming/Protocol.cs`, `Streaming/FrameParser.cs` | The preface, the 8-byte header, the per-channel limits (checked before a payload is read) and an incremental parser that accepts bytes in any split. |
| Control messages | `Streaming/ControlMessages.cs` | The ten control messages as JSON, with the depth limit. |
| Video frames | `Streaming/VideoFrames.cs` | Wraps an access unit with its sequence number, timestamp and `KEYFRAME`/`CONFIG` flags; tracks whether the parameter sets have been sent. |
| Backpressure | `Streaming/VideoSendQueue.cs` | A queue of at most 4 frames. When it is full it drops, and then refuses everything up to the next keyframe, which it asks the encoder for. |
| Timing | `Streaming/Timing.cs` | The host clock, the clock-offset estimate from `ping`/`pong`, reconnect backoff (250 ms doubling to 4 s, within a budget), and the keyframe-request rate limit (one per 500 ms). |
| Session | `Streaming/HostSession.cs`, `Streaming/ConnectionReader.cs` | Connects, runs the handshake, then one thread reads and one thread owns the write side. Drives `Connecting → Negotiating → Streaming → Reconnecting/Disconnected/Failed`. |
| Transport | `Streaming/AdbForward.cs` | Finds `adb`, makes the forward, removes it. The only file that knows ADB exists. |
| Run and report | `Streaming/StreamRunner.cs`, `Streaming/StreamReport.cs` | Ties the pipeline to the session, prints a line a second and a report at the end. |
| Encoder | `Encoding/H264Encoder.cs` | Gained keyframe on request (`CODECAPI_AVEncVideoForceKeyFrame`), which step 1 did not have. |

**Android** — `apps/android/app/src/debug/java/com/rsxdevelops/extendspace/livestream`, debug builds
only:

| Part | File | Does |
| --- | --- | --- |
| Framing and messages | `protocol/Protocol.kt`, `protocol/FrameParser.kt`, `protocol/ControlMessages.kt` | The same framing, limits and messages as the Windows side. |
| Session | `LiveSession.kt` | `LocalServerSocket`, the peer check, the handshake, the receive loop, statistics once a second, and listening again after a connection ends. |
| Decoder | `LiveDecoder.kt` | One `MediaCodec` in asynchronous mode, configured from the SPS and PPS of the first `CONFIG` frame, output to the `SurfaceView`. Each decoded frame is released at once, with no pacing. |
| Stream health | `VideoGate.kt` | Decides, frame by frame, whether to decode: it stops at a sequence gap and waits for a `CONFIG | KEYFRAME` frame. Also the client's keyframe-request limiter. |
| Latency | `LatencyTracker.kt` | Turns host timestamps into local ones and keeps per-second summaries. |
| Screen | `LiveStreamActivity.kt`, `LiveStreamViewModel.kt` | The video, a status overlay, and a result file written when a connection ends. In the launcher as "ExtendSpace live test". |

The step 2a decoder screen is untouched; the live screen reuses its SPS parser and decoder catalogue.

Access units are sent exactly as the encoder produced them, Annex B, parameter sets in-band in
front of every keyframe. The first frame of every connection is a keyframe the host forces, flagged
`CONFIG | KEYFRAME`.

**Release builds.** After the last change `gradlew assembleRelease` was run and the APK inspected:
the merged manifest lists `MainActivity` as its only activity, and no string `livestream`,
`LiveStream`, `decodertest` or `org/json` occurs in its dex files (the debug APK has 27 lines
matching `livestream`). `org.json`, which the control codec uses, is part of Android; the artifact
added to the build is a `testImplementation` dependency so that the unit tests can run on a desktop
JVM, and it ships in nothing.

### Changes to capture pacing

Step 1 found that limiting capture with `MinUpdateInterval` set to one frame interval rounds up to a
whole number of display refreshes, giving 55 fps for a 60 fps request on a 165 Hz panel. Two changes
were made here, and both also apply to the file command:

- `MinUpdateInterval` is now **half** a frame interval. Windows still discards the frames that are
  far too early, but no longer decides the rate.
- `FramePacer` became a token bucket holding at most two tokens. The old pacer refused any frame
  that came sooner than one interval after the last accepted one; 60 fps content on a 143 Hz monitor
  arrives 14 ms and 21 ms apart by turns, so it lost a frame every time one came early and delivered
  well under the target. With the bucket, a late frame does not cost the next one. The price is that
  after a pause one extra frame may be taken straight away.

Measured effect: the 720p30 run below sent 30.00 fps for 120 s from a source offering about 49 fps,
and a 30 s 1080p60 run sent 59.15 fps from a source offering about 78 fps.

### How to run

```powershell
# Tablet: open the live screen. -S restarts the app so that any options are applied.
$adb = "$env:ANDROID_HOME\platform-tools\adb.exe"
& $adb shell am start -S -n com.rsxdevelops.extendspace/.livestream.LiveStreamActivity

# PC, from apps\windows: capture the primary monitor and stream it
dotnet run --project experiments\ExtendSpace.Experiments.CaptureEncode -- stream `
    --output-size 1280x720 --fps 30 --bitrate 6M --duration 120 --no-border
dotnet run --project experiments\ExtendSpace.Experiments.CaptureEncode -- stream `
    --output-size 1920x1080 --fps 60 --bitrate 12M --duration 180 --no-border

# Afterwards: the client's own account of the last connection
& $adb shell cat /sdcard/Android/data/com.rsxdevelops.extendspace/files/live-last-result.txt
& $adb logcat -d -s ExtendSpaceLive

# Variations on the tablet
& $adb shell am start -S -n com.rsxdevelops.extendspace/.livestream.LiveStreamActivity --ez lowLatency false
& $adb shell am start -S -n com.rsxdevelops.extendspace/.livestream.LiveStreamActivity --es decoder c2.qti.avc.decoder.low_latency
& $adb shell am start -S -n com.rsxdevelops.extendspace/.livestream.LiveStreamActivity --es decoder c2.android.avc.decoder
```

`stream` takes the capture options of step 1 except `--output` and `--max-frames`, and adds:

| Option | Default | Meaning |
| --- | --- | --- |
| `--port <number>` | `27183` | Loopback TCP port to forward and connect to. |
| `--device <serial>` | the only device | adb serial, when more than one device is attached. |
| `--adb <path>` | `ANDROID_HOME`, then `PATH` | Which `adb.exe` to use. |
| `--no-adb-forward` | forward made and removed | The port is already forwarded; leave it alone. |
| `--reconnect-seconds <n>` | `30` | Give up if the connection cannot be opened, or re-opened after a loss, within this long. |

`--duration` counts from the moment the stream starts, not from launch. The order does not matter
much: if the live screen is not open yet the host keeps retrying for `--reconnect-seconds`. Exit
codes are as before: 0 the stream ran and ended in an orderly way, 1 it failed, 2 invalid arguments.

### Conditions

| | |
| --- | --- |
| PC | The machine of step 1, now docked and **on mains power**. Windows 11 Home, build 26300. Debug build. |
| Source | Primary monitor `\\.\DISPLAY5`, 2560 × 1440 at 143 Hz, driven by the NVIDIA GeForce RTX 5060 Laptop GPU. Scaled on the GPU to the encoded size. |
| Encoder | NVIDIA H.264 Encoder MFT, classified hardware by the same three criteria as before. High profile, CBR, 2 s keyframe interval, low-latency mode. |
| Tablet | Xiaomi Pad 7 (`2410CRP4CI`), Android 16, over USB. Battery about 93 %. Display reported as 3200 × 2136 at 120 Hz. |
| Decoder | `c2.qti.avc.decoder`, hardware, the platform's own choice, with the low-latency key set, unless a row says otherwise. |
| Screen content | **Partly controlled.** A 1920 × 1080 window showing ffmpeg's `testsrc2` pattern at a nominal 60 fps, with a running frame counter, was placed on the monitor with the machine's owner's agreement. It covers 56 % of the monitor; the rest was the desktop, and the machine stayed in use. |

The pattern is what makes the frame rates below meaningful at all — without it the capture API
delivers nothing while the screen is still — but it is not a metronome. `ffplay` presents on the
compositor's schedule, other windows add changes of their own, and the capture API reported
anything from 21 to 85 frames in a second while it ran. So a second in which fewer than 60 frames
were sent is, as a rule, a second in which fewer than 60 arrived to send. The per-second figures say
which.

### What the counters mean

| Counter | Counted where | Meaning |
| --- | --- | --- |
| Delivered | host | Frames the capture API handed over. |
| Skipped by the pacer | host | Delivered sooner than the target rate allows; released unencoded. |
| Skipped, no client | host | Delivered while no client was streaming; released unencoded. |
| Encoded | host | Access units that came out of the encoder. |
| Dropped | host | Encoded but not sent: send queue full, waiting for a keyframe after such a drop, or still queued when a connection ended. Each kind is counted separately. |
| Transmitted | host | Written to the socket in full. |
| Received | client | Read from the socket in full. |
| Missing | client | Sequence numbers that never arrived. |
| Discarded | client | Received but not given to the decoder: no surface, waiting for a keyframe, or backlog. |
| Decoded | client | Output frames the decoder returned. |
| Rendered | client | Decoded frames released to the display surface. This is the last point the app can observe. |

The client's totals reach the host in its `stats` messages. When a run ends the host stops sending
and waits, for up to 3 s, for a report that accounts for everything it transmitted before it says
goodbye, and it prints both sides in one report. "Rendered" is not "seen": the tablet's
compositor decides what reaches the panel and when. Android's frame-rendered callback agreed with
the rendered count in every run, but Android documents it as informational.

Frame rates are counts divided by the measurement window, which runs from the first completed
handshake to the stop. They are averages over the whole run, including seconds in which the source
offered little.

### Results

The full host report, the client's result file and a screenshot from the tablet for each run are in
`artifacts/experiment-01/live/` (git-ignored, because they show the screen).

**A live picture did render.** The handshake completed 60 to 82 ms after the host started
connecting, and screenshots taken from the tablet with `adb exec-out screencap` during each run
show the Windows desktop and the moving test pattern, with its frame counter advancing between
shots, complete and correctly coloured. That was judged by eye.

**Sustained runs**

| | 1280 × 720, 30 fps target | 1920 × 1080, 60 fps target |
| --- | --- | --- |
| Run | `sustained-720p30` | `sustained-1080p60` |
| Bitrate target | 6 Mbit/s | 12 Mbit/s |
| Measured duration | 120.018 s | 180.013 s |
| Delivered by capture | 5907 | 10,566 |
| Skipped by the pacer | 2304 | 1796 |
| Encoded | 3601 | 8765 |
| Transmitted | 3601 (61 keyframes) | 8765 (74 keyframes) |
| Bytes transmitted | 83,203,273 | 164,187,950 |
| Received by the client | 3601, 83,203,273 bytes | 8765, 164,187,950 bytes |
| Missing / discarded | 0 / 0 | 0 / 0 |
| Decoded | 3601 | 8765 |
| Rendered | 3601 | 8765 |
| Dropped anywhere, host or client | 0 | 0 |
| Decoder errors | 0 | 0 |
| Average frame rate, capture delivered | 49.19 fps | 58.65 fps |
| **Average frame rate, transmitted = rendered** | **30.00 fps** | **48.69 fps** |
| Average bitrate | 5.546 Mbit/s (693,256 bytes/s) | 7.297 Mbit/s (912,091 bytes/s) |
| Transmitted per second, min / median / max | 29.22 / 29.98 / 30.69 fps | 20.78 / 57.64 / 61.01 fps |
| Largest send queue seen | 1 frame | 1 frame |
| Host processor time | 3.7 % of one logical processor | 5.3 % of one logical processor |
| Host peak working set | 116.3 MiB | 119.2 MiB |

- **The 720p30 run hit its target in every one of its 120 seconds** (never below 29.2 fps) and not
  one frame was lost between the encoder and the tablet's surface.
- **The 1080p60 run did not average 60 fps, and should not be described as a 60 fps result.** It
  averaged 48.69 fps. Nothing was dropped: all 8765 encoded frames were rendered, and the send queue
  never held more than one frame. The shortfall is on the supply side. The capture API delivered 60
  or more frames in only 102 of the 180 seconds; in those seconds the stream carried a median of
  59.4 fps (minimum 53.1), and 71 of them carried 59 or more. In the other 78 seconds fewer than 60
  frames arrived to begin with. A 30 s run under the same settings a few minutes later
  (`variant-1080p60-no-lowlatency-key`, below) happened to get a steadier source and carried
  59.15 fps. An earlier 180 s run, with a slightly older client build and not kept, carried 10,210
  frames, 56.72 fps, also with no drops.
- So the claim these runs support is: **the path carried every frame it was given, at up to about
  60 fps at 1080p, for three minutes.** Whether it holds a steady 60 fps needs a source that offers
  a steady 60 fps, and that was not available here.
- Neither run filled its bitrate target; the pattern is cheap to encode.
- Memory did not grow in a way that shows at this length: peak working set was 116 to 120 MiB in
  every run from 20 s to 180 s. The longest run is three minutes, not the ten the success criteria
  ask for. Client memory was not measured.

**Decoder variants**, 30 s each at 1920 × 1080, 60 fps and 12 Mbit/s target:

| Run | Decoder on the tablet | Transmitted | Rendered | Discarded by the client | Average fps | Mbit/s | Capture → released, mean |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `variant-1080p60-no-lowlatency-key` | `c2.qti.avc.decoder`, low-latency key **not** set | 1775 | 1775 | 0 | 59.15 | 11.632 | 19.31 ms |
| `variant-1080p60-lowlatency-decoder` | `c2.qti.avc.decoder.low_latency` | 1542 | 1542 | 0 | 51.38 | 10.292 | 16.98 ms |
| `variant-1080p60-software-decoder` | `c2.android.avc.decoder` (software) | 1616 | 1599 | 15 | 53.86 sent, 53.29 rendered | 10.595 | 61.97 ms |

- The source differed from run to run (78, 53 and 58 fps delivered), so the frame rates in this
  table compare the screen, not the decoders.
- The low-latency decoder's mean was about 2 ms lower than the ordinary decoder's 19.0 to 19.3 ms.
  That is one 30 s run each, at different frame rates, with a clock uncertainty of ±1.6 to 1.8 ms.
  It is a hint, not a finding.
- Setting or not setting the low-latency key on the ordinary decoder made no visible difference
  (19.02 ms in the sustained run with it, 19.31 ms without). That decoder does not report the
  feature, so no effect is the expected result.
- The low-latency decoder was sent 10.3 Mbit/s on average against the 7 Mbit/s it reports as its
  limit, and decoded all of it.
- The software decoder kept up with the frame rate but took three times as long per frame, and it
  was the only run in which the client discarded anything: at start-up its backlog passed the limit
  once, the client dropped 15 frames and asked for a keyframe (`decoder_backlog`), and the picture
  continued from there. That is the backlog rule working as designed, on real data.

**An idle desktop** (`idle-desktop-1080p60`, 20 s, no test pattern): 302 frames, 15.06 fps,
1.056 Mbit/s, with whole seconds in which nothing was sent. Nothing was lost; the screen simply did
not change. The frame rate of this pipeline follows screen activity, and a still screen costs almost
nothing.

### Latency

**What was measured.** The time from Windows handing a captured frame to the host process, to the
tablet releasing the decoded frame to its display surface. It is called "capture → released" below.

**How.** Not by subtracting two wall clocks. The two devices' clocks are unrelated and neither is
trusted:

1. The host stamps each frame with its own monotonic clock (`Stopwatch`) at the moment the capture
   API's frame-arrived callback runs. That stamp travels in the video frame's `timestamp_us`.
2. The host sends `ping` with its clock; the tablet answers `pong` with its own monotonic clock
   (`System.nanoTime`). Taking the reply to have been written halfway through the round trip gives
   the difference between the two clocks, with an error of at most half the round trip. The host
   keeps the estimate from the fastest round trip it has seen and sends it back in the next `ping`.
3. The tablet converts each frame's stamp to its own clock with that estimate and subtracts it from
   the time it called `releaseOutputBuffer` for that frame. It reports count, minimum, mean, 95th
   percentile and maximum for each second.

The fastest round trip was 2.2 to 4.5 ms in these runs, so every cross-device figure below carries
an uncertainty of **±1.1 to ±2.2 ms** from the clock relation alone, and the report of each run
states its own. The figure on the host alone (capture → encoded) uses one clock and has no such
term.

| | `sustained-720p30` | `sustained-1080p60` |
| --- | --- | --- |
| Clock relation, best round trip | 3.704 ms (±1.852 ms) | 3.209 ms (±1.605 ms) |
| Capture → encoded, on the host | mean 1.37 ms, median 1.29, p95 1.83, max 12.59 | mean 2.22 ms, median 2.14, p95 2.85, max 10.78 |
| Capture → received by the app | mean 4.52 ms | mean 5.07 ms |
| **Capture → released to the surface** | **mean 15.69 ms** | **mean 19.02 ms** |
| — per-second means, median | 15.7 ms | 18.6 ms |
| — per-second 95th percentiles, median | 20.7 ms | 25.5 ms |
| — worst single frame | 139.5 ms | 143.1 ms |
| — worst frame after the first 3 s | 51.3 ms | 84.4 ms |
| — seconds whose 95th percentile exceeded 40 ms | 1 of 120, at start-up | 3 of 180, one of them at start-up |

Reading it:

- Subtracting the rows gives a rough budget: about 1.4 to 2.2 ms to encode, about 3 ms from the
  encoder to the app on the tablet (the socket, `adb`, USB), and about 11 to 14 ms inside the
  tablet between receiving a frame and releasing its decoded picture. **The tablet's decoder path
  is the largest part of what was measured.** That interval was not split further, so it includes
  any wait for a decoder input buffer as well as the decode itself. Over ADB, transport is not the bottleneck at these
  bitrates.
- The worst frames are the first ones. A hardware decoder takes around 100 ms to return its first
  picture, and the frames that arrive meanwhile wait for it. After the first three seconds no frame
  of the 720p run took more than 51.3 ms.
- These are single runs. The encoder figures are far lower than step 1's 11 ms because this is a
  different encoder (NVIDIA, not Quick Sync), a smaller picture and mains power; they are not a
  comparison.

**What this figure is not.** It is not glass-to-glass latency and must not be quoted as one. It
leaves out:

- everything before Windows hands the frame over: the application drawing, the compositor, and
  whatever the capture API adds;
- everything after the release on the tablet: its compositor, the wait for the next refresh (up to
  8.3 ms at 120 Hz) and the panel's response;
- the PC monitor's own delay, which a person comparing the two screens would also see.

**Display time on the tablet is unavailable.** The app asked for it through
`MediaCodec.setOnFrameRenderedListener`. On this device the callback reported, for every frame,
the same timestamp the frame had been queued with rather than a time on the display, which made the
"latency to display" identical to the latency to release. An early build printed that as a display
latency; it was wrong, it was noticed because the two numbers matched to the microsecond, and the
client no longer reports it. The only credible way to measure to the glass is still the one in the
[measurements table](#measurements): a camera filming both screens.

**One more caution about the start of the interval.** Windows stamps each captured frame itself
(`SystemRelativeTime`), and the experiment compared that stamp with the moment of hand-over: the
stamp ran *ahead* of the hand-over, by a median of 1.6 ms (720p run) to 3.3 ms (1080p run), with
occasional frames the other way round by tens of milliseconds. What Windows' stamp marks is not documented
precisely enough to build a latency figure on, so it is reported and not used. If it marks the
moment of composition, the true interval is a few milliseconds longer than the table says.

### Disconnect and reconnect

All at 1280 × 720, 30 fps. Performed through `adb`, not by hand.

| Test | What was done | What happened |
| --- | --- | --- |
| **Client vanishes** (`reconnect-720p30`) | 12 s into a 60 s stream the app was killed (`am force-stop`), and started again about 6 s later. | The host logged "connection lost: the other side closed the connection" within 0.2 s, went to `Reconnecting`, and retried after 0.25, 0.5, 1, 2 and 4 s. Each retry reached `adb` and was closed at once ("no app is listening"). The retry at 19.98 s found the restarted app; the handshake ran again in full and the state was `Streaming` at 20.04 s, starting with a fresh `CONFIG | KEYFRAME` frame. While no client was connected the host kept capturing but encoded nothing (238 frames released unencoded). |
| **App sent to the background** (same run) | About 33 s in, the Home key; about 6 s later the live screen was brought back. | The connection stayed up. The client went on receiving about 30 frames a second and discarded them, having no surface to decode onto (158 in all). When the surface came back it sent `request_keyframe` with reason `surface_recreated` (host log, 38.83 s), the host produced a keyframe, and frames were being rendered again within the same second. A screenshot taken afterwards shows the live picture. |
| **Totals for that run** | | Host: 2 connections established, 1 lost; 1562 frames transmitted, none dropped. Client, both connections: 1546 received, 158 discarded, 1388 submitted, 1387 decoded and rendered, 0 missing, 0 decoder errors. The 16 frames transmitted but never reported received went to the app that was killed, which could not send a final report. |
| **Client leaves on purpose** (`client-bye`) | 8 s in, the Back key on the live screen. | The client sent its final `stats` and `bye` ("the live screen was closed on the device"). The host ended in `Disconnected`, did **not** try to reconnect, and exited with code 0: 260 transmitted, 259 received, 256 rendered. |
| **Nothing listening** (`no-client`) | The app was stopped, then the host started with `--reconnect-seconds 6`. | Retries at the same backoff, then `Failed` after 6.6 s with "no connection could be established within 6 s; last problem: the connection was closed before anything arrived; behind an adb port forward that means no app is listening". Exit code 1. The forward was removed (`adb forward --list` empty afterwards). |

Through all of these the app did not crash (no `FATAL EXCEPTION` in the log) and the host process
ended by itself every time, with capture stopped, the encoder drained and the forward removed.

Seen once, in an earlier run of the reconnect test and not repeated with the final build: removing
the forward by hand (`adb forward --remove`) while streaming did not break the connection that was
already open. Only new connections need the forward.

**Not tested:**

- **Pulling the cable.** That needs a hand on the hardware. Killing the app exercises the same host
  path for a connection that closes; it does not exercise a connection that goes silent without
  closing, which is what the 5 s keepalive timeout is for. That timeout has no test of its own and
  has never fired against a device.
- **Rotation**, and any surface change other than Home and back.
- **A second device**, or `--device`.
- **A malformed or hostile peer against the running apps.** Bad prefaces, oversized lengths, deep
  JSON, truncated streams and wrongly typed fields are covered by unit tests on both sides, and an
  oversized frame, a peer that is not an ExtendSpace client and a version mismatch by a loopback
  test client on Windows. None was sent to the tablet.
- **The host's drop path.** The send queue never filled, so "drop to the next keyframe and force
  one" ran only in unit tests.

### Build and test results

| Command | Result |
| --- | --- |
| `dotnet build ExtendSpace.slnx` (in `apps/windows`) | Succeeded, 0 warnings, 0 errors |
| `dotnet test --solution ExtendSpace.slnx` | 299 passed, 0 failed (24 for the app's core, 275 for the experiment; 172 before this step) |
| `.\gradlew.bat assembleDebug testDebugUnitTest lintDebug` (in `apps/android`) | Succeeded. 82 unit tests passed (41 before this step). Lint: 0 errors, 1 warning, the same Gradle-version notice as before |
| `.\gradlew.bat assembleRelease` | Succeeded; neither debug screen is in the release APK (checked as described above) |
| `adb install -r app-debug.apk` | Succeeded |

What the new tests cover, and what they do not:

- **Framing**, on both sides: every split of a byte stream, each limit at and one past its edge, a
  bad preface, an unknown channel skipped, and three byte-exact examples (a `hello`, a `ping`, a
  video frame) that both test suites decode, so the two implementations are held to the same bytes.
- **Control messages**: round trips, missing and unknown fields, JSON nested too deep.
- **Sending**: the send queue's drop-and-wait-for-keyframe rule, sequence numbering, parameter-set
  tracking.
- **Timing**: the clock estimate, backoff, the keyframe limiter, and the new pacer.
- **The host session**, against a fake client on a loopback socket inside the test: the handshake,
  a version mismatch, a client with no decoder large enough, nothing listening, a peer that accepts
  and closes as `adb` does, a client that sends `bye`, and a client that disappears and comes back.
- **On Android**: the gate that decides what to decode, the request limiter and the latency
  arithmetic.
- No test captures a screen, runs an encoder, or touches `MediaCodec` or a `LocalServerSocket`.
  Those were exercised only by the runs above.

### Problems found along the way

All fixed, and listed because each says something about the design:

| Problem | Cause | Fix |
| --- | --- | --- |
| The host crashed when nothing was listening | A refused connection surfaced as an `AggregateException` from a timed wait and was not among the exceptions treated as "not connected". | Unwrapped; a refused connection is now an ordinary failed attempt. Covered by a test. |
| 1080p60 delivered well under 60 fps from a 60 fps source | The pacer, as described under [Changes to capture pacing](#changes-to-capture-pacing). | Token bucket. |
| The first 17 frames of every 1080p60 connection were thrown away | The client's backlog limit of 4 was reached while the decoder was still starting, so it discarded the opening keyframe and asked for another. | A larger allowance (30) until the decoder returns its first picture. |
| A "displayed" latency that was not one | The frame-rendered callback, as described under [Latency](#latency). | Not reported. |
| "Closed in the middle of a message" when no app was listening | `adb` accepts the TCP connection and then closes it when it finds nobody behind the forward. The host had received nothing, and described that as an interrupted message. | The message now says what it means: nothing arrived, so no app is listening. |
| Client totals short of the host's at the end of a run | The client's last `stats` could be up to a second old when the session ended. | The client sends a final `stats` before its own `bye`, and the host waits for the client's count to catch up before sending its own. |

### An observation about capture, unexplained

While setting up, the capture API delivered **no frames at all** from the external monitor while a
browser showed a full-screen video on it: the tablet showed one stale image. With the video out of
full screen, capture behaved normally. The cause was not established. Candidates are that the video
was paused, or that a full-screen video presented on a hardware overlay plane changes nothing the
capture API reports. If it is the second, it is a real limitation of mirroring full-screen video
and must be understood before Mirror mode ships. It was not investigated further, because the
monitor was in use.

Capturing the laptop's own panel (on the Intel adapter) while encoding on the NVIDIA adapter was
tried briefly and worked: 36 frames in 4 s of an idle screen. That is the cross-adapter case step 2a
listed as untried; it has now been tried once and not measured.

### Limitations

- One PC, one tablet, one cable, one encoder, one day. Single runs.
- The source was a test pattern in a window on a machine in use, not a controlled 60 fps signal, and
  the longest run is 180 s. The success criterion of ten minutes has not been attempted.
- Latency stops at the tablet's surface and starts at Windows' hand-over. No glass-to-glass figure
  exists.
- The picture was judged by eye on screenshots. No frame was compared with its source, so a frame
  shown twice or shown late would not have been noticed.
- The host does not choose a configuration from the client's capabilities; the person running it
  does. There is no bitrate adaptation and no reconfiguration when the source changes size.
- The Android side is one debug activity. There is no foreground service, so the stream lives only
  while that screen is in front, and the code is not connected to the app's own connection state
  machine. On Windows it is a console program, not connected to `ExtendSpace.Core`.
- The live activity is exported, like the decoder screen, so that `adb` and the launcher can start
  it. Debug builds only.
- The control channel is JSON and unauthenticated. Over USB the gate is ADB authorisation plus the
  peer check; there is no pairing.
- Tablet battery, temperature and memory were not recorded. Host GPU load was not measured this
  time.
- Debug builds on both sides.

### Next step

*As recommended at the end of step 3. [Phase 2.4](#phase-24--reliability-and-latency-validation)
did items 1 to 3 and investigated item 4 without resolving it.*

**Measure what a person would see, with a source that can be trusted, for long enough to matter.**

1. **Glass-to-glass latency with a camera.** A millisecond counter on the PC monitor, the tablet
   beside it, filmed at a high frame rate. This is the one number still missing, and the 16 to 19 ms
   measured here is only its middle.
2. **A controlled source.** A full-screen pattern that presents exactly once per refresh on a
   monitor nobody else is using, so that "60 fps for ten minutes" can be tested rather than
   inferred.
3. **A ten-minute soak** at 1080p60, recording memory on both sides and the tablet's temperature,
   and the cable pulled by hand during it.
4. **Find out what happened to full-screen video** under the capture API.

After that the code is worth moving: the framing, the session and the decoder out of the
experiment and the debug source set, behind the session coordinator on Windows and the connection
controller on Android, with the host choosing a configuration from the client's capabilities.

## Phase 2.4 — reliability and latency validation

Run on 2026-10-13, after step 3, against the same PC and tablet. Nothing here is product code: the
changes are to the experiment and to the debug-only Android screen, and neither application can
connect yet. This phase measured; it changed code only where a measurement or a test showed a
defect.

Raw output of every run is in `artifacts/experiment-01/phase-2.4/` (git-ignored): the host report,
the client's result, the tablet's log, resource samples, the pattern's own account, a screenshot,
and the camera clip.

### Outcome at a glance

| Workstream | Classification | Evidence in one line |
| --- | --- | --- |
| A. Controlled 1080p60 | **Passed** | With a source that was measured to change on every display refresh, 180 s carried 10,801 frames at 60.00 fps, none lost; every second had at least 59 sent. |
| B. Ten-minute 1080p60 soak | **Passed** | 600.011 s, 35,993 frames encoded = sent = received = decoded = released to the surface, 59.99 fps, no drift in rate or latency, no error on either side. |
| C. USB disconnect and reconnect | **Passed, after a fix** | A real cable pull was noticed by both sides within 40 ms and the stream resumed by itself 3.3 s after the cable went back. Before the fix it could not have: adb drops its port forward when the device goes. |
| D. Full-screen capture freeze | **Partially validated; unresolved** | Capture starvation was seen in 4 of 17 full-screen browser runs on the laptop panel, in two clusters hours apart, and in none of 12 with the browser's video overlays turned off. That it is Windows withholding frames, not this program discarding them, is established. The cause is not. |
| E. Glass-to-glass latency | **Partially validated** | One 120 fps camera clip: the tablet's glass was a median of 45.7 ms behind the monitor's. One clip, seven seconds, a phone camera. |

### Environment

| | |
| --- | --- |
| PC | As in step 3: Windows 11 Home build 26300, docked, on mains. Debug builds. |
| Monitors | External 2560 × 1440, which Windows reports at 144 Hz, primary, on the NVIDIA GeForce RTX 5060 Laptop GPU. Laptop panel 2560 × 1600 at 165 Hz, on the Intel adapter. Both on throughout. |
| Encoder | NVIDIA H.264 Encoder MFT, hardware, High profile, CBR 12 Mbit/s, 2 s keyframe interval. |
| Tablet | Xiaomi Pad 7 (`2410CRP4CI`), Android 16, USB. Battery 92 to 94 %. Decoder `c2.qti.avc.decoder`, hardware. |
| Other | From about half way through, an Android emulator belonging to other work was attached to the same adb server, so every command named the tablet (`--device <serial>`). The PC stayed in use. |

### Two tools added to the experiment

Both are commands of `ExtendSpace.CaptureEncode.exe`. Neither sends or stores anything.

**`pattern`** shows an animated test pattern in a borderless window and presents a new picture on
every display refresh through a DXGI swap chain. It is drawn with rectangle clears only: no
shaders, fonts or new dependencies. It replaces the `ffplay` window of step 3, which was not a
steady source. Every picture carries:

- a six-digit clock of elapsed milliseconds and a six-digit frame counter, as seven-segment digits;
- a row of 30 cells in which one lit cell steps along by one per frame;
- a block that crosses the window once a second;
- a mosaic that changes completely on every frame, which is what makes the encoder fill its
  bitrate target (the step 3 pattern needed about 7 of 12 Mbit/s; this one uses all 12).

It prints, each second, how many pictures it presented against how many refreshes the monitor had,
and at the end DXGI's own count of presents against refreshes. So that the source did its job is
measured in every run rather than assumed. `--model flip|blt` chooses how the window presents,
`--fullscreen` covers the monitor, and `--sync-interval` presents on every n-th refresh.

**`probe`** runs the same capture and encoding as `stream`, discards the result, and prints each
second what the capture API did: how many frame-arrived events Windows raised, how many frames came
with them, how many this program then set aside (paced out, overtaken), and what the encoder took
and produced. It exists to tell "Windows stopped delivering frames" from "this program threw them
away", which no earlier output could.

```powershell
cd apps\windows
$exe = "experiments\ExtendSpace.Experiments.CaptureEncode"
dotnet run --project $exe -- pattern                       # until Esc, a right-click on it, or Ctrl+C
dotnet run --project $exe -- pattern --fullscreen --model blt --monitor 1 --duration 10
dotnet run --project $exe -- probe --monitor 0 --duration 60 --output-size 1920x1080 --fps 60 --no-border
```

### A. Controlled 1080p60

**Procedure.** The pattern in a 1920 × 1080 always-on-top window on the external monitor, flip
model, a new picture every refresh. Then, with the live screen open on the tablet:

```powershell
dotnet run --project $exe -- stream --device <serial> --output-size 1920x1080 --fps 60 --bitrate 12M --duration 180 --no-border
```

**Requested against actual.** The step 3 column is the historical baseline, not a new measurement.

| | Step 3 baseline (`ffplay` window) | Phase 2.4, 180 s (`a1-1080p60-flip`) | Phase 2.4, 600 s (`b1-soak-1080p60-600s`) |
| --- | --- | --- | --- |
| Requested | 1920 × 1080, 60 fps, 12 Mbit/s | the same | the same |
| Measured duration | 180.013 s | 180.017 s | 600.011 s |
| Source: pictures presented per second | not measured | 141.25 (lowest second 137) | 143.63 (lowest second 120) |
| Capture delivered, average | 58.65 fps | 82.54 fps | 73.34 fps |
| Capture delivered, lowest second | 20.78 | 80.2 | 68.6 |
| Seconds in which capture delivered 60 or more | 102 of 180 | 180 of 180 | 600 of 600 |
| Encoded | 8765 | 10,801 | 35,993 |
| Transmitted | 8765 | 10,801 | 35,993 |
| Received by the client | 8765 | 10,801 | 35,993 |
| Decoded = released to the surface | 8765 | 10,801 | 35,993 |
| Dropped, missing or discarded, either side | 0 | 0 | 0 |
| **Average frame rate, transmitted = rendered** | **48.69 fps** | **60.00 fps** | **59.99 fps** |
| Transmitted per second, min / median / max | 20.78 / 57.64 / 61.01 | 59.4 / 60.0 / 60.5 | 57.7 / 60.0 / 60.9 |
| Seconds with 59 or more transmitted | 71 of 180 | 180 of 180 | 596 of 600 |
| Average bitrate | 7.297 Mbit/s | 12.014 Mbit/s | 12.007 Mbit/s |
| Largest send queue seen | 1 frame | 1 frame | 1 frame |
| Capture → released to the surface, mean | 19.02 ms | 19.07 ms | 19.71 ms |
| Host processor time | 5.3 % of one logical processor | 6.0 % | 6.0 % |
| Host peak working set | 119.2 MiB | 119.0 MiB | 121.4 MiB |

**Sustained 1080p60 is demonstrated** for this PC, this tablet and this source, for ten minutes.
The baseline's shortfall was the source: where nothing else on the screen was changing, `ffplay`
showing a 60 fps clip was measured here to give the capture API about 54 or 55 frames a second (see
workstream D), and this pipeline does not repeat frames.

Things the table needs read with it:

- **The source runs at the monitor's refresh rate, about 144 pictures a second, not at 60.** The
  pipeline's pacer picks 60 of them. That is the product's situation whenever content changes
  faster than the stream, and it is the only way to offer a steady supply on a 144 Hz monitor,
  where 60 fps content cannot be evenly spaced. It also means the 60 frames sent are 14, 21 or 28 ms
  apart rather than a steady 16.7. How that looks was not assessed.
- **Capturing slows the pattern a little.** On its own the flip-model pattern presented on every
  refresh (806 presents over 803 refreshes). While it was being captured it fell a few pictures
  short in some seconds: about 100 of the 620 seconds it ran for the soak, 232 pictures in all, the
  worst second 120. Capture never delivered fewer than 68.6 frames in a second.
- **The client's per-second rates wobble by about one frame** (57.7 to 63.0) because they are worked
  out from its own once-a-second reports, whose spacing is not exact. The totals are exact.
- **Capture delivered either 72 or 82.5 frames a second**, switching between the two within a run.
  Those are every second refresh of 144 Hz and of 165 Hz. With two monitors at different rates,
  Windows evidently rounds the capture interval to one monitor's beat or the other's. Presents
  showed the same thing: a present on the 144 Hz monitor returned on the 165 Hz monitor's beat.
  Neither was investigated; with the pacer in front it did not matter.

### B. Ten-minute soak

`b1-soak-1080p60-600s`: the same command with `--duration 600`. It ran to the end: "the requested
duration elapsed", one connection established, none lost, no keyframe request from the client, no
decoder error, no problem logged on the tablet, no app crash.

**Over time.** The run cut into fifths, the first five seconds left out of the first:

| | First fifth | Middle fifth | Last fifth |
| --- | ---: | ---: | ---: |
| Transmitted, median per second | 60.00 fps | 60.00 fps | 60.00 fps |
| Rendered on the client, median per second | 59.80 fps | 59.90 fps | 59.80 fps |
| Bitrate, median per second | 12.05 Mbit/s | 12.07 Mbit/s | 12.09 Mbit/s |
| Capture → released, median of per-second means | 19.9 ms | 20.0 ms | 19.4 ms |
| Capture → released, median of per-second 95th percentiles | 27.2 ms | 27.5 ms | 26.4 ms |

**Latency over the whole run.** The client reports count, minimum, mean, 95th percentile and
maximum once a second, so what exists is a distribution of seconds, not of frames:

| Capture → released to the surface | min | 5 % | median | 95 % | max |
| --- | ---: | ---: | ---: | ---: | ---: |
| Per-second mean | 17.1 | 18.4 | 19.7 | 21.1 | 28.4 ms |
| Per-second 95th percentile | 22.8 | 24.5 | 27.0 | 30.6 | 117.5 ms |
| Per-second 95th percentile, after the first 5 s | 22.8 | 24.5 | 27.0 | 30.5 | 41.6 ms |
| Per-second maximum, after the first 5 s | 25.2 | 26.9 | 30.6 | 43.7 | 79.2 ms |

Over all 35,993 frames the mean was 19.71 ms (clock relation ±1.2 ms). On the host alone, capture →
encoded: mean 2.35 ms, median 2.25, 95th percentile 2.82, maximum 23.89. The single worst frame of
the run, 149.2 ms, was in the first second, while the decoder started. A per-frame median across
devices is not available; the median of the per-second means, 19.7 ms, is the nearest thing.

**Resources**, sampled every 20 s from outside the processes:

| Time into the run | Host working set | Host processor time, cumulative | Tablet app, total PSS | Tablet battery temperature | Android thermal status |
| --- | ---: | ---: | ---: | ---: | ---: |
| start | 79.0 MiB | 0.3 s | 173 MB | 36.0 °C | 0 |
| 1 min | 115.7 MiB | 4.7 s | 185 MB | 36.0 °C | 0 |
| 3 min | 116.6 MiB | 11.8 s | 187 MB | 37.0 °C | 0 |
| 4 min | 117.6 MiB | 15.8 s | 175 MB | 37.0 °C | 0 |
| 5 min | 118.5 MiB | 18.2 s | 205 MB | 37.0 °C | 0 |
| 7 min | 119.6 MiB | 28.7 s | 204 MB | 37.0 °C | 0 |
| 9.5 min | 120.4 MiB | 35.6 s | 204 MB | 37.0 °C | 0 |

- The host grew by about 5 MiB between the first and the tenth minute. The experiment keeps its
  per-frame timing samples in memory for the report, up to 144,000 of each, which accounts for
  growth of that size and stops at that limit. That explanation was not tested by removing them.
- The tablet app's memory moved in steps: up to 187 MB over the first three minutes, down to 175,
  up to 205 at the fifth minute, and then flat at 204 for the last five. Steps in both directions
  are not a leak, and ten minutes is not long enough to rule one out.
- The battery read 94 % at the start and 92 % at the end: on this USB port the tablet discharges
  slowly while it decodes and lights its screen.

Not measurable here: a frame shown twice or held too long on the tablet. Sequence numbers show a
frame that did not arrive (none); nothing in the pipeline looks at what is on the glass. The camera
clip of workstream E is the only evidence about that, and it covers seven seconds.

### C. USB disconnect and reconnect

#### What the behaviour is meant to be

From the [protocol draft](../../protocol/specification/protocol.md#12-errors-and-reconnection) and
the [overview](overview.md): when a connection is lost without a goodbye the host retries *the
transport*, with backoff from 250 ms to 4 s, for a budget of 30 s (`--reconnect-seconds`), and then
fails. Over USB the transport is the adb forward. The Android side has nothing to retry: it goes
back to listening and waits. So a cable that returns within the budget should bring the stream back
without anyone touching either device; one that returns later needs the host started again, and
never the app.

#### What was wrong, found before any cable was pulled

Reading the lifecycle against adb's behaviour gave one suspicion, which a command confirmed:
**adb removes a port forward when its device disconnects and does not put it back when the device
returns.** The experiment made the forward once, at the start. After any real disconnection every
retry would have been refused until the budget ran out. Step 3's reconnection tests did not show
this because killing the app leaves the forward in place.

Then the automated tests were extended, first against the unchanged session, to see what failed:

| Behaviour tested | Before | Change |
| --- | --- | --- |
| After a loss the transport is set up again before each new attempt | No such step existed. | The session calls a transport-restore step before every attempt after the first; for USB that runs `adb forward` again. A failure is an ordinary failed attempt, retried within the budget. |
| A transport that cannot be set up again fails the session when the budget is spent | — | Same change; the reason given is adb's own message. |
| Stopping while a handshake is stalled returns promptly | **Failed:** it waited the whole 5 s handshake timeout and returned with the session thread still running. | The handshake waits are cancellable. |
| A client that stops reading costs frames, not memory, and does not hold up stopping | Queue stayed bounded and frames were counted as dropped (passed). **Stopping took 2.5 s**, and wrote the goodbye onto the socket while another thread was still blocked writing to it. | A writer that has not let go within 250 ms is ended by closing the socket; nothing more is written to it. |
| Starting a session twice is refused | **Failed:** a second thread would have been started on the same state. | Refused with an exception. |
| A client that goes silent is lost after the keepalive timeout and the host connects again | Passed. This path had no test before. | Timeouts became settable so that the test takes half a second. |
| A handshake that stalls is given up, its socket closed, and the next client served | Passed. | — |
| A client that leaves partway through the handshake is retried rather than fatal | Passed. | — |
| A connection that ends in the middle of a message is lost and re-established | Passed. | — |
| Stopping before anything has connected ends at once | Passed. | — |

On Android the session object cannot be exercised without a device, so its deterministic parts are
what is unit-tested: the gate that decides what to decode (no surface, sequence gap, decoder
failure, restart from a keyframe), the request limiter, the latency arithmetic, and now the wording
of how a connection ended. The rest of this section is what the device showed.

#### Scripted on the tablet (no hands)

| Test | What was done | What happened |
| --- | --- | --- |
| Forward and app both lost (`c1-forward-lost`) | 12 s into a 720p30 stream: `adb forward --remove`, then the app force-stopped; app started again 8 s later. | Host: "connection lost … noticed 548 ms after the last message from the client, 14 ms after the last video frame was written". It put the forward back itself, found no app listening, kept trying, and was `Streaming` again at 24.2 s. 2 connections, 1 lost. Without the fix this run would have ended in `Failed`. |
| A second host while one is streaming (`c2`) | A second `stream` started 10 s into the first, with an 8 s budget. | The second failed cleanly after its budget, exit code 1, because the app serves one host at a time and left it waiting. The first was not disturbed. When the second exited it removed the forward the first was relying on; the app was then killed and restarted, and the first host restored the forward and reconnected. |

The second host's failure originally read "the client did not send its Hello message within 5 s".
It now says that nothing answered and that another stream may already be connected.

A warning for anyone repeating this: `adb reconnect`, which looks like the obvious way to simulate
a disconnection, left the tablet absent from adb for at least 38 s here, until the adb server was
restarted. It was not used again.

#### The cable, pulled by hand

Three attempts. The tablet's side of each is from its own log (`adbd` reporting the USB link, and
the app's "Connection ended N ms after the last bytes from the host arrived").

| | Pull 1 | Pull 2 (`c3-cable-pull-2`) | Pull 3 (`c3-cable-pull-3`) |
| --- | --- | --- | --- |
| Stream | 1080p60, 170 s | 1080p60, up to 600 s | 1080p60, up to 600 s |
| Reconnect budget | 90 s | 120 s | **5 s**, to force the give-up path |
| Cable out for | — | 12.7 s | 14.7 s |
| Host noticed | — | 39 ms after the last message from the client, 9 ms after the last frame it wrote | 216 ms after the last message, 7 ms after the last frame |
| Tablet noticed | — | 33 ms after the last bytes arrived; 19 ms after `adbd` logged the link down | 23 ms after the last bytes; 24 ms after `adbd` |
| While the cable was out | — | `Reconnecting`; "the transport could not be set up: adb.exe: device '<serial>' not found" | the same, then `Failed` after 5.0 s: "no connection could be re-established within 5 s" |
| Recovery | — | **By itself.** `Streaming` again 15.96 s after the loss, about 3.3 s after the cable went back in. Full handshake, fresh keyframe. | None, by design. A new `stream` against the same app process, not restarted, then ran 1201 frames at 60.05 fps. |
| Frames | — | 964 captured frames released unencoded during the outage; no encoded frame dropped. 7240 sent over both connections, 7239 received by the tablet's own log: one frame was in flight when the cable came out. | All 10,550 frames sent before the pull were received. |
| Afterwards | — | Forward in place while running and removed at the end; no host process left; app did not crash. | No forward, no host process; the app's process was the same one, listening. |

**Pull 1 missed.** The tablet's log shows the cable coming out a minute and a half after that
three-minute stream had ended. It taught nothing about streaming; it did show the tablet returning
to adb by itself after a replug with nothing running.

Notes:

- When the tablet cannot send its last statistics, the host's report shows frames "not reported
  received" (54 in pull 2, 13 in pull 3). The tablet's log, read after the cable was back, gives
  the true counts above. The host cannot know them.
- The app logged pull 2's second connection as "the host closed the connection without saying
  goodbye" when it was the tablet's own Back key that ended it. That wording is fixed and
  unit-tested. The tablet had been taken away by the time that build was ready, so the cable pulls
  and the soak are from the build before it; the fix was confirmed on the device later the same
  day (see [the audit](#audit-later-the-same-day)).

#### Still not tested

- **A link that goes silent without closing** was not covered by the cable pulls: both were
  reported to both ends as a closed connection within tens of milliseconds, so the 5 s keepalive
  timeout never had to act. It was tested separately afterwards, on both sides; see
  [the audit](#audit-later-the-same-day).
- Pulling the cable at the PC end as opposed to the tablet end (which end was pulled was not
  recorded), a hub, a different cable, a second device.
- Rotation, and the tablet's screen turning off during a stream.
- A hostile or malformed peer against the running apps, as before.

### D. The full-screen capture freeze

Step 3 reported that the capture API delivered nothing while a browser showed a full-screen video.
The question was whether that can be reproduced, and whether frames stop arriving or are being
discarded.

**Method.** `probe` on one monitor while something is shown on that monitor in a known way for
about ten seconds, with nothing shown before and after. What was shown reported its own rate: the
pattern counts its presents, and the browser page was a local file whose only content is a looping
60 fps video and which writes the player's own frame count into its window title, read from outside
the browser. Chrome was a separate instance with a throwaway profile, so the browser in use on the
machine was not involved.

**What each second of silence means.** With a still screen the capture API raises no events at all.
That is how it is designed, it is what the idle phases of every run below show, and it is the first
thing to rule out whenever "capture froze" is reported.

| Shown | How | External monitor (NVIDIA) | Laptop panel (Intel) |
| --- | --- | --- | --- |
| Test pattern | window, blt model | 74 events/s, 60 encoded | 79 events/s (the pattern managed only 79 pictures/s here), 60 encoded |
| Test pattern | window, flip model | 75, 60 encoded | 94, 60 encoded |
| Test pattern | full screen, blt model | 74, 60 encoded | 57, 56 encoded (source again 79/s) |
| Test pattern | full screen, flip model | 73, 60 encoded | 100, 60 encoded |
| `ffplay`, 60 fps clip | window | 70, 59 encoded | 54.5, 53.5 encoded |
| `ffplay`, 60 fps clip | full screen | 55, 53 encoded | 54, 53 encoded |
| Chrome, 60 fps video | window | 74.5 and 75.5 (2 runs) | 62 to 70 (4 runs) |
| Chrome, 60 fps video | full screen | 59 to 60 (4 runs) | 59.5 to 62.5 in 13 runs; **starved in 4** (medians 12.5, 23.5, 33.5, 32.5) |
| Chrome, 60 fps video, `--disable-direct-composition-video-overlays` | full screen | not run | 59.5 to 63 in all 12 runs |

Figures are the median number of frame-arrived events per second during the phase.

**The one run that starved** (`d8-chrome-laptop-fullscreen`, the first full-screen Chrome run on
the laptop panel). For the ten seconds measured, Chrome's own counter advanced by 660 frames with
none newly dropped: it was showing 60 frames a second. In the same seconds Windows raised between 3
and 44 frame-arrived events a second, median 12.5, including five consecutive seconds of 3 to 5.
This program paced out at most 7 of those in any second. The encoder produced a median of
412 kbit/s where every normal run produced about 11,800, so what did arrive had hardly changed
from one frame to the next. The same command repeated eight more times that afternoon behaved
normally every time.

**It came back five hours later.** During the audit the first three full-screen runs on the laptop
panel starved again, each with a brand-new browser profile, and each alternating with a run that
had video overlays disabled and was normal. Then it stopped: the next two default runs were normal,
and so were three more that played for 45 seconds each instead of 10.

| Run (laptop panel, full screen, default flags) | Browser's own rate | Capture events per second while it played | Encoded |
| --- | ---: | --- | ---: |
| `d8`, 15:19 | 60 fps | 3 to 44, median 12.5; a 5 s stretch of 3 to 5 | 412 kbit/s |
| `g0-check`, 20:12 | 60 fps | 4 to 46, median 23.5; a 5 s stretch of exactly 4 | 308 kbit/s |
| `g1-fresh-default-1`, 20:13 | 60 fps | 14 to 54, median 33.5; a 5 s stretch of 14 to 18 | 1470 kbit/s |
| `g1-fresh-default-2`, 20:14 | 60 fps | 17 to 54, median 32.5; a 4 s stretch of 17 to 19 | 1344 kbit/s |
| the other 13 | 50 to 61 fps | medians 59.5 to 62.5; lowest single second 43 | about 11,900 kbit/s |

All three evening runs had the same shape: about twelve seconds of an irregular 25 to 57 events a
second from the moment the browser opened, then a drop to a flat low rate that lasted until the
browser was closed. That low rate, 4 in one run and about 17 in the others, is close to what the
same monitor produced with the browser gone (2, and 12 to 18), which is to say that in those
seconds the video itself seems to have contributed no capture events at all. A normal run sits at
60 from its second second.

**What this establishes**

- The starvation is real and it is upstream of this program. When it happens Windows raises few or
  no capture events; the pacer and the encoder are not the ones discarding. That question is
  closed.
- It is not caused by full-screen presentation as such. A full-screen flip-model swap chain, which
  is what a game presents, was captured normally on both monitors, as was full-screen `ffplay`.
- It is intermittent and it clusters: 4 of 17 identical runs, one in the afternoon and three in a
  row in the evening, with long runs of normal behaviour on either side.
- It is not backpressure from this program. In the starved seconds no frame was overtaken, none
  arrived to an empty pool, and the encoder returned as many pictures as it was given, each second.
  This program holds at most two of the capture pool's three buffers. What is low is the number of
  events Windows raises.
- It was not an invalid reproduction: in every run the browser was a separate window whose position
  was read back from Windows, the page reported itself visible, and its own counter showed it
  presenting 60 frames a second.

**What it does not establish**

- **The cause.** Every run that starved was hardware-decoded browser video, full screen, on the
  adapter that does not do the encoding. That is the situation in which Windows can put video on a
  hardware overlay plane instead of composing it, and a capture that follows composition would then
  see nothing change. The evidence now leans that way and does not get there: with Chrome's video
  overlays disabled 12 runs of 12 were normal, against 13 of 17 with them enabled, and in the
  evening cluster the three starved runs alternated with two overlay-disabled runs that were
  normal. But 4 in 17 against 0 in 12 is within what chance allows (about one time in ten), the
  starved runs came in clusters whose trigger is unknown, and nothing here observed which plane the
  video was on. It remains a hypothesis.
- **What starts and ends a cluster.** A new browser profile was suspected and ruled out: three
  starved runs and five normal ones all used new profiles. Playing for longer was suspected and
  ruled out for 45 seconds. Whatever else was on that monitor differed between the times and was
  not controlled.
- **Whether it is what step 3 saw.** That was YouTube, full screen, in the everyday browser, on the
  external monitor. Full-screen browser video on the external monitor was normal in four runs here.
  The original scenario itself was not re-run: an attempt to do so through the everyday browser was
  discarded when a one-frame check showed the test tab had been opened behind another tab, so that
  nothing on screen was changing. Without that check it would have been reported as a reproduction.
- Whether a paused video, a still screen, or protected content was involved in the original
  observation. Any of them would also have produced a still picture.

**Also learned.** `ffplay` showing a 60 fps clip gave the capture API 54 to 55 frames a second in
the three phases where it was the only thing changing (53 encoded). In the fourth, a window on the
external monitor with the desktop around it also changing, 70 events arrived and 59 were encoded.
It is not a 60 fps source, which is what step 3 suspected.

**Status: unresolved, retained as an open investigation.** No workaround was applied, and none
should be on this evidence: turning overlays off is a browser launch flag, not something a mirroring
product can ask of its users, and it has not been shown to be the cause.

**Why no presentation trace was taken.** The tool that would settle it records, for each present,
whether it was composed, flipped independently or put on an overlay plane. That is PresentMon,
which reads kernel graphics events. It is not installed on this machine, and reading those events
needs an elevated session or membership of the Performance Log Users group; the session this work
ran in has neither, and changing that was not something to do without being asked. A process named
`PresentMonService` is running on the machine, evidently installed by other software, but it offers
no command-line interface and was left alone.

**Next step, as a procedure**

1. In an elevated PowerShell, with PresentMon downloaded from its publisher (Intel, GameTechDev):
   `PresentMon-x64.exe --process_name chrome.exe --output_file presents.csv --timed 40 --terminate_after_timed`.
   Its `PresentMode` column says, per present, `Composed: Flip`, `Hardware: Independent Flip`,
   `Hardware Composed: Independent Flip` (an overlay plane) and so on.
2. In an ordinary prompt at the same moment:
   `ExtendSpace.CaptureEncode.exe probe --monitor 1 --duration 40 --output-size 1920x1080 --fps 60 --no-border`.
3. Start the video: a separate Chrome with its own `--user-data-dir`, `--start-fullscreen`, opening
   the test page kept with the evidence (`scripts/testpage.html`, which loops a 60 fps clip and
   writes the player's frame count into the window title). `scripts/d_chrome.sh` does steps 2 and 3
   and records the phase times.
4. Repeat until a run starves; it took between one and nine tries here. Then compare the
   `PresentMode` of the seconds in which `probe` shows few events with the seconds in which it
   shows 60. If the starved seconds are overlay presents and the normal ones are composed, the
   hypothesis is confirmed; if both are composed, it is refuted.
5. Run it also with `--monitor 0` on the external monitor, and with the original scenario, YouTube
   full screen in the everyday browser, in front.

### E. Glass-to-glass latency

**What was measured.** How far the picture on the tablet's glass is behind the picture on the PC
monitor's glass at the same instant, with the tablet mirroring that monitor at 1080p60 over USB.
That is what someone looking at both screens sees. It was measured with a camera and with nothing
from either device's clock.

**Setup.** The pattern on the external monitor, streamed to the tablet (`e1-camera`, 516 s,
59.98 fps, 30,983 frames sent and 30,983 received). The tablet propped in front of the monitor so that one phone camera saw
both. A 7.04 s clip recorded in the phone's slow-motion mode: 845 frames, which the file's own
metadata gives as captured at 120 frames a second, so 8.33 ms per camera frame.

**Reading it.** The pattern's stepping cell is the frame number modulo 30, so in any one camera
frame the lit cell on the monitor and the lit cell on the tablet differ by the number of pattern
frames the tablet is behind. For every camera frame, a script took the brightness along the
stepping row on each screen and found the brightness-weighted position of what was lit. Where each
of the 30 cells lies in the picture was learned from the clip itself, from where lit cells were
seen over the seven seconds, so no measurement of the picture by hand went into it. The difference
in cells was turned into time with the pattern's own measured rate during that run, 143.6 pictures
a second (6.96 ms each). Camera frames in which the monitor's lit cell was outside the picture (the
window's right-hand end was cut off) or was wrapping from the last cell to the first were left out:
411 of 845. Which frames those are depends on the frame number, not on the latency.

**Result**, 434 camera frames:

| Tablet's glass behind the monitor's glass | |
| --- | ---: |
| Median | 45.7 ms |
| Mean | 45.7 ms |
| 5th to 95th percentile | 38.8 to 53.3 ms |
| Minimum, maximum | 32.6, 66.0 ms |

In the same seconds the pipeline's internal figure, capture hand-over to release to the tablet's
surface, had a mean of 19 to 20 ms.

**How far to trust it**

- Each reading is good to about one pattern frame, ±7 ms. The monitor shows a new picture every
  6.9 ms and a camera frame lasts 8.3 ms, so most camera frames catch two pictures on the monitor
  and, since the tablet changes picture 60 times a second, often two on the tablet. The weighted
  position averages over that. The phone's sensor is also read out line by line, which can shift
  the two screens against each other by a few milliseconds in any one frame, in either direction.
- The spread is real, not noise. The tablet holds each picture for 16.7 ms while the monitor moves
  on every 6.9 ms, and the 60 frames sent each second are 14 to 28 ms apart. The lag therefore
  rises and falls by about that much many times a second.
- It is a comparison of two screens. The monitor's own delay from composition to light is not
  separated out; a faster or slower monitor would move the figure.
- **One clip, seven seconds, one camera position, one phone.** Two stills were also checked by hand
  and agree with the script: in one the two frame counters read 6 apart (42 ms), in another the lit
  cells were 4.5 to 8.5 apart with the tablet caught between two pictures (31 to 59 ms). Reading the
  clock digits by eye was tried and abandoned: two blended seven-segment digits read as a third, and
  the readings disagreed with the counters beside them.
- The exposure time of the camera is not known, and 120 frames a second is the file's claim.

**So: about 46 ms glass to glass in this setup, of which the pipeline's own measured part is about
20 ms.** The other 26 ms or so is the two displays: what the tablet does between the app releasing
a frame and its panel showing it, less what the monitor does between Windows composing a frame and
its panel showing it. Those were not measured separately. This is a first measurement, not a
characterisation: it wants repeating at several moments, with a camera whose timing is known, and
at 720p30.

**The four figures, kept apart**

| Figure | From | To | Value here | How |
| --- | --- | --- | --- | --- |
| Host stage | Windows hands the frame to the host | encoded picture reaches the sender | 2.2 ms mean | one clock |
| Internal pipeline | Windows hands the frame to the host | tablet app releases the decoded frame to its surface | 19.7 ms mean over ten minutes | two clocks related by ping, ±1.2 ms |
| Tablet display time | release to the surface | light from the tablet's panel | **not measured**; Android's callback does not report it on this device | — |
| Glass to glass | light from the monitor | light from the tablet | 45.7 ms median, one clip | camera |

### Defects found, and what was done

| # | Found by | Defect | Fix | Regression test |
| --- | --- | --- | --- | --- |
| 1 | Reading the lifecycle; confirmed with adb | The adb forward was made once; adb drops it when the device disconnects, so no reconnection after a real disconnection could succeed. | The session restores its transport before each new attempt. | Two host-session tests; confirmed on the device by cable pull 2 and by `c1-forward-lost`. |
| 2 | New test | Stopping during a stalled handshake blocked for the 5 s handshake timeout and returned with the session thread still alive. | Cancellable handshake waits and connect. | Host-session test. |
| 3 | New test | Stopping with a write blocked took 2.5 s and wrote a goodbye onto a socket another thread was still writing to. | The socket is closed to end the blocked write; no last message is attempted. | Host-session test. |
| 4 | New test | A session could be started twice. | Refused. | Host-session test. |
| 5 | Device run `c2` | A host refused by a busy app was told only that no Hello came. | The message says another stream may be connected. | Host-session test. |
| 6 | Tablet log | The app logged its own closing as the host hanging up. | Wording depends on which side closed. | Android unit test; confirmed on the device in the audit. |
| 7 | Device test in the audit | When the host went silent the app's log gave the reason as "Try again", the operating system's text for a read that timed out. | It now says that nothing arrived for 5 s. | Android unit test; confirmed on the device. |
| 8 | Reading the code, in the final review | Closing the live screen said goodbye, closed the socket and waited for the session's threads on the UI thread. A write to a host that is not reading blocks for the socket's 5 s send timeout, and there are two such writes, behind a lock the statistics timer may be holding in a write of its own. | Stopping returns at once and the work runs on its own thread. The goodbye gets 500 ms; then the socket is closed whatever happened, which also frees any write still stuck. Opening the screen again retries taking the socket's name for up to 2 s, since the previous session may still be finishing. | Seven Android unit tests for the two pieces of logic; confirmed on the device. |

Also changed, not defects: the host's loss message and the app's log now say how long before the
loss the connection last showed life, which is where the "noticed within" figures come from; the
session's timeouts can be set by tests; the capture code counts frame-arrived events as well as
frames, for `probe`.

Two mistakes in the test harness itself, caught before they reached a result: the crash check
counted another app's crash on the tablet as ours until it was made to look at the package name;
and the background browser tab described under D.

### Build and test results

| Command | Result |
| --- | --- |
| `dotnet build ExtendSpace.slnx` (in `apps/windows`) | Succeeded, 0 warnings, 0 errors |
| `dotnet test --solution ExtendSpace.slnx` | 355 passed, 0 failed, 0 skipped (24 for the app's core, 331 for the experiment; 299 before this phase, 353 before the audit). The suite was run repeatedly while it grew; the one intermittent failure seen was a race in a new test's own setup, since fixed. |
| `git diff --check` | Clean |
| `.\gradlew.bat assembleDebug testDebugUnitTest lintDebug` (in `apps/android`) | Succeeded. 91 unit tests passed, 0 failed, 0 skipped (82 before this phase, 83 before the audit, 84 after it). Lint: 0 errors, 1 warning, the Gradle-version notice as before |
| `.\gradlew.bat assembleRelease` | Succeeded. Release manifest: one activity, `MainActivity`. No `livestream`, `LiveStream`, `decodertest`, `DecoderValidation` or `org/json` string in the release dex (27 lines match `livestream` in the debug dex). No network permission. |
| `adb install -r app-debug.apk` | Succeeded. The soak, the cable pulls and the camera clip used the build before the last two log-message changes and the stop-path change; the final build was installed and exercised afterwards, as described under the audit and the final review. |

Baseline before any change, on the same day: 299 and 82 passing, lint 0 errors and 1 warning.

### Limitations

- One PC, one tablet, one cable, one day; debug builds; a machine in use.
- Ten minutes is the longest run. The tablet app's memory stepped up once in it.
- The source ran at 144 pictures a second. A source at exactly 60, the case the pacer's uneven
  spacing would matter most for, cannot be produced on this monitor.
- 720p30 was not re-measured in this phase; its only results are step 3's.
- Glass to glass is one seven-second clip.
- The capture starvation is unexplained and can evidently happen without warning.
- No presentation trace exists: the session had no rights to take one.

### Still pending on a device

1. The capture starvation with presentation events being recorded, as set out under D, including
   the original YouTube full-screen scenario.
2. A second glass-to-glass clip, at another moment and at 720p30.
3. Rotation, and the tablet's screen turning off during a stream.

Done in the audit and no longer pending: the final debug build on the device, and a link that goes
silent on each side.

### Audit, later the same day

The working tree was re-read against this section, the tests were re-run, and three things left
open above were followed up with the tablet attached again. Corrections it produced are made in
place above; what it added is here.

**The working tree** was as described: the same commit, the same 21 modified files and 4 new
paths, nothing staged, nothing under `artifacts/` tracked, no local path or credential in the
changes. One figure in this section was wrong and is corrected: `ffplay` gave the capture API 54 to
55 frames a second, not "53 to 59", which mixed in the encoder's count. The tablet's adb serial,
which had been written into three commands here, is now `<serial>`.

**A link that goes silent with the socket still open**, 720p30, final build (`f3-silent-link`, run
twice with the same result):

| | Host goes silent | App goes silent |
| --- | --- | --- |
| How | The host process was suspended for 9.4 s. adb, the cable and the socket stayed up. | The app's process was stopped (`kill -STOP` through `run-as`) for 9.2 s. |
| Who has to notice | The tablet | The host |
| What it logged | "Connection ended 5021 ms after the last bytes from the host arrived: … nothing arrived from it for 5 s." | "connection lost: nothing was received from the client for 5 s (noticed 5002 ms after the last message from the client …)" |
| Cleanup | The app closed the connection, released its decoder and listened again. | The host's writer was blocked on the stalled app; the host closed the socket to end that write and logged that no final message could be sent. No encoded frame was left unaccounted for. |
| Recovery | When the host was resumed it found the connection gone and was `Streaming` again 0.3 s later. | The host retried while the app was stopped; it was `Streaming` again 0.6 s after the app was continued. |

So the 5 s timeout works on a device on both sides, to within about 30 ms, and each side cleans up
and carries on. The app did not crash and its process was the same throughout. This is a frozen
process, not a cable that has stopped carrying data while both ends are healthy; the second was not
produced, and both ends would see it the same way.

Two deterministic tests were added on the host for what the device showed: a client that keeps
sending is never timed out, and a client that has stopped both sending and reading is still timed
out, has its socket closed, and is replaced by the next client.

**The final debug build** was installed and run: 1080p60 for 30 s, 1798 frames sent, received and
released, 59.92 fps; then a 720p30 stream ended with the Back key, which the host reported as the
client ending the session and the tablet logged as "Stopped: the live screen was closed."

**The capture starvation** was reproduced three more times and is written up under D.

### Final review: leaving the live screen

The audit noted that the live screen's stop path wrote to the socket from the UI thread. Traced
through: the Back key finishes the activity, the view model is cleared on the main thread, and that
called the session's `stop()`, which sent the final statistics and the goodbye, closed the socket,
and then waited up to 3 s for the accept thread. The writes are ordinary blocking writes on a local
socket whose send timeout is the 5 s keepalive value. They return at once while the socket's buffer
has room, which is why nothing was ever seen to stall, and they block when it is full, which is
what a host that has stopped reading eventually produces. Worst case the UI thread could have waited
for a lock held by the statistics timer in its own blocked write, then 5 s for each of two writes,
then the 3 s join. That was not reproduced on the device: freezing the host for a few seconds does
not fill the buffers. It is a defect by construction, not by observation.

The fix is the smallest that removes the wait: `stop()` marks the session stopped and returns, and a
thread of its own does the rest. The goodbye is given 500 ms; then the socket is closed regardless,
and closing it is what releases a write that is stuck, on any thread. Because the old session may
now still be finishing when the screen is opened again, taking the socket's name is retried for up
to 2 s instead of failing at once.

On the tablet, with the build that has the change (`h1` to `h3`, 720p30):

| Check | Result |
| --- | --- |
| Back key with a healthy host | The host still received the goodbye and ended "the client ended the session"; all 260 frames it sent were received. The launcher was in front 0.7 s after the key. |
| Back, then the screen opened again immediately, then a stream | Listened and streamed: 241 frames sent and received. |
| Back with the host process frozen | The launcher was in front 0.7 s after the key. When the host was resumed 3 s later it found the goodbye waiting and ended in an orderly way. |
| Through all three | No crash, no "application not responding", no skipped-frame warning for the app. |

### Is it reliable enough for the next architecture phase?

For moving this code out of the experiment and behind the session coordinator and the connection
controller: **yes, as a starting point, with three things carried along rather than left behind.**

What supports that: with a real source the pipeline held its target for ten minutes without losing
a frame or drifting; it survived an actual cable pull and came back unaided; the lifecycle defects
found were small, specific and now tested; and the latency someone would see was measured once and
is about 46 ms.

What has to be dealt with first, in this order:

1. **The capture starvation.** A mirror that can silently show a still picture while video plays is
   a product-level fault, and its trigger is unknown. It needs the presentation trace described
   under D before Mirror mode is called working, and probably a fallback (Desktop Duplication, or
   detecting the condition) once it is understood.
2. **Nothing further on the silent link.** The timeouts were exercised on the device on both sides
   in the audit, and the live screen's stop path, which used to do its socket writes on the UI
   thread, was moved off it in the final review. What still runs on the UI thread is releasing the
   decoder when its surface goes away, which has to finish before the surface does.
3. **A longer soak**, thirty minutes to an hour, with the tablet's memory and temperature recorded,
   before anyone depends on the ten-minute result.

Not blockers, but decisions the next phase will have to make: what to do about frame pacing when
the monitor's rate is not a multiple of the stream's, and whether 46 ms is the target or the
starting point.

## Prerequisites

| Prerequisite | State on the development machine |
| --- | --- |
| .NET 10 SDK | Installed (10.0.401); see [setup](../development/setup.md). |
| Windows 11 with a GPU that exposes a hardware H.264 encoder | Checked: Intel Quick Sync for the laptop panel's adapter, used in step 1; NVIDIA for the adapter driving an external monitor, used in step 2a. |
| A way to call Media Foundation and Direct3D 11 from C# | Decided for the experiment: CsWin32. Revisit before it goes into the host. |
| `ffmpeg` / `ffprobe`, to validate streams | Installed (build 2026-01-26), on `PATH`. |
| `adb` | Installed (platform-tools 37.0.1) but not on `PATH`; found through `ANDROID_HOME`. |
| A physical Android device with USB debugging enabled by its owner | Connected for steps 2a and 3: a Xiaomi Pad 7 (`2410CRP4CI`), Android 16. On HyperOS, *Developer options → Install via USB* must also be on or `adb install` is refused. The emulator has not been used. |
| Android app builds | Yes. |

## What counts as success

For the whole experiment. Where each stood after step 3, and then after Phase 2.4:

- The Android screen shows the live Windows desktop, recognisably and continuously, for ten minutes
  without a crash, a freeze or unbounded memory growth on either side.
  **Not met.** Three minutes at 1080p and two at 720p, without a crash or a lost frame. Ten minutes
  was not attempted, and memory on the tablet was not measured.
  *Phase 2.4:* **met** for one ten-minute run at 1080p60: no crash, no freeze, no lost frame; host
  memory grew by about 5 MiB and the tablet app's moved between 175 and 205 MB without a trend.
- Unplugging the cable ends both sides cleanly; replugging and restarting works without restarting
  the apps.
  **Not tested as written.** The app being killed and restarted was: the host reconnected by itself
  and the stream resumed. The cable was not pulled.
  *Phase 2.4:* **met**, after a fix. The cable was pulled twice during a stream. Both sides noticed
  within 40 ms; with the cable back inside the reconnect budget the stream resumed unaided, and
  outside it the host ended with a clear failure and a fresh host worked against the same app.
- Rotating the device or backgrounding the app and returning recovers the picture (keyframe request
  after the surface is recreated).
  **Met for backgrounding**, with the `surface_recreated` keyframe request seen in the host's log.
  Rotation was not tried.
  *Phase 2.4:* unchanged. Rotation is still untried.
- The run records which encoder and decoder were actually used. Until that line says "hardware",
  nothing may be described as hardware-accelerated.
  **Met.** Every report names both; in the sustained runs both were hardware.

No numeric target is set before the first measurement; the experiment produces the baseline that
later targets are set against.

## Measurements

| Metric | How | Step 1 | Step 3 |
| --- | --- | --- | --- |
| Resolution | Reported by the capture item and confirmed by the decoder's output format. | Measured; confirmed by ffprobe, and in step 2a by the tablet decoder's output format. | Measured on the host; the client parses it from the SPS. |
| Capture frame rate | Count of frames delivered by the frame pool per second. | Measured, as an average over the run. | Measured, per second and over the run. |
| Encoded frame rate and bitrate | Count and bytes of access units leaving the encoder per second. | Measured, as averages over the run. | Measured, per second and over the run, as transmitted. |
| Encode time | QPC time from texture submitted to sample received. | Measured ("encoder turnaround"). | Measured, and also from the capture hand-over. |
| Dropped frames (host) | Frames skipped because the send queue was full. | There is no send queue yet. Frames dropped ahead of the encoder are counted. | Counted by cause. Zero in every run. |
| Dropped frames (client) | Gaps in `sequence`, plus decoder outputs discarded. | Not applicable. | Gaps counted, and frames discarded before the decoder. Decoder outputs are never discarded. |
| Decode time | Time from `queueInputBuffer` to the matching output buffer. | Not applicable. Step 2a counted frames over elapsed time; it did not time individual frames. | **Not measured on its own.** Receive → release (about 11 to 14 ms) contains it. |
| Transport round trip | `ping`/`pong`. | Not applicable. | Measured: best 2.2 to 4.5 ms. |
| Capture → released to the tablet's surface | Host timestamp in each frame, clocks related by `ping`/`pong`. Added in step 3. | Not applicable. | Measured: mean 15.7 ms at 720p30, 19.0 ms at 1080p60, ± half a round trip. |
| **Glass-to-glass latency** | Display a running millisecond counter on the Windows monitor, photograph or film the monitor and the phone together with a high-frame-rate camera, and subtract. This is the only measurement that includes capture, display scan-out and panel delay, and the only one to quote. | Not measurable until step 3. | **Not measured.** No camera was used. |

Phase 2.4 added to this table: the internal figure over ten minutes at 1080p60 (mean 19.7 ms), and
a first glass-to-glass measurement from one 120 fps camera clip, a median of 45.7 ms at 1080p60.
The pattern's stepping cell was used rather than its millisecond counter, because blended digits
could not be read reliably; see [workstream E](#e-glass-to-glass-latency).

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
   **Partly answered.** From an unpackaged console app it delivered frames at the panel's full
   refresh interval (6.06 ms) when allowed to, and Windows granted the borderless request. That the
   border is really gone has not been confirmed by eye. Step 3 adds two cautions: the capture API's
   own rate limit rounds to whole refreshes, so the experiment now paces frames itself, and capture
   delivered nothing during one full-screen video (unexplained; see
   [step 3](#an-observation-about-capture-unexplained)). Phase 2.4 saw the capture API starve
   again, in 4 of 17 identical full-screen browser runs, and showed that it is Windows raising
   no events rather than this program discarding frames; the cause is still unknown
   ([workstream D](#d-the-full-screen-capture-freeze)).
2. Which interop approach to Media Foundation is workable from C#?
   **Answered for the experiment:** CsWin32; see [Interop findings](#interop-findings).
3. What is the baseline glass-to-glass latency over ADB, and which stage dominates it?
   **Half answered.** Glass to glass is still unmeasured. From Windows handing over a frame to the
   tablet releasing the decoded picture took a mean of 15.7 ms at 720p30 and 19.0 ms at 1080p60,
   and of that the tablet's receive-to-release stage was the largest part (11 to 14 ms), against
   1.4 to 2.2 ms to encode and about 3 ms in transit. The stages outside that interval, the two
   displays above all, may well be larger than everything inside it.
   **Phase 2.4, one camera clip:** about 46 ms from the monitor's glass to the tablet's at 1080p60,
   of which the measured pipeline is about 20 ms. So the displays are indeed the larger part.
4. Is one TCP stream over ADB adequate at the bitrates needed, or does throughput cap quality?
   **Adequate at what was tried.** Up to 11.6 Mbit/s on average, with seconds of 20 to 29 Mbit/s,
   crossed in about 3 ms with the send queue never deeper than one frame. Higher bitrates, larger
   pictures and a busier USB bus were not tried.
5. How do common Android hardware decoders behave with a long-GOP, no-B-frame stream and
   low-latency mode?
   **One data point.** A Qualcomm SM7675 tablet's hardware decoder decoded every picture of five
   such files (keyframes every 120 pictures, no B-frames) at up to 2560 × 1600, and so did its
   separate low-latency decoder at 1920 × 1080. Live, in step 3, the same decoder took a 2 s
   keyframe interval with forced keyframes at every (re)connection and after a surface change
   without an error; its first frames took up to about 140 ms from capture to release while it
   started. The
   low-latency decoder's mean was about 2 ms lower in one 30 s run, which is within the noise of
   that comparison. Every other device is still open.

## Known product-level concern, out of scope here

ADB requires the user to enable Developer options and USB debugging. That is acceptable for an
experiment and for early adopters, and is a real usability and security cost for a general release.
Alternatives (Android Open Accessory, USB tethering with the Wi-Fi transport) should be weighed once
the pipeline itself is proven.
