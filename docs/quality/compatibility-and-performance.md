# Compatibility and performance (Phase 6)

What ExtendSpace v1 supports, what has actually been run, and what is still a risk. Measured figures
are quoted from the device runs in [production session architecture](../architecture/production-session-architecture.md)
sections 19–20 (stage 4 and 4.1, October 2026) and [experiment 01](../architecture/streaming-experiment-01.md);
**no new device measurement was made in Phases 5–8.**

## Supported configuration for the beta

| | Supported | Tested on | Notes |
| --- | --- | --- | --- |
| Windows | Windows 11, x64 | One PC (Windows 11 build 26300) | Manifest floor is 10.0.22000 (Windows 11 21H2). Windows 10 not tried; ARM64 builds but not tried |
| PC display | Any monitor Windows lists; the main one by default | One PC, main display, 1080p60 and 720p30 streams | Larger displays are scaled down to the stream size on the GPU |
| Encoder | Media Foundation H.264, hardware preferred | One hardware encoder | The software encoder path (V1) not run |
| adb | Android SDK Platform-Tools on `PATH` or under `ANDROID_HOME` | Platform-Tools 37.0.1 (adb 1.0.41) | No minimum version enforced; very old adb is untested |
| Android | 10 (API 29) to 17 (API 37) | One Xiaomi Pad 7, `c2.qti.avc.decoder` | `minSdk 29`, `targetSdk 37` |
| Android form factor | Tablets and phones, both orientations | Tablet only | Picture fitting is unit-tested for phone and tablet shapes (D69) |
| Cable | USB data cable, direct port | One cable | Hubs and docks untried |

## Risk-based test matrix for Phase 7 hardware (owner-run)

Ordered by risk. "Why" is the failure that would surface there first.

| Priority | Configuration | Why | Minimum check |
| --- | --- | --- | --- |
| 1 | Two Android devices from other vendors (a Samsung and a Pixel, ideally one phone) | Decoder startup, backlog and surface-loss behaviour were tuned on one Qualcomm decoder (ES-052, ES-053) | Acceptance rows S1–S6, L1–L4 |
| 2 | A second PC with a different GPU vendor (Intel or AMD vs this PC) | Encoder output check (D57), keyframe forcing, capture | S1, S2, P1 |
| 3 | A 4K or high-refresh main display | GPU scaling cost, pacer at 144 Hz | S2, P1–P3 |
| 4 | Two monitors, mirroring the secondary | Display choice (D68), capture of a non-primary monitor | C9, S8 |
| 5 | Display scaling at 150 % and 200 % | Enumerated sizes, capture size | S8 |
| 6 | Emulator plus tablet attached | Device choice among several (V17) | C3 |
| 7 | Windows 10 | Below the manifest floor? Decide support | Launch only |

## Performance: what is known

| Figure | Value | Source | Production path? |
| --- | --- | --- | --- |
| Frames delivered at 1080p60, 60 minutes | 216,212 sent, all drawn, none lost | Section 20.3 (V10) | Yes |
| Render latency on the tablet after the first second | mean 19–21 ms, p95 27–30 ms | Section 20.11 | Yes |
| First frame after the stream starts (cold decoder) | 328–456 ms | Sections 20.3, 20.10 | Yes |
| Encoder turnaround, worst in a 10-minute soak | 23.9 ms | Section 8.2 | Experiment |
| Ping round trip over USB | 1.6–4.1 ms | Section 19.6 | Yes |
| Glass to glass | about 46 ms | Experiment 01 | Experiment only |
| Recovery from a short client freeze | 5–12 ms in most rounds; one 404–417 ms round explained by D63 and changed by D64 | Sections 20.12–20.13 | Yes; **D64 not yet re-measured** |
| Memory over 60 minutes | no growth on either side | Section 20.3 | Yes |
| Tablet temperature, 60 minutes | 36–37 °C, thermal status 0 | Section 20.3 | Yes |
| Host CPU and GPU load | **not measured** on the production path | — | — |
| Tablet battery drain | **not measured** | — | — |

**Overhead added in Phase 5:** the stream diagnostics read existing counters once a second and log
only on a change of health state; nothing is computed per frame. Not measured; by construction it is
a handful of allocations a second.

**Defaults kept:** 1080p60 at 12 Mbit/s when the client can decode it, else 720p30 at 6 Mbit/s
(D16); send queue of 4 frames; client backlog of 4 (30 while a new decoder catches up, D60). No
evidence suggests changing them; the measurements above were taken with them. No optimisation was
made in Phase 6, deliberately: no bottleneck has been measured.

**To measure before the beta gate (owner-run, acceptance rows P1–P5):** host CPU/GPU in Task
Manager during a 10-minute 1080p60 stream; tablet battery percentage over 30 minutes; first-frame
time from Connect; recovery time after a cable pull.
