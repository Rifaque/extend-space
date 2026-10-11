# IddCx feasibility for Extend mode

Status: research only. No driver code exists in this repository, nothing has been built against the
WDK, and nothing has been installed. Last reviewed 2026-10-11.

Extend mode needs Windows to believe a real monitor is attached. The supported way to do that is an
Indirect Display Driver (IDD) built on the IddCx class extension. This document records what is
known, what is assumed, and what must be proven before a production driver is attempted.

Every claim is tagged:

- **[Verified]** — read directly from the cited Microsoft source on the review date.
- **[Assumed]** — believed true from general knowledge or community practice, not confirmed against
  an authoritative source or by experiment. Treat as a hypothesis.
- **[Open]** — unknown; needs an experiment or a decision.

## Summary

- An IDD is feasible in principle for this product: "creating virtual monitors" is a scenario
  Microsoft names for the model, the driver is user-mode, and Microsoft publishes a working sample.
- The engineering risk is moderate. The **distribution risk is high**: installing the driver on other
  people's machines requires a Microsoft-trusted signature, which needs an EV certificate and a
  Hardware Developer Program account, and the attestation route is now documented as being for
  testing scenarios.
- The riskiest technical piece is getting frames from the driver (Session 0) to the host app (user
  session). The recommended first design **avoids that entirely**: the driver only creates the
  monitor, and the host captures that monitor with the same capture path Mirror mode uses.
