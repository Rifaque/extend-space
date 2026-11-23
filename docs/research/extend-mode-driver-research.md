# Extend mode: driver research, plan, critique and revised plan

Status: **written 2026-11-23 before any Extend code.** Decisions D87–D91 in the
[decision log](../architecture/decision-log.md). Architecture and milestones:
[extend-mode-architecture.md](../architecture/extend-mode-architecture.md). Hardware checklist:
[extend-mode-beta-acceptance.md](../quality/extend-mode-beta-acceptance.md). Earlier work this builds on:
[extend-mode-research.md](../architecture/extend-mode-research.md) (Phase 9) and
[iddcx-feasibility.md](../architecture/iddcx-feasibility.md) (October 2026).

Tags: **[Read]** from a listed source; **[Code]** from this repository; **[PC]** observed on the
development PC; **[Unverified]** reported by a secondary summary or not checked.

## 1. Baseline (reconnaissance)

- Repository: `main`, HEAD `8a9669f`, 39 modified and 71 untracked entries (Phases 3–11, uncommitted),
  nothing staged. **[Code]**
- Tests at the start: Windows 995 total, 969 passed, 0 failed, 26 skipped; Android 305, 0 failures;
  builds clean. **[PC]**
- This PC: NVIDIA RTX 5060 Laptop GPU and Intel graphics (hybrid); main monitor `\\.\DISPLAY9`
  2560 × 1440 at (0, 0); laptop panel `\\.\DISPLAY1` 2560 × 1600 at (−2560, −72); 125 % scaling. **No
  virtual display driver is installed** (`pnputil /enum-devices /class Display` lists only the two GPUs;
  `C:\VirtualDisplayDriver` does not exist). **[PC]**
- What Extend can reuse **[Code]**: the session coordinator, capture by monitor device name that refuses
  to substitute another monitor (D68), the encoder, the protocol, USB and Wi-Fi transports, the Android
  decoder and surface, and input mapping onto a monitor by device name (D83) with the virtual-screen
  origin fix (ES-132). The coordinator's stream choice is injectable (`ChooseEncoding`). What it lacks:
  any notion of a virtual display, a stream shaped to a non-16:9 source, and Extend's UX.

## 2. The six named projects

