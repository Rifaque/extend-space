# Experiment 01 — USB mirroring, end to end

Status: **step 1 is done, and so is the first half of step 2.** A real Windows display was captured,
encoded to H.264 by a hardware encoder and written to a `.h264` file that an independent decoder
reads without error; see [Step 1](#step-1--capture-and-encode-to-a-file). Files from that pipeline
were then copied to a physical Android tablet and decoded and shown by its hardware decoder; see
[Step 2a](#step-2a--decode-a-captured-file-on-android). The rest is not started: nothing has been
framed, sent over ADB or streamed live, and no latency has been measured.

Goal: prove the riskiest assumptions of Mirror mode with the least code — that a Windows display can
be captured, hardware-encoded to H.264, carried over ADB, hardware-decoded on Android and shown on a
`Surface`, with latency and frame rate good enough to keep going.

This is an experiment, not the product pipeline. Its code is throwaway unless it turns out clean; its
findings are the deliverable.

```
Windows display ─► capture ─► H.264 encode ─► framing ─► adb forward ─► framing ─► MediaCodec ─► Surface
└──────────── step 1: exists ───────────┘   └────────── design only ─────────┘   └── step 2a: exists ──┘
                                        └────────── .h264 file, copied with adb push ──────────┘
```

## Boundaries and proposed APIs

The first two rows are what step 1 implemented, as proposed. The rest is still a proposal.

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
   transport and decoding with known-good input. **Half done:** the file has been decoded on a
   device from storage ([step 2a](#step-2a--decode-a-captured-file-on-android)). Sending it through
   the socket with framing is not started.
3. **Live.** Connect step 1's encoder to step 2's sender. **Not started.**

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
trade-off was not explored.

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
protocol's framing, is the current [next step](#next-step).

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

### Next step

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

## Prerequisites

| Prerequisite | State on the development machine |
| --- | --- |
| .NET 10 SDK | Installed (10.0.401); see [setup](../development/setup.md). |
| Windows 11 with a GPU that exposes a hardware H.264 encoder | Checked: Intel Quick Sync for the laptop panel's adapter, used in step 1; NVIDIA for the adapter driving an external monitor, used in step 2a. |
| A way to call Media Foundation and Direct3D 11 from C# | Decided for the experiment: CsWin32. Revisit before it goes into the host. |
| `ffmpeg` / `ffprobe`, to validate streams | Installed (build 2026-01-26), on `PATH`. |
| `adb` | Installed (platform-tools 37.0.1) but not on `PATH`; found through `ANDROID_HOME`. |
| A physical Android device with USB debugging enabled by its owner | Connected for step 2a: a Xiaomi Pad 7 (`2410CRP4CI`), Android 16. On HyperOS, *Developer options → Install via USB* must also be on or `adb install` is refused. The emulator has not been used. |
| Android app builds | Yes. |

## What counts as success

For the whole experiment. Step 1 alone meets none of these; they need steps 2 and 3.

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

| Metric | How | Step 1 |
| --- | --- | --- |
| Resolution | Reported by the capture item and confirmed by the decoder's output format. | Measured; confirmed by ffprobe, and in step 2a by the tablet decoder's output format. |
| Capture frame rate | Count of frames delivered by the frame pool per second. | Measured, as an average over the run. |
| Encoded frame rate and bitrate | Count and bytes of access units leaving the encoder per second. | Measured, as averages over the run. |
| Encode time | QPC time from texture submitted to sample received. | Measured ("encoder turnaround"). |
| Dropped frames (host) | Frames skipped because the send queue was full. | There is no send queue yet. Frames dropped ahead of the encoder are counted. |
| Dropped frames (client) | Gaps in `sequence`, plus decoder outputs discarded. | Not applicable. |
| Decode time | Time from `queueInputBuffer` to the matching output buffer. | Not applicable. Step 2a counted frames over elapsed time; it did not time individual frames. |
| Transport round trip | `ping`/`pong`. | Not applicable. |
| **Glass-to-glass latency** | Display a running millisecond counter on the Windows monitor, photograph or film the monitor and the phone together with a high-frame-rate camera, and subtract. This is the only measurement that includes capture, display scan-out and panel delay, and the only one to quote. | Not measurable until step 3. |

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
   border is really gone has not been confirmed by eye.
2. Which interop approach to Media Foundation is workable from C#?
   **Answered for the experiment:** CsWin32; see [Interop findings](#interop-findings).
3. What is the baseline glass-to-glass latency over ADB, and which stage dominates it?
   Open. The one stage measured so far is the encoder, at about 11 ms per frame on this machine.
4. Is one TCP stream over ADB adequate at the bitrates needed, or does throughput cap quality?
   Open. For scale: the captures here averaged 0.6 to 10.7 Mbit/s against a 12 Mbit/s target.
5. How do common Android hardware decoders behave with a long-GOP, no-B-frame stream and
   low-latency mode?
   **One data point.** A Qualcomm SM7675 tablet's hardware decoder decoded every picture of five
   such files (keyframes every 120 pictures, no B-frames) at up to 2560 × 1600, and so did its
   separate low-latency decoder at 1920 × 1080. What low-latency mode does to latency is still open,
   as is every other device.

## Known product-level concern, out of scope here

ADB requires the user to enable Developer options and USB debugging. That is acceptable for an
experiment and for early adopters, and is a real usability and security cost for a general release.
Alternatives (Android Open Accessory, USB tethering with the Wi-Fi transport) should be weighed once
the pipeline itself is proven.
