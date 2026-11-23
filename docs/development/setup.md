# Development setup

## Prerequisites

| Tool | Version used | Needed for | Install |
| --- | --- | --- | --- |
| .NET SDK | 10.0 (LTS) | Windows host | `winget install Microsoft.DotNet.SDK.10` |
| JDK | 17 or newer (22 used) | Android build | Any current OpenJDK distribution |
| Android SDK | Platform 37, build-tools 36+ | Android build | Android Studio SDK Manager |
| Git | any recent | — | — |

Not needed to build the apps: Visual Studio, the C++ toolset, the Windows SDK, the WDK. The Windows
host builds entirely with the `dotnet` CLI; the Windows App SDK comes from NuGet.

Needed later, not now:

| Tool | Needed for | Notes |
| --- | --- | --- |
| Visual Studio 2026 with *Desktop development with C++*, Spectre-mitigated libraries and the *Windows Driver Kit* component, plus the matching Windows SDK and WDK — or the EWDK ISO | Building the virtual-display driver | See [IddCx feasibility](../architecture/iddcx-feasibility.md#1-toolchain). Do not install a driver on a machine you depend on. |
| A physical Android device with USB debugging enabled | Steps 2 and 3 of the first streaming experiment | See [experiment 01](../architecture/streaming-experiment-01.md). |
| `ffmpeg` and `ffprobe` on `PATH` | Checking that a captured `.h264` file really decodes | Optional. The capture experiment runs without them. |

## Windows host

```powershell
cd apps/windows
dotnet build ExtendSpace.slnx
dotnet test --solution ExtendSpace.slnx
dotnet run --project src/ExtendSpace.Host

# Capture-and-encode experiment: writes a .h264 file of the primary monitor, or streams it to a
# device; also shows a test pattern (pattern) and reports what capture delivers (probe)
dotnet run --project experiments/ExtendSpace.Experiments.CaptureEncode -- --help
```

- `global.json` pins the .NET 10 SDK and selects the Microsoft.Testing.Platform test runner, which
  xUnit v3 uses. That is why the test command is `dotnet test --solution`, not `dotnet test <path>`,
  and why it has to be run from `apps/windows`: from the repository root `global.json` is not found
  and `dotnet test apps/windows/ExtendSpace.slnx` fails with a "VSTest target is no longer supported"
  error before running anything.
- Without `-p:Platform`, the app builds for the machine's own architecture. Use `-p:Platform=x64` or
  `-p:Platform=ARM64` to choose.
- Warnings are errors (`Directory.Build.props`).
- The app is unpackaged and carries the Windows App SDK runtime; it needs only the .NET 10 runtime
  on the machine.

Projects:

| Project | Target | Contents |
| --- | --- | --- |
| `src/ExtendSpace.Core` | `net10.0` | Session state machine, capabilities, view models. No UI or Windows dependency. |
| `src/ExtendSpace.Host` | `net10.0-windows10.0.26100.0`, minimum Windows 11 (22000) | WinUI 3 application. Builds `ExtendSpace.exe`. |
| `tests/ExtendSpace.Core.Tests` | `net10.0` | xUnit v3 tests for `ExtendSpace.Core`. |
| `experiments/ExtendSpace.Experiments.CaptureEncode` | `net10.0-windows10.0.26100.0`, minimum Windows 11 (22000) | Console experiment: display capture to an H.264 file, or live to an Android device over `adb forward`. Builds `ExtendSpace.CaptureEncode.exe`. Not referenced by the app. See [experiment 01](../architecture/streaming-experiment-01.md). |
| `tests/ExtendSpace.Experiments.CaptureEncode.Tests` | `net10.0-windows10.0.26100.0` | xUnit v3 tests for the experiment's deterministic logic, including the protocol framing and the host session against a fake client on a loopback socket. They do not capture or encode anything and need no device. |

## Android companion

```powershell
cd apps/android
.\gradlew.bat assembleDebug testDebugUnitTest lintDebug
.\gradlew.bat installDebug     # needs a connected device or running emulator
```

- The SDK is located through `ANDROID_HOME` or a `local.properties` file (git-ignored).
- Gradle 9.7.1 through the wrapper, Android Gradle Plugin 9.4.1 with its built-in Kotlin support,
  Kotlin Compose compiler plugin 2.4.21. Versions are in `gradle/libs.versions.toml`.
- `compileSdk` and `targetSdk` are 37 because current AndroidX releases require compiling against
  API 37. `minSdk` is 29 (Android 10).
- Application ID `com.rsxdevelops.extendspace`. Change it before any public release if RSX Develops
  owns a different reverse-domain name; it cannot be changed after publishing.
- Permissions: `INTERNET` and `CHANGE_WIFI_MULTICAST_STATE` since Phase 10, for the Wi-Fi transport
  and its DNS-SD advertisement (manifest comment explains each). Nothing else.
- `org.bouncycastle:bcpkix-jdk18on` is a `testImplementation` dependency only: the Wi-Fi TLS tests
  build self-signed certificates on the desktop JVM, where AndroidKeyStore does not exist. The
  release APK contains no Bouncy Castle class (checked with `dexdump`).
- Debug builds contain two extra screens. "ExtendSpace decoder test" plays a `.h264` file captured
  by the Windows experiment through the device's H.264 decoder; how to use it is in
  [experiment 01](../architecture/streaming-experiment-01.md#reproduce). "ExtendSpace live test"
  listens on a local socket and shows the stream the Windows experiment sends through
  `adb forward`; see [how to run](../architecture/streaming-experiment-01.md#how-to-run). Their
  code, manifest entries and tests are in `app/src/debug` and `app/src/testDebug`, so release builds
  have none of it. Neither needs a permission.
- `org.json` is a `testImplementation` dependency only. The live screen's control messages use the
  `org.json` classes that are part of Android; unit tests run on a desktop JVM, where those classes
  are stubs, so the tests bring a real implementation. Nothing is added to any APK.
- Xiaomi HyperOS refuses `adb install` (`INSTALL_FAILED_USER_RESTRICTED`) until *Developer options →
  Install via USB* is switched on, on the device, by its owner.

## Wireless and input tests (Phases 10–11)

Run by the normal suites, no setup: codec, pairing-code vectors (`tests/pairing-vectors/`), touch
vectors (`tests/input-vectors/`), trust and identity stores, discovery merging, routing, real TLS 1.3
loopback tests on both sides (session, pin mismatch, `not_paired`, every pairing outcome), input
mapping and lifecycle, and the input path over a loopback connection. Shared vectors were computed
with Python independently of both apps.

Gated (opt-in, because they reach outside the test process):

| Variable | Test | What it does |
| --- | --- | --- |
| `EXTENDSPACE_DNSSD_TESTS=1` | `ExtendSpace.Media.Tests` `WirelessAdapterTests.The_browser_finds_a_service_advertised_on_this_PC` | Advertises a test DNS-SD service **on the LAN** for a few seconds and browses for it with the production watcher |
| `EXTENDSPACE_INTEROP_DIR=<empty folder>` on **both** | Android `InteropTabletTest` and Windows `InteropHostTests` | The real Android endpoint and link (desktop JVM) and the real Windows pairing, transport, controller and input, on loopback: pairing with matching codes, a session over TLS, video, a touch. Start the Android side first, in the background: `gradlew.bat --offline testDebugUnitTest --tests "*InteropTabletTest" --rerun`, then `ExtendSpace.Core.Tests.exe -class ExtendSpace.Core.Tests.Wireless.InteropHostTests`. Results: `tablet.log`, `tablet-result` in the folder |

Wireless state on the PC lives in `%LOCALAPPDATA%\ExtendSpace` (`identity.bin`, DPAPI-protected;
`trusted-devices.json`, public key hashes only); deleting the folder resets pairing. On the tablet,
*Clear storage* does the same.

## State of the development machine

Inspected on 2026-10-11. Windows 11 Home, build 26300, x64. The .NET entries were rechecked on
2026-10-12, after the .NET 10 SDK had been installed.

**Installed and verified** (used successfully during this phase)

| Tool | Detail |
| --- | --- |
| Git | 2.52.0 |
| .NET SDK | 10.0.401 in `C:\Program Files\dotnet`, used for every Windows build and test since it was installed. 7.0.410 is also installed and unused. |
| .NET runtimes | 10.0.12, plus 9.0.10, 9.0.11, 8.0.23 and 7.0.20 |
| Android device | Xiaomi Pad 7 (`2410CRP4CI`), Android 16, over USB with ADB, from 2026-10-12. The debug build installs and runs on it. |
| `ffmpeg`, `ffprobe` | Build 2026-01-26 (gyan.dev "essentials"), on `PATH`. Used to validate the capture experiment's output. |
| JDK | Temurin 22.0.2 on `PATH` |
| Android SDK | `ANDROID_HOME` set; platforms 36, 36.1, 37.0; build-tools 35, 36, 36.1 |
| Gradle | No global install; 9.7.1 was already in the wrapper cache and is now pinned by the project wrapper |
| `adb` | 37.0.1, in the SDK, not on `PATH` |
| Android emulator | AVD `Medium_Phone_API_36.1` boots. Its data partition is 96% full, so the app could not be installed on it |
| `winget` | Present; the .NET 10 SDK package resolves to 10.0.401 |

**Installed but not verified** (present, not exercised)

| Tool | Detail |
| --- | --- |
| Visual Studio Build Tools 2026 (18.2) | C++ build tools workload, MSVC 14.50, MSBuild 18 |
| Visual Studio Community 2022 (17.14) | Core editor only, plus MSVC 14.44 |
| Windows SDK | 10.0.26100 (user-mode headers, `signtool`) |
| JDK 25 | Oracle, in `Program Files\Java` |
| Android NDK | 28.2 |

**Missing**

| Tool | Effect |
| --- | --- |
| WDK, IddCx headers, `inf2cat`, `stampinf`, `devcon`, Spectre-mitigated libraries | No driver can be built. Not needed until the driver work starts. |
| WinUI 3 project templates | Not needed; the projects were written by hand. |
| Android SDK command-line tools | Not needed for building. |

**Not required for this phase**: Android NDK, the C++ toolsets, the Windows SDK, an Android Studio
bundled JDK (the one in the Android Studio folder appears incomplete).

### How the Windows projects were first verified, before the SDK was installed

This is history: the SDK is installed now and is what every later build used. On 2026-10-11, to
avoid changing the machine, the Windows projects were built and tested with a portable copy of
the .NET 10.0.401 SDK, downloaded as a zip from Microsoft's release feed, checked against the
published SHA-512, extracted to a temporary folder and used through its own `dotnet.exe`. Nothing
was installed, no `PATH` or registry entry was changed, and the folder was deleted afterwards.

## Verification record

| Command | Result |
| --- | --- |
| `dotnet build ExtendSpace.slnx` (clean) | Succeeded, 0 warnings, 0 errors |
| `dotnet test --solution ExtendSpace.slnx` | 24 passed, 0 failed, 0 skipped |
| `dotnet run --project src/ExtendSpace.Host` | Window opened; closed normally with exit code 0 |
| Scripted UI walk of the running host through UI Automation | Every navigation item and both option groups selected without the process exiting; Connect button reported disabled |
| `gradlew clean assembleDebug testDebugUnitTest lintDebug` | Succeeded; `app-debug.apk` produced; 9 tests passed, 0 failed; lint 0 errors, 1 warning |
| Install and launch on the emulator | **Not done.** `adb install` failed: the emulator image has 260 MB free. Wiping the user's emulator was not attempted. |

Capture-and-encode experiment, 2026-10-11 to 2026-10-12, with the installed .NET 10.0.401 SDK, from
`apps/windows`:

| Command | Result |
| --- | --- |
| `dotnet build ExtendSpace.slnx`, before any change | Succeeded, 0 warnings, 0 errors |
| `dotnet test --solution ExtendSpace.slnx`, before any change | 24 passed, 0 failed, 0 skipped |
| `dotnet build ExtendSpace.slnx`, with the experiment | Succeeded, 0 warnings, 0 errors |
| `dotnet test --solution ExtendSpace.slnx`, with the experiment | 140 passed, 0 failed, 0 skipped: the same 24 plus 116 for the experiment |
| `ExtendSpace.CaptureEncode.exe capture …`, eight runs against the real display | All succeeded; every file decodes fully with ffmpeg. Figures and limits are in [experiment 01](../architecture/streaming-experiment-01.md#results). |

Android decoder validation, 2026-10-12:

| Command | Result |
| --- | --- |
| `dotnet build ExtendSpace.slnx`, `dotnet test --solution ExtendSpace.slnx` | 0 warnings, 0 errors; 172 passed, 0 failed |
| `gradlew assembleDebug testDebugUnitTest lintDebug` | Succeeded; 41 tests passed, 0 failed; lint 0 errors, 1 warning |
| `gradlew assembleRelease` | Succeeded; the decoder test screen is not in the release APK |
| `adb install -r app-debug.apk` on the Xiaomi Pad 7 | Succeeded, after *Install via USB* was enabled on the tablet |
| Decoder test screen on the tablet, five real captures | Every picture of every file decoded by the hardware decoder and shown. Figures and limits are in [experiment 01](../architecture/streaming-experiment-01.md#results-on-the-device). |
| The app's own screens on the tablet | **Not exercised.** Only the decoder test screen was opened. |

Live stream over `adb forward`, 2026-10-13:

| Command | Result |
| --- | --- |
| `dotnet build ExtendSpace.slnx`, `dotnet test --solution ExtendSpace.slnx` | 0 warnings, 0 errors; 299 passed, 0 failed: 24 for the app's core, 275 for the experiment |
| `gradlew assembleDebug testDebugUnitTest lintDebug` | Succeeded; 82 tests passed, 0 failed; lint 0 errors, 1 warning |
| `gradlew assembleRelease` | Succeeded; neither debug screen, and no `org.json` artifact, is in the release APK |
| `adb install -r app-debug.apk` on the Xiaomi Pad 7 | Succeeded |
| `ExtendSpace.CaptureEncode.exe stream …` to the tablet, nine kept runs | A live picture on the tablet in every run that had a client; no frame lost in the sustained runs; reconnection after the app was killed. Figures and limits are in [experiment 01](../architecture/streaming-experiment-01.md#step-3--live-stream-over-adb-forward). |
| The app's own screens on the tablet | **Still not exercised.** |

Reliability and latency validation (Phase 2.4), 2026-10-13:

| Command | Result |
| --- | --- |
| `dotnet build ExtendSpace.slnx`, `dotnet test --solution ExtendSpace.slnx`, before any change | 0 warnings, 0 errors; 299 passed |
| The same, after | 0 warnings, 0 errors; 355 passed, 0 failed, 0 skipped: 24 for the app's core, 331 for the experiment |
| `git diff --check` | Clean |
| `gradlew assembleDebug testDebugUnitTest lintDebug` | Succeeded; 91 tests passed, 0 failed (82 before); lint 0 errors, 1 warning |
| `gradlew assembleRelease` | Succeeded; neither debug screen and no `org.json` artifact in the release APK |
| `ExtendSpace.CaptureEncode.exe stream …` to the tablet, with the new `pattern` command as the source | 1080p60 for 180 s and for 600 s without a lost frame; recovery from two real cable pulls. Figures and limits are in [experiment 01](../architecture/streaming-experiment-01.md#phase-24--reliability-and-latency-validation). |
| Final debug APK on the tablet | Installed and run: a 30 s 1080p60 stream, a stream ended from the tablet, a silent link on each side, and leaving the live screen with the host healthy and frozen. The soak, the cable pulls and the camera clip are from an earlier build of the same day. |
| The app's own screens on the tablet | **Still not exercised.** |

Phases 1–4 mission (D64 to D66), 2026-11-22, from `apps/windows` and `apps/android`:

| Command | Result |
| --- | --- |
| `dotnet build ExtendSpace.slnx`, Debug and `-c Release` | 0 warnings, 0 errors |
| `dotnet test --solution ExtendSpace.slnx`, before any change | 813: 792 passed, 0 failed, 21 skipped (gated) |
| The same, after | 836: 815 passed, 0 failed, 21 skipped (gated) |
| `gradlew testDebugUnitTest lintDebug assembleDebug assembleRelease`, before and after | 254, then 260 tests, 0 failures; lint: version notices only; both variants build |
| Release APK, `dexdump` and `aapt2` | No experiment or harness classes; `MainActivity` and the unexported `SessionActivity` |
| The Windows host started (Debug build) and read through UI Automation for about 5 s, nothing clicked | Window opened; the Android device card said "1 device found." and Connect was enabled with that device chosen; closed normally, exit code 0. No session was started |
| The app's own screens on the tablet | **Not exercised in this mission** (ISSUES.md ES-030) |

Phases 5–8 mission (D67 to D73), 2026-11-22: see [PROJECT_STATUS.md](../../PROJECT_STATUS.md)
for the final figures. Additionally: the Windows package was built with
`packaging/windows/Build-Package.ps1` and validated in scratch folders (install, launch, clean exit,
upgrade, uninstall); the Android release APK with `packaging/android/Build-Apk.ps1`; release signing
was proven with a throwaway key. No device was used.

When more than one device is attached to adb (an emulator, say), `stream` needs `--device <serial>`
and plain `adb` commands need `-s <serial>`. `adb reconnect` left the tablet absent from adb here
until the adb server was restarted; avoid it as a way to simulate a disconnection.

Remaining warning: Android lint reports that Gradle 9.8.1 is available; the wrapper stays on 9.7.1,
the version already cached on this machine.

Size note: the Windows debug output is about 230 MB because the `Microsoft.WindowsAppSDK`
metapackage carries AI, search and widget components the app does not use. Replacing it with only
the WinUI component packages is a follow-up; a first attempt failed on a package-version mismatch in
the feed and was reverted.

## Repository conventions

- Nothing generated is committed: no `bin/`, `obj/`, `build/`, APKs, packages or SDKs. The single
  tracked binary is `apps/android/gradle/wrapper/gradle-wrapper.jar`, which the Gradle wrapper
  requires.
- No secrets, signing keys, keystores or certificates in the repository. `.gitignore` excludes the
  common file types as a backstop.
- `local.properties` and other machine-specific files stay untracked.

## Linux host (D96–D103)

* Build and unit-test anywhere: `dotnet build apps/linux/ExtendSpace.Linux.slnx` and run
  `apps/linux/tests/ExtendSpace.Linux.Tests/bin/Debug/net10.0/ExtendSpace.Linux.Tests` (Lab and E2E tests skip).
* Lab tests on Linux: publish the tests `-r linux-x64 --self-contained`, then on a Linux machine with
  GStreamer (base, good, ugly, x), Xvfb, xrandr and xdotool run them with `EXTENDSPACE_LINUX_LAB=1` and
  `DISPLAY` set. In WSL without root the packages can be unpacked into a user folder with
  `apt-get download` and `dpkg-deb -x`, with `LD_LIBRARY_PATH` and `GST_PLUGIN_SYSTEM_PATH` pointing there.
* Tablet end to end: `EXTENDSPACE_LINUX_E2E=1 EXTENDSPACE_TABLET_ADDRESS=<ip>` runs
  `TabletEndToEndTests` (pairing confirmed on the tablet; marker files in `EXTENDSPACE_E2E_DIR`).
* Package: `packaging/linux/Build-LinuxPackage.ps1` (tarball + SHA-256; never overwrites).