Searched again in Phase 9 by exact name: **PadDisplay, SecondScrn, DisplayMaster, FOLD, ezdisplay: not
found** as public projects; **TabletMonitor** exists twice (hpixsel, YuioDP), and the related projects
FlexDisplay, SecondMonitor and anydisplay were studied. **Every one of them that offers Extend uses the
community Virtual Display Driver below**, installed by the user (FlexDisplay, SecondMonitor,
anydisplay, YuioDP) or bundled with an admin-only helper service (hpixsel/TabletMonitor). **[Read]**
Details: [Phase 9 research, section 4](phase-9-research.md#4-competing-and-related-projects).

## 3. The community driver: VirtualDrivers/Virtual-Display-Driver ("VDD", "MttVDD")

<https://github.com/VirtualDrivers/Virtual-Display-Driver>. **[Read]** unless marked.

| Question | Finding |
| --- | --- |
| Licence | **MIT**, "Copyright (c) 2024 Virtual Display"; derived from Microsoft's IndirectDisplay sample via ge9/IddSampleDriver (Driver.cpp keeps "Copyright (c) Microsoft Corporation"). No NOTICE, no extra restriction found. MIT permits commercial use, modification and redistribution with the copyright notice |
| Maintainers | MikeTheTech and the "VirtualDrivers" organisation; 10.7k stars, 425 commits, 205 open issues |
| Activity | Latest tag **25.7.23** (23 July 2025); latest commit 22 June 2026; 2026 commits mostly dependabot/CI plus a few fixes |
| Signing | "Free code signing … provided by SignPath.io, certificate by SignPath Foundation"; WHQL or Microsoft attestation: not found |
| Install | "Virtual Driver Control" app (Install button) or `winget install --id=VirtualDrivers.Virtual-Display-Driver -e`; installing a driver needs administrator rights **[Unverified for VDC specifics]** |
| Control | Settings `C:\VirtualDisplayDriver\vdd_settings.xml` (and `HKLM\SOFTWARE\MikeTheTech\VirtualDisplayDriver`): `<monitors><count>`, global refresh rates, a resolution list. Named pipe `\\.\pipe\MTTVirtualDisplayPipe` with commands such as `RELOAD_DRIVER`, `SETDISPLAYCOUNT n`, `PING`→`PONG` **[Unverified: from a summary of Driver.cpp]**, pipe ACL reportedly "Everyone: generic all" **[Unverified]** |
| Dynamic add/remove | **Not supported in the source read**: the monitor count is read when the adapter starts; `SETDISPLAYCOUNT` rewrites the settings and re-initialises the adapter **[Unverified detail]** |
| Identity | EDID manufacturer "MTT", product 0x1337, friendly name "VDD by MTT" — **[PC] confirmed on hardware**; it reports output technology **HDMI (5)**, not INDIRECT_VIRTUAL |
| Known issues | #468 BSOD 0x133 DPC_WATCHDOG_VIOLATION (closed Feb 2026); #531 IddCx watchdog takes the display offline (Oct 2026); #474 black screen since a January 2026 Windows update (25H2); #471 cannot be primary on 24H2/25H2; #490 instability on build 26200; README warns GPU/chipset driver updates can black-screen it (uninstall VDD first; Safe Mode recovery) |

## 4. Alternatives

| Solution | Licence | Windows | Approach | Dynamic display | Interface | Distribution | Suitability |
| --- | --- | --- | --- | --- | --- | --- | --- |
| **VDD (MttVDD)** | MIT | 10/11 | IddCx, Microsoft sample lineage | No (count at adapter start; reload to change) | XML/registry, named pipe (reportedly world-writable) | Signed (SignPath); winget; redistributable under MIT | **Best available for a user-installed first step**; lifecycle and security weaknesses below |
| **Parsec VDD** (nomi-san/parsec-vdd wrapper) | Wrapper MIT; **driver binary is Parsec's**, no redistribution terms found | 10 21H2+ (v0.45) | IddCx | **Yes**: `VDD_IOCTL_ADD/REMOVE`, keepalive `UPDATE` < 100 ms; displays unplug ~1 s after pings stop | Device interface `{00b41627-04c4-429e-a26e-0265cf50c8fa}` | Signed; installer from Parsec; cannot be bundled without terms | **Best lifecycle** (no orphaned monitor after a crash); user would install from Parsec; driver last released April 2024 |
| **SudoVDA** (SudoMaker) | MIT / CC0 (README) | 10/11 | IddCx | Yes: add/remove IOCTLs, watchdog (default 3 s) | `{e5bcc234-…}` | **No signed binary, no release** | Unusable without our own signing |
| **Own IddCx driver** (Microsoft sample) | ours | 10/11 | IddCx | Yes, as designed | ours (IOCTLs, ACL of our choice) | Needs EV certificate + Hardware Developer Program; attestation is "for testing", retail via WHCP | Right long term; months of work and an owner purchase (Phase 9 D85, iddcx-feasibility) |
| No driver (spare output, dummy HDMI plug) | — | — | real hardware | — | — | — | User workaround only |
| Desktop-duplication tricks, cropping a wider desktop | — | — | none | — | — | — | Rejected: not a real display (the brief forbids a cropped or duplicated view) |

**Microsoft facts [Read]:** an IDD reports monitors "being connected and disconnected"
(`IddCxMonitorCreate`, `IddCxMonitorArrival`, `IddCxMonitorDeparture`; the same monitor reconnected
needs departure and arrival again); IDDs run in Session 0 as UMDF drivers; `DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY`
has `INDIRECT_WIRED` (16) and `INDIRECT_VIRTUAL` (17); `DISPLAYCONFIG_TARGET_DEVICE_NAME` carries the
friendly name and EDID manufacturer/product ids when `edidIdsValid`; `SDC_TOPOLOGY_EXTEND` restores the
last extended configuration from the persistence database. **Not documented:** whether
Windows.Graphics.Capture or Desktop Duplication can capture an indirect display, and whether a new
monitor is extended automatically. Every peer project captures VDD monitors with Desktop Duplication or
FFmpeg's ddagrab, which is evidence but not proof for Windows.Graphics.Capture.

## 5. The critical question: can the existing capture take the virtual monitor?

**Expected yes, unproven.** An IDD monitor is a WDDM display path driven by the IDD adapter; DWM
composes it like any other, it gets a GDI device name (`\\.\DISPLAYn`) and an `HMONITOR`, and
`IGraphicsCaptureItemInterop::CreateForMonitor` takes an `HMONITOR`. **[Read/derived]** The capture
code already resolves monitors by device name and refuses any other (D68), so Extend needs no new
capture path. What must be proven on hardware (E-H2): that the capture item for the VDD monitor yields
frames on this hybrid-GPU laptop (the IDD's render adapter and the capture device must cooperate); that
a mode change ends the capture (the current pipeline treats a size change as a fault, ES-057); that the
monitor's disappearance is reported as `monitor_not_found`.

## 6. Initial plan (as first written)

1. Bundle the VDD installer in the ExtendSpace ZIP; on first Extend, run it elevated from the app.
2. A small elevated helper service performs `SETDISPLAYCOUNT` over the pipe to create the monitor at
   session start and remove it at session end, and writes the tablet's resolution into
   `vdd_settings.xml`.
3. Call `SetDisplayConfig` to extend the desktop onto the new monitor and place it right of the main one.
4. Capture it, stream at 1920 × 1080, map input onto it.
5. Android unchanged.

## 7. Critique (hostile review)

| Perspective | Flaw |
| --- | --- |
| Windows driver expert | `SETDISPLAYCOUNT` re-initialises the adapter: every session start and end would flicker all virtual monitors and hits the code path behind the open watchdog/BSOD reports. VDD has no hot add/remove; the plan pretends it does |
| Security engineer | An elevated helper is a permanent privileged attack surface on every user's PC, for convenience. The pipe is reportedly writable by every process: ExtendSpace relying on it adds nothing a local attacker cannot already do, but driving it from an elevated service would. Bundling a driver means owning its security updates |
| Maintainer | Bundling binds releases to a third-party driver's cadence and to its regressions (#474, #490); MIT allows it, the support burden does not |
| Systems architect | Writing the XML needs admin (`C:\` root folder); `SetDisplayConfig` "to extend and position" rewrites the topology database and can move physical monitors (the brief: never silently change the physical configuration) |
| QA | None of it can be tested here without installing a driver; the plan has no smaller proof first |
| UX | A user who only wanted Mirror gets a driver install prompt; an elevated prompt per session is unacceptable |
| Normal user | "Why does ExtendSpace want administrator rights?" |
| Streaming | Fixed 1920 × 1080 letterboxes any non-16:9 virtual display (the Pad 7 is 3:2): a 16:9 "monitor" on a 3:2 tablet wastes a third of a strip |
| Lifecycle | With VDD the monitor exists whenever the driver is enabled, ExtendSpace running or not; the plan's "remove at session end" depends on the reload path above |

## 8. Revised plan (implemented)

| Area | Initial | Critique | Revised (D87–D91) |
| --- | --- | --- | --- |
| Driver | Bundle VDD, install elevated from the app | Privileged, support burden, regressions | **User installs VDD** themselves (guided: winget command, project link); ExtendSpace never installs or elevates (D87). Parsec VDD is the planned second provider for its keepalive lifecycle; own driver stays E5 |
| Creating the monitor | Pipe `SETDISPLAYCOUNT` per session | Reload path, BSOD reports | **ExtendSpace creates nothing**: it detects the VDD monitor Windows already has (D88) |
| Topology | `SetDisplayConfig` extend + position | Rewrites physical layout | **Read-only**: if the virtual monitor is not part of the desktop, ExtendSpace tells the user to choose *Extend these displays* in Display settings (D88). Programmatic topology deferred to a hardware-proven milestone |
| Capture | new | — | **Mirror's pipeline with the virtual monitor's device name** as the source; substitution impossible (D68), so Extend never captures a physical display (D89) |
| Stream shape | 1920 × 1080 | Wastes the tablet | **Shaped to the virtual monitor**: largest even size of its shape inside the 1920 × 1080 envelope (portrait: 1080 × 1920), Extend only; Mirror unchanged (D89) |
| Input | — | Must not leak to physical displays | **Same mapper by device name**: the virtual monitor's rectangle; touches outside its picture ignored (D83, ES-132 fixed) (D89) |
| Protocol | none | Tablet cannot say "extended display" | Optional `stream_config.mode` = `"extend"`; older peers ignore it (D90) |
| Lifecycle | remove at end | VDD cannot | **Documented**: the VDD monitor exists while VDD is enabled; ExtendSpace leaves nothing behind of its own; disable the monitor in Display settings or VDD's control app when not wanted (D91) |
| Proof first | — | — | **Milestone E-M1 = detection and identification, PC-verified read-only; E-M2..M5 in code; hardware proof needs the owner to install VDD** |

## 9. Risks and open questions

| # | Risk | Mitigation |
| --- | --- | --- |
| X1 | WGC cannot capture the VDD monitor on a hybrid-GPU laptop | Hardware check E-H2; fallback: Desktop Duplication path (not built) |
| X2 | VDD instability on current Windows builds (#474, #490, #531) | User-installed, documented recovery (uninstall VDD; Safe Mode); ExtendSpace never reloads it |
| X3 | VDD identity strings are from a summary | Detection also accepts `INDIRECT_VIRTUAL` output technology and the "Virtual" friendly name; verified on hardware |
| X4 | A persistent virtual monitor confuses users when no tablet is connected | UX text; Parsec provider later (keepalive) |
| X5 | Resolution of the virtual monitor is VDD's list, not the tablet's exact size | User picks in Display settings; ExtendSpace shapes the stream to whatever it is |
| X6 | A mode change during a session ends it (ES-057) | Unchanged behaviour; reconnect; dynamic reconfiguration is later work |

## 9a. What hardware showed (2026-11-23)

The owner installed VDD 25.7.23 (driver 11.30.4.434, both files validly signed by SignPath Foundation,
hardware ID `Root\MttVDD`, a UMDF driver) from VDD Control after winget fetched the control app.
Windows created `\\.\DISPLAY9` and extended the desktop onto it without being asked, at 800 × 600, the
first mode in VDD's default list (16:9 modes and 800 × 600, 30 Hz). The existing Windows.Graphics.Capture
path captured it (section 5's question: **yes**, on this hybrid-GPU laptop), and Extend streamed it over
USB and Wi-Fi with touch landing on it alone. Results: [acceptance](../quality/extend-mode-beta-acceptance.md).

## 10. Sources

VDD: repository README, LICENSE, Releases, Tags, commits, `Virtual Display Driver (HDR)/vdd_settings.xml`,
`MttVDD/Driver.cpp` (first ~40k characters read verbatim, the rest summarised), issues search page 1.
Parsec VDD: nomi-san/parsec-vdd README, LICENSE, `core/parsec-vdd.h`, releases and commits API.
SudoVDA: README, `Common/Include/sudovda-ioctl.h`, `AdapterOption.h`, releases and commits API.
Apollo README. Microsoft Learn: iddcx-objects, IddCxMonitorArrival, indirect display driver model
overview, DISPLAYCONFIG_TARGET_DEVICE_NAME, DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY, SetDisplayConfig,
Desktop Duplication API, screen capture. Phase 9 sources for the peer projects.
