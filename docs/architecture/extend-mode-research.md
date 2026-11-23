# Extend mode: research and roadmap

Status: **research only (Phase 9). No driver code exists and none is written in Phases 9–11.** Builds
on [IddCx feasibility](iddcx-feasibility.md) (October 2026), which remains the detailed study of the
driver model and toolchain; this document adds the November 2026 findings and the milestone plan.

## 1. What has to exist

Capture APIs (Windows.Graphics.Capture, Desktop Duplication) read monitors; they cannot create one.
For Windows to extend the desktop onto the tablet, Windows must enumerate a monitor that it can place
in Display Settings, give modes to, and render into. The supported user-mode way is an **Indirect
Display Driver (IDD)** on **IddCx** (UMDF, Session 0). Microsoft names "creating virtual monitors" as
one of the model's scenarios. ExtendSpace then captures that monitor with the **existing** Mirror
pipeline; Extend is "a different display source", as the architecture overview requires.

## 2. Options

| Option | Effort | Distribution | Control | Risk | Verdict |
| --- | --- | --- | --- | --- | --- |
| **A. Adopt the community Virtual Display Driver (VirtualDrivers/Virtual-Display-Driver, MIT)** | Low | Signed through SignPath Foundation; GitHub releases and winget | Settings XML (`C:\VirtualDisplayDriver\vdd_settings.xml`) and its own control app; programmatic control API: not found | Their release cadence and settings format; whether bundling is welcome (MIT allows it; to confirm with the maintainers) | **E1 route** |
| B. Own IddCx driver from Microsoft's sample | High: WDK, C++, signing pipeline | Needs EV certificate + Hardware Developer Program; attestation documented as for testing, retail via WHCP/HLK | Full: our own IOCTLs, add/remove on demand, our EDID | Signing cost and maintenance; Session 0 debugging | Only if A proves unworkable |
| C. Fork VDD | Medium | Own signing again | Full | As B for signing | Not before A is measured |
| D. Other projects' drivers (Parsec VDD, Apollo/SudoVDA, RustDesk IDD) | Low–medium | Varies; licences to check | Varies | Licence/compatibility | Survey in E1 |
| E. No driver (an existing spare output, a dummy HDMI plug) | none | n/a | none | Hardware dependency | Document as a user workaround only |

## 3. Milestones (each with an exit test)

| # | Milestone | Exit criterion |
| --- | --- | --- |
| E0 | Licence and distribution check of VDD; ask maintainers about bundling; record decision | Written answer or decision to require user install |
| E1 | **Prototype with VDD installed by the user**: ExtendSpace lists the virtual monitor in "Display to mirror", mirrors it (existing path), touch maps to it (existing mapper, by device name) | Windows dragged onto the tablet on the owner's PC; input lands on it |
| E2 | Detect VDD, guide install, choose resolution to match the tablet (settings file + driver reload, or VDC), restore on session end | Session start/stop leaves Display Settings as it found it |
| E3 | Mode as a first-class choice: Extend = "create/enable the virtual monitor, capture it"; keep it attached during `Reconnecting`, remove after the budget (protocol section 12) | Reconnect does not move windows; failure removes the monitor |
| E4 | Sleep/wake, monitor removal, driver failure, multiple tablets assessment | Fault table tested |
| E5 | Decision point: stay on VDD or own driver (B), with data from E1–E4 | Decision log entry |

## 4. Interactions already prepared by Phases 10–11

- Input targets a monitor **by device name**; a virtual monitor is just another name.
- The capture path already takes a device name and refuses to substitute another monitor (D68).
- Transport and session code know nothing of modes.

## 5. Risks

Driver install needs administrator rights (one prompt); a user-installed driver can be removed under
us (detect and say so); Windows may cancel touch contacts and re-place windows on mode changes;
HDR/refresh features of VDD are not needed and should be left off; security: whoever can write the
VDD settings file can change monitors (it lives under `C:\`; check its ACL in E0).

---

**Update 2026-11-23:** milestone E1 is done and superseded in detail by the Extend mode mission:
[extend-mode-driver-research.md](../research/extend-mode-driver-research.md) (research, plan, critique,
revised plan) and [extend-mode-architecture.md](extend-mode-architecture.md).
