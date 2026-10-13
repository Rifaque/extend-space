# Capture → H.264 experiment

The Windows side of [streaming experiment 01](../../../../docs/architecture/streaming-experiment-01.md):
capture a real Windows display, encode it with a Media Foundation H.264 encoder, and either write a
raw `.h264` elementary stream ([step 1](../../../../docs/architecture/streaming-experiment-01.md#step-1--capture-and-encode-to-a-file))
or send it live to the Android debug build over USB
([step 3](../../../../docs/architecture/streaming-experiment-01.md#step-3--live-stream-over-adb-forward)).
It is a standalone console program; the ExtendSpace app does not use it.

```powershell
cd apps/windows
dotnet run --project experiments/ExtendSpace.Experiments.CaptureEncode -- list
dotnet run --project experiments/ExtendSpace.Experiments.CaptureEncode -- capture --duration 10
dotnet run --project experiments/ExtendSpace.Experiments.CaptureEncode -- capture --output-size 1920x1080
dotnet run --project experiments/ExtendSpace.Experiments.CaptureEncode -- --help
```

The output file is a recording of your screen. `*.h264` is git-ignored; keep it that way.

To stream live, connect the device with USB debugging on, open "ExtendSpace live test" on it (a
debug build of the Android app), then:

```powershell
dotnet run --project experiments/ExtendSpace.Experiments.CaptureEncode -- stream --output-size 1920x1080 --fps 60 --duration 60
```

The program runs `adb forward tcp:27183 localabstract:extendspace`, connects to that loopback port,
and removes the forward when it stops. It opens no listening socket of its own. `adb` is found
through `ANDROID_HOME` or `PATH`, or given with `--adb`. If the connection is lost it sets the
forward up again and reconnects, for up to `--reconnect-seconds`.

Two more commands help with measuring. Neither sends or keeps anything:

```powershell
# A test pattern that changes on every display refresh, with a millisecond clock and a frame
# counter: a source that can be trusted. Esc or a right-click on it closes it.
dotnet run --project experiments/ExtendSpace.Experiments.CaptureEncode -- pattern

# What the capture API delivers each second, before and after this program's own pacing.
dotnet run --project experiments/ExtendSpace.Experiments.CaptureEncode -- probe --duration 30 --output-size 1920x1080
```

To check a file with ffmpeg, if you have it:

```powershell
ffmpeg -v error -xerror -framerate 60 -i <file>.h264 -f null -   # no output and exit code 0 = every frame decoded
ffplay -framerate 60 <file>.h264                                 # watch it
```

How it works, what it measured and what it does not prove are in the experiment document linked
above, not here.
