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

**Nothing connects, mirrors or extends yet.** What exists today:

| Area | State |
| --- | --- |
| Windows host app shell (WinUI 3): navigation, Connect screen, diagnostics | Implemented |
| Android companion app shell (Jetpack Compose): Connect and About screens | Implemented |
| Session state model on both platforms, with unit tests | Implemented |
| Protocol | Draft specification only |
| USB transport, Wi-Fi transport, pairing | Not implemented |
| Display capture and H.264 encoding | Proven in a standalone experiment that writes a `.h264` file; not part of the app |
| Video decoding on Android | Proven on one tablet by a debug-only screen that plays a captured `.h264` file; not part of the app |
| Virtual display driver (Extend mode) | Not implemented; feasibility researched |

Both apps say so in their own UI: the Connect button on Windows is disabled, with the reason shown.

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
- [Security model](docs/security/security-model.md)

### License

MIT; see [LICENSE](LICENSE). A future virtual display driver derived from Microsoft's sample would carry that sample's MS-PL licence for the derived files, kept in its own directory; see the [feasibility notes](docs/architecture/iddcx-feasibility.md#8-licensing).

### Developer

Developed by **RSX Develops**.
