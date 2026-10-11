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
| A physical Android device with USB debugging enabled | The first streaming experiment | See [experiment 01](../architecture/streaming-experiment-01.md). |

## Windows host

```powershell
cd apps/windows
dotnet build ExtendSpace.slnx
dotnet test --solution ExtendSpace.slnx
dotnet run --project src/ExtendSpace.Host
```

- `global.json` pins the .NET 10 SDK and selects the Microsoft.Testing.Platform test runner, which
  xUnit v3 uses. That is why the test command is `dotnet test --solution`, not `dotnet test <path>`.
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
- The app requests no permissions yet.

## State of the development machine

Inspected on 2026-10-11. Windows 11 Home, build 26300, x64.

**Installed and verified** (used successfully during this phase)

| Tool | Detail |
| --- | --- |
| Git | 2.52.0 |
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
| .NET runtimes | 7.0, 8.0, 9.0 (no 10.0) |

**Missing**

| Tool | Effect |
| --- | --- |
| **.NET 10 SDK** | The only installed SDK is 7.0.410, which has been out of support since May 2024 and cannot build `net10.0`. **The Windows host cannot be built on this machine as it stands.** |
| .NET 10 runtime | Needed to run the built app; comes with the SDK. |
| WDK, IddCx headers, `inf2cat`, `stampinf`, `devcon`, Spectre-mitigated libraries | No driver can be built. Not needed until the driver work starts. |
| WinUI 3 project templates | Not needed; the projects were written by hand. |
| A connected Android device | The app has not been launched on Android. |
| Android SDK command-line tools | Not needed for building. |

**Not required for this phase**: Android NDK, the C++ toolsets, the Windows SDK, an Android Studio
bundled JDK (the one in the Android Studio folder appears incomplete).

### How the Windows projects were verified without the SDK installed

To avoid changing the machine, the Windows projects were built and tested with a portable copy of
the .NET 10.0.401 SDK, downloaded as a zip from Microsoft's release feed, checked against the
published SHA-512, extracted to a temporary folder and used through its own `dotnet.exe`. Nothing
was installed, no `PATH` or registry entry was changed, and the folder was deleted afterwards.
Installing the SDK properly (first row of the prerequisites table) is still required to work on the
Windows host.

## Verification record

| Command | Result |
| --- | --- |
| `dotnet build ExtendSpace.slnx` (clean) | Succeeded, 0 warnings, 0 errors |
| `dotnet test --solution ExtendSpace.slnx` | 24 passed, 0 failed, 0 skipped |
| `dotnet run --project src/ExtendSpace.Host` | Window opened; closed normally with exit code 0 |
| Scripted UI walk of the running host through UI Automation | Every navigation item and both option groups selected without the process exiting; Connect button reported disabled |
| `gradlew clean assembleDebug testDebugUnitTest lintDebug` | Succeeded; `app-debug.apk` produced; 9 tests passed, 0 failed; lint 0 errors, 1 warning |
| Install and launch on the emulator | **Not done.** `adb install` failed: the emulator image has 260 MB free. Wiping the user's emulator was not attempted. |

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