- This machine cannot build a driver today: the WDK is not installed. See
  [Toolchain](#1-toolchain).

## 1. Toolchain

| Requirement | Source | This machine |
| --- | --- | --- |
| Visual Studio 2026 (Community or higher) with **Desktop development with C++**, the Spectre-mitigated library components, and the **Windows Driver Kit** individual component | [Verified] [Download the WDK](https://learn.microsoft.com/windows-hardware/drivers/download-the-wdk) | Missing. Build Tools 2026 (18.2, MSVC 14.50) and VS 2022 Community (MSVC 14.44, core editor only) are installed; neither has the WDK component or Spectre libraries. |
| Windows SDK whose **build number matches** the WDK | [Verified] same page, "Kit versioning" | SDK 10.0.26100 is installed. |
| WDK (latest is 28000.2526 for VS 2026; 26100.6584 for VS 2022) | [Verified] same page | Missing: no `km` headers, no `iddcx` headers, no `inf2cat`, `stampinf` or `devcon`. `signtool` is present (from the SDK). |
| Alternative: WDK as a NuGet package (from 10.0.26100.1) | [Verified] same page | Not tried. [Open] whether it builds a UMDF driver with Build Tools alone, without the VS WDK component. |
| Alternative: EWDK ISO, a self-contained command-line environment including Build Tools | [Verified] same page | Not tried. Avoids a system-wide install; good fit for CI. |

A driver built against a newer IddCx can run on older Windows using runtime checks; Microsoft states
a single binary built against IddCx 1.11 can run on Windows 10 1803 and later
([Verified] [IddCx 1.11 updates](https://learn.microsoft.com/windows-hardware/drivers/display/iddcx1-dot-11-updates)).
So: build with the current WDK, declare a conservative minimum, and feature-check at runtime.

## 2. Driver architecture and monitor lifecycle

[Verified] from the [IDD model overview](https://learn.microsoft.com/windows-hardware/drivers/display/indirect-display-driver-model-overview)
and [IddCx objects](https://learn.microsoft.com/windows-hardware/drivers/display/iddcx-objects):

- An IDD is a **UMDF** driver. The model "doesn't support kernel-mode components".
- It runs in **Session 0** "without any components running in the user session".
- It may use any DirectX API on the desktop image, but must not call "GDI, windowing APIs, OpenGL, or
  Vulkan".
- The driver creates one `IDDCX_ADAPTER` (`IddCxAdapterInitAsync`, completed by
  `EvtIddCxAdapterInitFinished`). There is no destroy callback; the adapter lives until the UMDF
  device stops.
- The driver creates each `IDDCX_MONITOR` with `IddCxMonitorCreate` then `IddCxMonitorArrival`, and
  removes it with `IddCxMonitorDeparture`, which destroys the object. Re-plugging the "same" monitor
  requires a new create/arrival pair.
- The **OS** creates and destroys `IDDCX_SWAPCHAIN` objects and hands them to the driver through
  `EvtIddCxMonitorAssignSwapChain` / `EvtIddCxMonitorUnassignSwapChain`. One swap chain per monitor
  at a time.

[Verified] from the [Microsoft sample](https://github.com/microsoft/Windows-driver-samples/tree/main/video/IndirectDisplay)
(`IddSampleDriver/Driver.cpp`, `IddSampleDriver.inf`, `IddSampleApp/main.cpp`):

- The driver registers `EvtIddCxParseMonitorDescription`, `EvtIddCxMonitorGetDefaultDescriptionModes`,
  `EvtIddCxMonitorQueryTargetModes`, `EvtIddCxAdapterCommitModes` and the two swap-chain callbacks.
  The sample supplies a hard-coded EDID and mode list.
- The INF is `Class = Display`, includes `WUDFRD.inf`, sets `UpperFilters = IndirectKmd`, and names
  the class extension with `UmdfExtensions = IddCx0102`.
- The sample device is created at run time by a companion app using `SwDeviceCreate` under
  `HTREE\ROOT\0`; closing the handle removes the device. The INF also lists a root-enumerated
  hardware ID (`Root\IddSampleDriver`).
- Custom IOCTLs must go through `EvtIddCxDeviceIoControl`: the sample notes that "IddCx redirects
  IoDeviceControl requests to an internal queue".

Design consequences for ExtendSpace:

- One adapter, monitors added and removed on demand, one per connected Android device. [Assumed]
  that arrival/departure at arbitrary times is reliable in practice; the sample only plugs a monitor
  once at D0 entry.
- The monitor description (EDID) and mode list should be generated from the Android device's
  negotiated capabilities, with a **stable identity per paired device** so Windows can restore the
  user's display arrangement on reconnect. [Assumed] that a stable EDID serial is sufficient for
  that; [Open] until tested.
- When the user changes resolution in Windows Settings, `EvtIddCxAdapterCommitModes` fires and the
  streaming pipeline must reconfigure. This is a required lifecycle path, not an edge case.

## 3. Frame delivery

[Verified] from the sample's `SwapChainProcessor`:

1. On assignment the driver receives the swap chain, the **render adapter LUID** and a
   new-frame event handle.
2. It creates a D3D11 device on that adapter and calls `IddCxSwapChainSetDevice`.
3. It loops on `IddCxSwapChainReleaseAndAcquireBuffer`. `E_PENDING` means no new frame: wait on the
   event. Success returns the desktop image as an `IDXGIResource`.
4. It calls `IddCxSwapChainFinishedProcessingFrame` after handling the frame.
5. Any other failure (the sample cites `DXGI_ERROR_ACCESS_LOST`) means the swap chain was abandoned;
   the loop exits and the swap-chain object is deleted so the OS supplies a new one.

The sample calls the frame loop "the most performance-critical section of code in an IddCx driver"
and lists a GPU copy or GPU encode as acceptable work there. IddCx 1.11 adds an optional D3D12 path
([Verified] IddCx 1.11 updates); D3D11 is sufficient here.

The driver must drain the swap chain regardless of what we do with the frames. The question is how
frames reach the encoder, which lives in the host app. Three candidates:

| | Design | For | Against |
| --- | --- | --- | --- |
| **A** | **Driver creates the monitor only.** The host app captures the virtual monitor through the same display-capture path used for Mirror. The driver acquires and releases frames and discards them. | Smallest possible driver. No Session 0 to user-session frame transport. Mirror and Extend share capture, encode and transport by construction. | [Assumed] that Windows.Graphics.Capture can capture an IDD monitor at full rate; widely done by open-source streaming tools but not confirmed from Microsoft documentation. One extra GPU copy. Capture restrictions (protected content, secure desktop) apply. |
| **B** | Driver copies each frame to a shared D3D11 texture and passes a handle to the host app over an IOCTL. | Frames come straight from the swap chain with IddCx metadata (dirty rects, timing). | Cross-session, cross-process GPU sharing between a restricted driver host and a user app. Considerably more driver code and more failure modes. [Open] whether the driver host's account can share to an unelevated process without loosening security. |
| **C** | Driver encodes H.264 itself and sends the bitstream over a pipe. | Shortest frame path. | Puts the codec, rate control and error handling inside a driver host. Hardest to debug and to keep reliable. Duplicates the Mirror pipeline. |

**Recommendation: build A first and measure it.** Move to B only if A fails a stated latency or
frame-rate target on real hardware. C is not recommended.

## 4. Boundary between the driver and the C# host

- The driver is native C++ (UMDF 2). There is no supported managed driver model. [Assumed]
- The host talks to it through a **device interface** and IOCTLs handled in
  `EvtIddCxDeviceIoControl`. From C#, that is `CreateFile` on the interface path plus
  `DeviceIoControl` via P/Invoke; no native shim is required. [Assumed]
- Microsoft's Session 0 guidance for UMDF drivers says to use "a client/server mechanism such as
  remote procedure call (RPC) or named pipes" to talk to a service, and never to depend on UI
  ([Verified] [Session zero guidelines](https://learn.microsoft.com/windows-hardware/drivers/wdf/session-zero-guidelines-for-umdf-drivers)).
- Proposed control surface, deliberately tiny: `GetVersion`, `AddMonitor(deviceId, modes)`,
  `RemoveMonitor(deviceId)`, `Heartbeat`. Every input is length- and range-checked in the driver;
  the driver is a privilege boundary.
- The device interface must carry a security descriptor that admits only the interactive user (or
  administrators plus a broker). [Open] what the right SDDL is and how it is set from the INF.

Under design A no pixel data crosses this boundary at all.

## 5. Packaging, signing, installation and removal

[Verified] from [Driver signing options](https://learn.microsoft.com/windows-hardware/drivers/dashboard/driver-signing-offerings),
[Driver code signing requirements](https://learn.microsoft.com/windows-hardware/drivers/dashboard/code-signing-reqs)
and [Attestation signing](https://learn.microsoft.com/windows-hardware/drivers/dashboard/code-signing-attestation):

- Windows device installation uses digital signatures to verify driver package integrity and
  publisher identity.
- **WHCP / HLK-tested and dashboard-signed** is the recommended route and the only one that allows
  retail distribution through Windows Update.
- **Attestation signing** needs no HLK testing, works on Windows 10 desktop and later, and "supports
  Windows Desktop kernel mode and user mode drivers". It requires an **EV code-signing certificate**
  and registration in the Hardware Developer Program. Microsoft now describes it as "for testing
  purposes only", and attestation-signed drivers "can't be published to Windows Update for retail
  audiences".
- **Preproduction signing** exists for partners and only loads on devices explicitly provisioned to
  trust it.
- `SwDeviceCreate` requires the caller to "have Administrator access", and the device is removed
  when the handle is closed
  ([Verified] [SwDeviceCreate](https://learn.microsoft.com/windows/win32/api/swdevice/nf-swdevice-swdevicecreate)).

Not verified against a Microsoft source in this review:

- [Assumed] A UMDF driver package signed with a self-signed certificate installs on a normal
  (non-test-mode) system if that certificate is first added to the machine's Trusted Root and
  Trusted Publishers stores, because the kernel-mode code-integrity policy does not apply to
  user-mode driver binaries. If true, local development needs **no** `bcdedit /set testsigning`
  change. This must be confirmed on a disposable machine before anyone relies on it.
- [Assumed] `pnputil /add-driver <inf> /install` and `pnputil /delete-driver <oem.inf> /uninstall`
  are sufficient for install and removal, and both require elevation.
- [Open] Whether attestation-signed drivers remain acceptable for direct public download now that
  the documentation frames attestation as a testing offering. This is a policy question, not a
  technical one, and it decides whether an individual developer can ship Extend at all.

Installation model options:

| Model | Elevation | Crash behaviour | Notes |
| --- | --- | --- | --- |
| Software device created per session with `SwDeviceCreate` | Every session, or a privileged broker service | Device, and its monitors, vanish when the creating process dies | Matches the Microsoft sample. Needs a service to avoid a UAC prompt per connect. |
| Root-enumerated device installed once at setup | Setup only | Device persists; monitors must be removed by the driver itself | The unelevated app only sends IOCTLs. Fewer moving parts at run time. |

**Proposed:** root-enumerated device installed once by an explicit, user-initiated, elevated setup
step, with monitors plugged on demand. The app must never install a driver silently or
automatically.

## 6. Recovery

Leaving a phantom monitor attached is the worst failure this feature can have: windows and the
cursor move onto a display nobody can see. Required behaviours:

| Event | Required behaviour | How | Status |
| --- | --- | --- | --- |
| Android client disconnects | Keep the monitor through a short reconnect window, then remove it | Host sends `RemoveMonitor` when the session leaves `Reconnecting` | [Open] the right grace period |
| Host app exits normally | Remove all monitors | Host sends `RemoveMonitor` during teardown | Design only |
| Host app crashes or is killed | Remove all monitors within seconds, with no cooperation from the app | Driver watches the control handle: on file cleanup/close it calls `IddCxMonitorDeparture`. Backstop: a driver-side watchdog that removes monitors when `Heartbeat` stops. | [Open] whether WDF file-object callbacks can be combined with IddCx's own request handling; the watchdog is the fallback |
| Swap chain abandoned (`DXGI_ERROR_ACCESS_LOST`, GPU reset, render adapter change) | Exit the frame loop, delete the swap chain, wait for a new assignment | As in the Microsoft sample | [Verified] pattern exists in the sample |
| Sleep / resume, lock screen, fast user switching, RDP | Session survives or fails cleanly to `Disconnected` | Unknown | [Open] needs testing |
| Driver host crash | Windows restarts the UMDF host; monitors are gone until re-added | Host detects the lost handle and re-adds | [Assumed] default UMDF restart policy applies |

## 7. Compatibility constraints

[Verified] from [IddCx versions](https://learn.microsoft.com/windows-hardware/drivers/display/iddcx-versions):

| Windows 11 release | Build | IddCx |
| --- | --- | --- |
| 21H2 | 22000 | 1.8 |
| 22H2 | 22621 | 1.9 |
| 23H2 | 22631 | 1.10 (`0x1A00`) |
| "Windows 2024" | not listed | 1.10 (`0x1A80`), adds runtime power management |
| not listed (documented as prerelease) | not listed | 1.11 (`0x1B00`), adds D3D12 |

- Windows 11 as the floor means IddCx 1.8 is the minimum to design against. Nothing ExtendSpace
  needs is newer than that. HDR (1.10) is out of scope.
- IddCx 1.9 "disallow[s] UMDF process pooling", which supersedes the sample README's older advice
  about choosing a `DeviceGroupId`.
- The OS chooses a **render adapter** for the swap chain and reports its LUID; the driver must create
  its device on that adapter. `IddCxAdapterSetRenderAdapter` (1.4+) lets a driver state a
  preference. Hybrid-GPU laptops are the case to test. [Open]
- This development machine runs build 26300, which is not in Microsoft's table. Its IddCx version
  must be read with `IddCxGetVersion` rather than assumed. [Open]
- ARM64 needs its own driver build; the current WDK supports it ([Verified] Download the WDK).
  [Open] whether to support it initially.
- [Assumed] An IDD needs a hardware GPU to act as render adapter; behaviour on machines using only
  the Microsoft Basic Render Driver is unknown.

## 8. Licensing

- The Microsoft IDD sample is licensed under **MS-PL** ([Verified] `LICENSE` in
  `microsoft/Windows-driver-samples`).
- This repository is MIT. MS-PL requires that any portion of the sample distributed in source form
  stays under MS-PL. A driver derived from the sample can live in `native/display-driver/` with its
  own MS-PL notice; that does not require relicensing the rest of the repository, and the repository
  licence should not be changed to accommodate it.
- A clean-room driver written from the documentation alone could be MIT, at the cost of losing a
  known-good starting point. [Open] decision; deriving from the sample is the lower-risk choice.

## 9. What must be proven before a production driver

In order. Each step is cheap relative to the one after it, and any of them can end the effort.

1. **Signing route.** Decide whether an EV certificate and Hardware Developer Program account are
   obtainable, and whether attestation signing is acceptable for public distribution. If not,
   evaluate depending on an existing, already-signed open-source virtual display driver instead of
   shipping one. Without an answer here, Extend can only ever work on the developer's own machine.
2. **Toolchain.** Install the WDK (or mount the EWDK) and build the unmodified Microsoft sample.
3. **Install on a disposable machine.** Not this workstation. Note that Windows 11 Home has no
   Hyper-V, so this needs a third-party hypervisor or a spare PC. Confirm the virtual monitor
   appears, modes can be changed, and removal leaves no residue. Confirm or refute the self-signed
   install assumption in section 5.
4. **Design A.** From an unelevated app, capture the virtual monitor with the Mirror capture path.
   Measure frame rate and added latency against a physical monitor.
5. **Crash recovery.** Kill the controlling process and confirm the monitor disappears on its own.
6. **Lifecycle soak.** Sleep/resume, GPU driver restart (`Win+Ctrl+Shift+B`), lock, user switch,
   hybrid-GPU switch, repeated plug/unplug.

Only after 1–5 pass is it worth writing ExtendSpace-specific driver code.

## 10. Unresolved risks

| Risk | Severity | Notes |
| --- | --- | --- |
| No affordable, policy-compliant way to sign for public distribution | High | Could confine Extend to self-built installs. Not solvable with engineering. |
| Design A capture of an IDD monitor is too slow or restricted | Medium | Fallback is design B, which is a much larger driver. |
| Phantom monitor after a crash | Medium | Two independent mechanisms proposed; neither proven. |
| No safe place to test | Medium | Driver experiments must not run on the primary workstation; Home edition has no Hyper-V. |
| Hybrid-GPU and render-adapter behaviour | Medium | Unknown until tested on such hardware. |
| Secure desktop, protected content, HDR | Low for now | Expected to show black or be unsupported; document rather than solve. |
