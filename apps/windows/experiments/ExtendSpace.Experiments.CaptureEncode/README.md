# Capture → H.264 experiment

Step 1 of [streaming experiment 01](../../../../docs/architecture/streaming-experiment-01.md#step-1--capture-and-encode-to-a-file):
capture a real Windows display, encode it with a Media Foundation H.264 encoder, and write a raw
`.h264` elementary stream. It is a standalone console program; the ExtendSpace app does not use it.

```powershell
cd apps/windows
dotnet run --project experiments/ExtendSpace.Experiments.CaptureEncode -- list
dotnet run --project experiments/ExtendSpace.Experiments.CaptureEncode -- capture --duration 10
dotnet run --project experiments/ExtendSpace.Experiments.CaptureEncode -- capture --output-size 1920x1080
dotnet run --project experiments/ExtendSpace.Experiments.CaptureEncode -- --help
```

The output file is a recording of your screen. `*.h264` is git-ignored; keep it that way.

To check a file with ffmpeg, if you have it:

```powershell
ffmpeg -v error -xerror -framerate 60 -i <file>.h264 -f null -   # no output and exit code 0 = every frame decoded
ffplay -framerate 60 <file>.h264                                 # watch it
```

How it works, what it measured and what it does not prove are in the experiment document linked
above, not here.
