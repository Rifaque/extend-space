# ExtendSpace

**More screen. More room. Wherever you work.**

ExtendSpace is an open-source second-screen solution by **RSX Develops** that turns an Android device into an additional display for Windows.

The project aims to support both screen mirroring and true extended-display functionality, with USB and wireless connectivity, hardware-accelerated video streaming, and a clean, native-feeling user experience.

### Planned capabilities

- Screen mirroring from Windows to Android
- True Windows virtual-display extension
- Wired USB and wireless local-network connections
- Hardware-accelerated video encoding and decoding
- Configurable resolution, frame rate, and streaming quality
- Secure device pairing and local-first operation
- Connection diagnostics and automatic recovery

### Project status

**Early development — architecture and feasibility stage.**

The initial development focus is establishing the streaming pipeline and validating Windows virtual-display integration. Features listed above are planned and should not be interpreted as already implemented.

**Beta candidate 0.2.0-beta.1 plus Phases 9–11: USB Mirror works from the apps' own screens; Wi-Fi Mirror with
secure pairing and touch input from the tablet are complete in code (hardware acceptance pending); Extend does not exist yet.**
On the tablet tap **Wait for PC** (turn on *Allow Wi-Fi connections* and *Pair with a PC* once for Wi-Fi);
on Windows choose USB or Wireless, the device and the display, and **Connect**. Touch input is off until
you choose Touch or Mouse under *Input from the tablet*.
Testers: start with [docs/beta/quick-start.md](docs/beta/quick-start.md). Builds:
[docs/release/release-process.md](docs/release/release-process.md). The production
session path has streamed a live 1080p60 picture from Windows to one Android tablet over USB, for up
to an hour, with recovery from cable pulls and a frozen app; the new screens and build 0.2.0-beta.1 have not yet
been run on hardware (see [ISSUES.md](ISSUES.md), ES-106, and the
[beta acceptance](docs/quality/beta-acceptance.md)). Status: [PROJECT_STATUS.md](PROJECT_STATUS.md);
plan: [ROADMAP.md](ROADMAP.md). What exists today:

| Area | State |
| --- | --- |
| Windows host app (WinUI 3): Connect screen with device list and choice, Connect for USB Mirror, diagnostics | Implemented; unit-tested; the screen not yet run on hardware |
| Android companion app (Jetpack Compose): Connect screen with Wait for PC, full-screen session screen, About | Implemented; unit-tested; the screens not yet run on hardware |
| Session state model on both platforms, with unit tests | Implemented |
| Protocol | Draft specification, unchanged. Implemented in the experiment, and in the production code of both apps (host side in `ExtendSpace.Core`, client side in the Android app's `protocol/`) |
| USB transport | Proven in the experiment and, since stages 3 and 4, in the production path on one PC and one tablet, including recovery from a pulled cable with video |
| Wi-Fi transport, pairing | Implemented (Phase 10): tablet listens, PC finds it by DNS-SD and connects; mutual TLS 1.3 with pinned keys; six-digit numeric-comparison pairing confirmed on both devices; Forget on both. Tested over loopback TLS on both sides and between the two real implementations on one PC; **not yet run over Wi-Fi with a tablet** |
| Touch input | Implemented (Phase 11): touches on the tablet's picture become real Windows touch (up to 10 fingers) or mouse input on the mirrored display, over USB or Wi-Fi; off by default. Mapping and lifecycle unit-tested; a touch carried end to end between the real implementations on one PC; **not yet injected on hardware** |
| Display capture and H.264 encoding | In the Windows host (`ExtendSpace.Media`, built from the experiment's code) behind the production session; hardware-encoded on one PC |
| Video decoding on Android | In the Android app (`link/`, `video/`, built from the debug experiment's code) behind the production session; decoded on one tablet. Started from the release session screen (and, for the gated tests, a debug-only harness) |
| Live mirroring, end to end | Production path: a test pattern streamed at 1080p60 for ten minutes, 36,081 frames sent and all decoded and released to the tablet's surface, none lost (stage 4, [section 19](docs/architecture/production-session-architecture.md#19-the-production-video-path-stage-4)); then, on the final build, a sixty-minute 1080p60 soak with all 216,212 frames drawn, the 720p30 fallback, Home, rotation, screen-off, cable pulls inside and beyond the retry budget and a frozen app (stage 4.1, [section 20](docs/architecture/production-session-architecture.md#20-physical-device-stabilisation-stage-41)). The experiment's earlier figures, including about 46 ms glass to glass, are in [experiment 01](docs/architecture/streaming-experiment-01.md#phase-24--reliability-and-latency-validation). One fault is open: Windows occasionally stops delivering captured frames while a browser plays video full screen; nothing yet detects it |
| Virtual display driver (Extend mode) | Not implemented; feasibility researched |

**Linux host (prototype):** `apps/linux` — the same session, pairing and protocol on Linux, with
GStreamer capture, the desktop portals on GNOME/KDE (Mirror, Extend, touch) and XTest on X11. Proven
from a real Linux host to the tablet over Wi-Fi; desktop acceptance is pending.
[Install](docs/beta/install-linux.md) · [architecture and parity](docs/architecture/linux-host-architecture.md).

**Extend (prototype):** with the free community [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)
installed (ExtendSpace does not install it), choose Extend on the PC: the tablet becomes an extra Windows
display, over USB or Wi-Fi, with touch. Proven on one PC and one tablet; ExtendSpace offers the best of the driver's
modes for the tablet (1920 × 1080 at 60 Hz for a 3:2 tablet) with one click. Details: [research](docs/research/extend-mode-driver-research.md),
[architecture](docs/architecture/extend-mode-architecture.md), [acceptance](docs/quality/extend-mode-beta-acceptance.md).

### Platforms

- Windows 11 — host application
- Android 10 or later — companion display application

### Repository layout

```
apps/
  windows/     C# / .NET 10 / WinUI 3 host, core library, experiments and tests
  android/     Kotlin / Jetpack Compose companion
protocol/
  specification/protocol.md
docs/
  architecture/   overview, IddCx feasibility, streaming experiment 01
  development/    setup, build and verification record
  security/       security model
```

### Building

Windows host (needs the .NET 10 SDK):

```powershell
cd apps/windows
dotnet build ExtendSpace.slnx
dotnet test --solution ExtendSpace.slnx
dotnet run --project src/ExtendSpace.Host
```

Android companion (needs a JDK and the Android SDK):

```powershell
cd apps/android
.\gradlew.bat assembleDebug testDebugUnitTest
```

Prerequisites, versions and what has actually been verified are in [docs/development/setup.md](docs/development/setup.md).

### Documentation

- [Architecture overview](docs/architecture/overview.md)
- [Protocol draft](protocol/specification/protocol.md)
- [IddCx feasibility for Extend mode](docs/architecture/iddcx-feasibility.md)
- [Experiment 01: USB mirroring end to end](docs/architecture/streaming-experiment-01.md)
- [Production session architecture and migration plan](docs/architecture/production-session-architecture.md) (session owners, the real USB connection, capture and encoding on Windows, and the link and decoder on Android; section 21 for the current flows)
- [Architecture decision log](docs/architecture/decision-log.md)
- [Phase 9 research: wireless, pairing, input, Extend](docs/research/phase-9-research.md)
- [Next-generation architecture](docs/architecture/next-generation-architecture.md), [transport](docs/architecture/transport-architecture.md), [input](docs/architecture/input-architecture.md), [Extend mode research](docs/architecture/extend-mode-research.md)
- [Roadmap](ROADMAP.md), [project status](PROJECT_STATUS.md), [issue ledger](ISSUES.md)
- [Beta acceptance and gate](docs/quality/beta-acceptance.md), [compatibility and performance](docs/quality/compatibility-and-performance.md), [security and privacy review](docs/quality/security-privacy-review.md)
- [Release process](docs/release/release-process.md); tester guides in [docs/beta](docs/beta/)
- [Security model](docs/security/security-model.md)

### License

MIT; see [LICENSE](LICENSE). A future virtual display driver derived from Microsoft's sample would carry that sample's MS-PL licence for the derived files, kept in its own directory; see the [feasibility notes](docs/architecture/iddcx-feasibility.md#8-licensing).

### Developer

Developed by **RSX Develops**.
