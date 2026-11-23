# Ubuntu platform strategy

2026-11-23. Preparation for the first native Ubuntu session (Phase B). This document holds research and
a plan, not results: **nothing here has been run on Ubuntu hardware yet**. Architecture:
[ubuntu-host-architecture.md](../architecture/ubuntu-host-architecture.md). Test plan:
[ubuntu-acceptance-plan.md](../quality/ubuntu-acceptance-plan.md). Order of work:
[ubuntu-and-ui-redesign-roadmap.md](../roadmap/ubuntu-and-ui-redesign-roadmap.md).

Evidence tags, as in the earlier Linux research:

| Tag | Meaning |
| --- | --- |
| **[Doc]** | Official documentation, specification, upstream source or package archive |
| **[Bug]** | Upstream issue tracker entry (status read on the date below) |
| **[Report]** | Secondary source (news site, forum, third-party project); not relied on alone |
| **[Repo]** | Read in this repository's code |
| **[CachyOS]** | Measured on the owner's hardware on 2026-11-23 (Hyprland), not Ubuntu |
| **[Inferred]** | Reasoned from the above; must be confirmed on Ubuntu |

## 1. Direction

- Windows is the primary platform and release target. Ubuntu is the primary Linux target.
- **CachyOS is frozen** at `7c24c3f` (section 12). Its code stays; nothing is removed or rewritten.
- The Ubuntu baseline is **Ubuntu 26.04 LTS, Ubuntu Desktop (GNOME 50, Wayland)**. Everything else is
  measured against that.

## 2. Ubuntu release

| Fact | Source | Confidence |
| --- | --- | --- |
| Ubuntu 26.04 LTS "Resolute Raccoon" released April 2026, Linux 7.0, GNOME 50 | [Doc]/[Report] OMG! Ubuntu release post; korben.info beta notes | High |
| 26.04 is the first LTS **without an Xorg desktop session** for GNOME. X11 apps still run through XWayland | [Doc] Ubuntu Discourse "Ubuntu 25.10 drops support for GNOME on Xorg"; [Report] The Register (GNOME 50) | High |
| GNOME 50 removed the X11 session upstream; installing Xorg does not bring back a GNOME-on-Xorg login | [Report] The Register 2026-03-19 | High |
| Upgrades 24.04 → 26.04 open with 26.04.1 (July 2026) | [Report] OMG! Ubuntu | High |
| GStreamer in 26.04: **1.28.2** (security update 1.28.2-1ubuntu0.1) | [Doc] packages.ubuntu.com, resolute | High |
| .NET 10 is in the 26.04 archive (`dotnet10`, universe); 8 and 9 through a PPA | [Doc] Ubuntu developer docs; Microsoft .NET blog 2026-04-23 | High |
| Ubuntu 24.04 LTS: GNOME 46, GStreamer 1.24, Wayland default with Xorg session available; .NET 10 backported | [Doc] earlier research (WSL lab ran 24.04 + GStreamer 1.24); Ubuntu Discourse ".NET 10 is now available on Ubuntu 24.04" | High |

**Recommendation.** Install **Ubuntu 26.04.x LTS (Desktop, default GNOME)**. Do not install 26.10: an
interim release lasts nine months and is not what users run on their main PCs. Treat 24.04 LTS as a
**secondary** target (same portal APIs, older GNOME 46; also has an Xorg session) and test it only after
26.04 passes. The app's dependencies (portals, PipeWire, GStreamer) come from the distribution, so
"supports Ubuntu" means "supports specific LTS releases".

**Consequence for the plan.** On Ubuntu 26.04 GNOME there is **no X11 path at all**: no `ximagesrc`
capture, no XTest, no RandR Extend. Every feature goes through the portals. The X11 code stays for other
X11 desktops (section 9) but cannot help the primary target.

## 3. Desktop compatibility matrix (Ubuntu 26.04 family)

Status legend: **Supported** · **Limited** (supported with stated limits) · **Experimental** ·
**Research** · **Unsupported** · **Not evaluated**. All Linux rows are currently **unverified on
hardware**; the "expected" column is what the research predicts and what Phase B must confirm.

| Flavour / desktop | Default session (26.04) | X11 session | Portal backend | Capture | Remote input | Extend (true virtual monitor) | Tier and expected status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| **Ubuntu Desktop / GNOME 50** | Wayland only [Doc] | **None** [Doc] | xdg-desktop-portal-gnome | ScreenCast portal + PipeWire | RemoteDesktop portal: pointer, keyboard; touch expected [Inferred, verify] | ScreenCast `VIRTUAL` → Mutter virtual monitor [Doc, earlier research]; size from PipeWire negotiation | **Tier 1 · target: Supported** |
| **Kubuntu / KDE Plasma 6.6** | Wayland [Report: It's FOSS, Ubunlog] | `plasma-session-x11` installable, not supported by Kubuntu [Report] | xdg-desktop-portal-kde | ScreenCast portal | RemoteDesktop portal (KWin); persistence across reboot buggy [Bug KDE 480235, 517454] | `VIRTUAL` exists but **fixed at 1920 × 1080** until Plasma 6.8 [Bug KDE 485850, 512620: fixed in 6.8] | **Tier 2 · expected: Limited** (Extend not tablet-shaped on 6.6) |
| Xubuntu / Xfce 4.20 | X11 [Report] | yes (default) | xdg-desktop-portal-gtk (no ScreenCast/RemoteDesktop) [Inferred] | `ximagesrc` (existing code) | XTest mouse only (existing code) | RandR monitor on enlarged framebuffer, unverified on a WM (ES-152) | Tier 3 · Experimental (Mirror + Mouse) |
| Lubuntu / LXQt | X11 [Report: The Register 2026-05-06] | yes | lxqt/gtk portal (no ScreenCast) [Inferred] | `ximagesrc` | XTest | As Xfce | Tier 3 · Experimental |
| Ubuntu MATE | X11 [Report] | yes | gtk portal [Inferred] | `ximagesrc` | XTest | As Xfce | Tier 3 · Experimental |
| Ubuntu Cinnamon | X11 (Cinnamon Wayland is experimental upstream) [Report] | yes | xapp/gtk [Inferred] | `ximagesrc` | XTest | As Xfce | Tier 3 · Experimental |
| Ubuntu Budgie | Budgie 10.10 moved to Wayland on wlroots (labwc) [Inferred from upstream plans; **not confirmed for 26.04**] | unknown | likely xdg-desktop-portal-wlr [Inferred] | Portal (wlr) | **No RemoteDesktop in portal-wlr** [Doc, earlier research] | No portal `VIRTUAL` | Tier 3 · Research |
| Ubuntu Unity | X11 (Unity 7 / Compiz) [Inferred] | yes | gtk | `ximagesrc` | XTest | As Xfce; Compiz behaviour unknown | Tier 3 · Research |
| Ubuntu Kylin / UKUI | Not confirmed for 26.04 | unknown | unknown | unknown | unknown | unknown | Not evaluated |
| Ubuntu Studio | Uses KDE Plasma (since 21.04) [Report, verify] | as Kubuntu | as Kubuntu | as Kubuntu | as Kubuntu | as Kubuntu | Same as Tier 2; **no separate testing** |
| Ubuntu 24.04 GNOME 46 Wayland | Wayland, Xorg session available | yes | portal-gnome 46 | Portal | Portal | `VIRTUAL` (older Mutter; no fixed modes) | Tier 1b · after 26.04 |
| Ubuntu 24.04 GNOME on Xorg | — | yes | — | `ximagesrc` | XTest | RandR (Mutter on Xorg may ignore RandR 1.5 monitors) | Tier 3 · Experimental |

**What the matrix says.** Exactly two technology stacks matter for Ubuntu: **portals on Wayland**
(GNOME, KDE) and **plain X11** (Xfce, LXQt, MATE, Cinnamon, Unity). Every flavour falls into one of these
or into "wlroots without RemoteDesktop" (Budgie, CachyOS/Hyprland's cousin). ExtendSpace should support
**stacks, not flavours**: the app detects the capabilities (it already does: `LinuxEnvironment`,
`AvailableSourceTypes`, `HasRemoteDesktop`) and the UI names them.

## 4. Screen capture (ScreenCast portal)

| Item | Finding | Source | Confidence |
| --- | --- | --- | --- |
| Interface | `org.freedesktop.portal.ScreenCast` (CreateSession → SelectSources → Start → OpenPipeWireRemote) | [Doc] XDG portal spec | High |
| Source types | MONITOR 1, WINDOW 2, VIRTUAL 4; read `AvailableSourceTypes` | [Doc] | High |
| Persistence | `persist_mode` 2 + `restore_token` on SelectSources (v4+); token is **single use**, store the new one every time | [Doc] spec; [Bug] KDE 480235 | High |
| Persistence on GNOME | Works for ScreenCast since xdg-desktop-portal-gnome 42; survives reboot on GNOME 45 | [Bug] RH 2174946; KDE 480235 comments | High |
| Restore and locked screen | Persistent tokens reported blocked while the screen is locked | [Report] pinggy.io | Low |
| Cursor | `cursor_mode` EMBEDDED (2) used today [Repo] | [Doc] | High |
| Ubuntu 26.04 quirk | Virtual and portal streams on 26.04 reported **not updating on pointer motion alone** (only on damage); workaround `MUTTER_DEBUG_DISABLE_HW_CURSORS=1`. No GNOME answer yet | [Report] GNOME Discourse 2026-05-09 | Medium; **test early** (affects Mouse mode feel) |
| Frame delivery | PipeWire SHM or DMA-BUF; current pipeline sets `always-copy=true` and `keepalive-time=500` [Repo] | — | — |
| Size change | A stream can renegotiate; ExtendSpace ends the session on a size change (ES-057) | [Repo] | — |

## 5. Remote input (RemoteDesktop portal)

| Item | Finding | Source | Confidence |
| --- | --- | --- | --- |
| Interface | `org.freedesktop.portal.RemoteDesktop`: CreateSession → SelectDevices → (ScreenCast SelectSources on the same session) → Start | [Doc] | High |
| Devices | KEYBOARD 1, POINTER 2, TOUCHSCREEN 4; read `AvailableDeviceTypes` | [Doc] | High |
| Pointer | `NotifyPointerMotionAbsolute(stream, x, y)` in **stream coordinates**; buttons as evdev codes; axis smooth and discrete | [Doc]; [Repo] `DesktopPortal` | High |
| Touch | `NotifyTouchDown/Motion/Up` only if TOUCHSCREEN was granted | [Doc] | High (spec) |
| Touch on GNOME | Mutter's remote desktop implements touch; whether portal-gnome advertises TOUCHSCREEN in `AvailableDeviceTypes` on 26.04 is **not confirmed by a source** | [Inferred] | Medium: check with `busctl` in Stage 0 |
| libei | `ConnectToEIS` (RemoteDesktop v2): merged in xdg-desktop-portal, portal-gnome and Mutter | [Doc] libei tracking issue #1 | High. Not needed now: Notify* stays valid |
| **Persistence** | RemoteDesktop v2 carries `persist_mode`/`restore_token` on **SelectDevices**; passing them on SelectSources in a RemoteDesktop session is an **error** | [Doc] spec; [Report] computer-use-linux #185 | High |
| ExtendSpace today | RemoteDesktop sessions request **no persistence** at all [Repo `DesktopPortal.OpenAsync`] | — | **Gap: with input on, GNOME will ask every connection.** Fix in Stage 4 (architecture §5) |
| Consent | GNOME shows one dialog for "share screen + allow remote control"; user can refuse remote control | [Inferred] | Verify in Stage 4 |

## 6. Extend (genuine virtual monitor)

| Desktop | Mechanism | Size | Refresh | Lifetime | Source | Confidence |
| --- | --- | --- | --- | --- | --- | --- |
| GNOME 50 (portal) | ScreenCast `VIRTUAL` → Mutter `RecordVirtual` | Chosen by **PipeWire negotiation**: ExtendSpace offers the tablet size first (`video/x-raw,width,height` after `pipewiresrc`) [Repo] | Not documented; expect the stream's negotiated framerate up to 60 [Inferred] | Exists exactly as long as the portal session | [Doc] portal-gnome MR 4; [Report] Phoronix (GNOME 50 adds fixed modes and HiDPI scale tag to the **private** `RecordVirtual` API) | Medium-high; **the most important Stage 5 measurement** |
| GNOME private API | `org.gnome.Mutter.ScreenCast.RecordVirtual` with fixed modes (GNOME 50) | Exact modes | — | — | [Report] Phoronix 2026-02 | Not used: private, unavailable to sandboxed apps, "subject to change" |
| KDE Plasma 6.6 | ScreenCast `VIRTUAL` (KWin virtual output) | **1920 × 1080 only**; other sizes give no frames | — | session | [Bug] KDE 485850, 512620 (fixed in 6.8) | High |
| KDE Plasma 6.8+ | as above, resizable via negotiation | tablet size | — | session | [Bug] same, "FIXED-IN 6.8" | Medium (release not checked) |
| X11 desktops | RandR `--setmonitor` + `--fb` (D99) | any | n/a | ExtendSpace restores the framebuffer | [Repo] | Experimental, ES-152 |
| Budgie (wlroots) | none through the portal; labwc has no `create_output` equivalent known | — | — | — | [Inferred] | Research |

**Is "Extend" genuine on GNOME?** Yes, by the portal's design: Mutter creates a real logical monitor that
appears in Settings → Displays while the session lasts, windows can be moved onto it, and it is removed
when the session closes (including when the process dies, because the D-Bus session closes) [Inferred
from portal semantics; to verify in Stage 5]. Not available: choosing its position (Mutter places it;
the user rearranges in Settings), choosing the refresh rate explicitly, keeping it after disconnect.

## 7. Hardware acceleration and encoders

| Item | Finding | Source |
| --- | --- | --- |
| Encoder elements (GStreamer 1.28) | `nvh264enc` (nvcodec, gst-plugins-bad, loads the NVIDIA driver's libnvidia-encode at run time), `vah264enc` (va plugin, gst-plugins-bad), `vaapih264enc` (gstreamer-vaapi, deprecated upstream), `qsvh264enc` (oneVPL, -bad, if built), `x264enc` (-ugly) | [Doc] GStreamer docs; [Repo] `PipelineDescription` |
| Measured on CachyOS (PipeWire 1080p60) | NVENC ~3 ms hold, x264 ~1 ms more CPU, **VA-API ~67 ms (four frames)** only when fed by PipeWire (ES-173, cause unknown) | [CachyOS] ES-165 |
| Current order | NVENC → x264 → VA-API; `EXTENDSPACE_ENCODER` overrides [Repo] | — |
| Ubuntu packages | `gstreamer1.0-plugins-bad` (va, nvcodec, h264parse), `-ugly` (x264), `gstreamer1.0-pipewire`; Intel VA driver `intel-media-va-driver` (main) or `-non-free` (multiverse, needed for some encode paths); AMD `mesa-va-drivers` | [Inferred] from Debian/Ubuntu packaging; **verify with `gst-inspect-1.0` and `vainfo` in Stage 0** |
| **Packaging gap** | `install.sh` asks Ubuntu users for base/good/ugly/x/pipewire **but not `-bad`**: no `h264parse`, no `vah264enc`, no `nvh264enc` [Repo] | Fix in the Ubuntu package (architecture §9) |
| Hybrid NVIDIA + Intel | On GNOME Wayland the compositor normally renders on the integrated GPU; NVENC encodes from system memory after `always-copy` (one copy, measured fine on CachyOS) [Inferred] | Measure |

Policy and diagnostics: architecture §7.

## 8. USB (adb) on Ubuntu

| Item | Finding | Confidence |
| --- | --- | --- |
| Package | `adb` (universe) [Inferred, verify]; udev rules from `android-sdk-platform-tools-common` (shipped since 16.04) [Report] | Medium |
| Permission model | Modern rules tag devices for the logged-in seat (`uaccess`), older ones use `plugdev` [Report] | Medium: check the installed rule file |
| Root | Never needed by ExtendSpace; adb server runs as the user | High |
| Authorisation | Tablet shows "Allow USB debugging?" once per host key (`~/.android/adbkey`) | High |
| Forwarding | `adb forward` entries are dropped when the device disconnects (seen on Windows) [Repo memory] — Core already re-forwards on reconnect | High |
| Multiple devices | Core chooses by serial (D65) | High |

Test plan: acceptance plan, Stage 2.

## 9. Wi-Fi, discovery, pairing

- The tablet listens; the host connects out (D74). **No inbound port on Ubuntu**, so no firewall rule for
  the stream.
- Discovery uses Core's own one-shot mDNS querier (D103), sending legacy-unicast queries from an ephemeral
  port; avahi-daemon (installed by default on Ubuntu) keeps port 5353 and is unaffected.
- **Hypothesis to test:** with **UFW enabled** (off by default on Ubuntu Desktop), the tablet's unicast
  reply to a query that went to the multicast address may not match connection tracking and be dropped.
  If so, discovery fails while add-by-address works. Test both UFW states in Stage 2.
- CachyOS found the tablet in 1.1 s: **historical only**. Discovery was intermittent on the Windows LAN
  (ES-134); add-by-address stays first-class in the UI.
- IPv6 link-local: not supported by add-by-address today; keep IPv4 for Phase B.

## 10. Secret storage

| Option | Ubuntu GNOME | Kubuntu | Xfce/LXQt/MATE | Headless/SSH | Flatpak |
| --- | --- | --- | --- | --- | --- |
| Secret Service (D-Bus `org.freedesktop.secrets`) via libsecret | GNOME Keyring, unlocked at login | KWallet implements Secret Service in Plasma 6 [Inferred, verify] | gnome-keyring usually installed but may not auto-unlock | none or locked | through the Secret portal |
| Current ExtendSpace | PKCS#12 identity in `~/.local/share/ExtendSpace`, 0700/0600, **not encrypted** (ES-151) [Repo] | | | | |

Recommendation (design only, not tonight):

1. **Default: Secret Service.** Store a random 256-bit key there; encrypt the identity file with it
   (AES-GCM). The file stays where it is; only the key moves. Works with GNOME Keyring and KWallet alike.
2. **Locked keyring:** the unlock prompt is the desktop's; ExtendSpace waits, and if refused, offers
   "pair again later" rather than writing an unencrypted file.
3. **No Secret Service** (minimal X11 desktops): keep today's 0700 file as a **disclosed fallback**
   ("Your pairing key is protected by file permissions only") in Settings → Privacy. Refusing to persist
   would make Wi-Fi pairing unusable on Tier 3 desktops, and the threat (offline copy of the home folder)
   already exposes far more than a tablet pairing.
4. Migration: on first start with Secret Service available, encrypt the existing file in place.

## 11. Packaging

| Format | Portals/PipeWire | GPU encoders | adb/USB | GStreamer | Updates | Effort | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Tarball + `install.sh` (exists, D102) | host's | host's | host's | host's | reinstall | done | Keep for developers and Tier 3 |
| **`.deb` for Ubuntu LTS** | host's | host's drivers | `Recommends: adb, android-sdk-platform-tools-common` | `Depends:` exact plugin packages | APT (a PPA or a downloadable .deb) | low-medium | **Recommended next (initial Ubuntu format)** |
| Flatpak | best (designed for portals) | NVIDIA needs the matching GL extension; VA via runtime | **adb inside the sandbox needs `--device=all`** and a bundled adb | runtime | Flathub | medium-high | Later; best for non-Ubuntu distributions |
| AppImage | host's | host's | host's | bundled or host (fragile plugin mix) | manual | medium | Not recommended (GStreamer plugin mismatches) |
| Snap | portal interfaces exist; strict confinement complicates adb/raw USB | content snaps | `raw-usb` interface, manual connect | bundled | automatic | high | Not now |

**Why `.deb` first:** ExtendSpace leans entirely on the system's portals, PipeWire, GPU drivers and
GStreamer plugins. A `.deb` declares them and lets APT install them in one step — the main Phase A pain
was missing plugins and a false dependency warning (ES-170). The app itself stays self-contained .NET
(no runtime dependency). Install to `/opt/extendspace`, launcher in `/usr/share/applications`, user data
untouched on removal (purge documented).

## 12. CachyOS freeze

| | |
| --- | --- |
| Status | **Frozen experimental support.** No active development, no compatibility commitment |
| Checkpoint | `7c24c3f` (validation and fixes), on top of `46f98e2` |
| Proven there | Mirror (USB, Wi-Fi) 60 fps; Extend via Hyprland headless output 1616 × 1080 at 60 Hz; Mouse via wlr virtual pointer with smooth scroll; pairing; USB cable-pull recovery; packaging; host pipeline p95 537 ms → 5.5 ms (NVENC), 59.9 fps, 27 % CPU |
| Not run there | Manual IP, Forget, killed process mid-session, sleep/wake, network change, tablet restart, 10-minute soak, other desktops (ES-179) |
| Code that stays | `Hyprland/`, `Wayland/VirtualPointer`, per-output restore tokens, D104–D107 |
| Revisit only for | Regressions in shared code, security issues |
| **Lessons carried to Ubuntu** | (1) measure encoder latency, never trust the "hardware first" assumption (ES-165); (2) `videorate drop-only` (ES-166); (3) release capture on every failure path (ES-161); (4) single-instance lock (ES-164); (5) remember the last tablet's shape before the handshake (ES-162); (6) probe portal interfaces before asking for them (ES-160); (7) check the portal gave the output that was asked for (ES-167) |

## 13. Open questions (answer in Stage 0–5)

1. Does portal-gnome 50 advertise TOUCHSCREEN? (`busctl --user get-property org.freedesktop.portal.Desktop /org/freedesktop/portal/desktop org.freedesktop.portal.RemoteDesktop AvailableDeviceTypes`)
2. RemoteDesktop interface `version` on 26.04 (≥ 2 means persistence on SelectDevices).
3. ScreenCast `AvailableSourceTypes` includes VIRTUAL (expect 7)?
4. Does the virtual monitor accept 1616 × 1080, and at what refresh rate does it run?
5. Does pointer-only motion produce frames (the 26.04 damage report)?
6. Which encoders exist with stock packages plus the NVIDIA driver; VA-API latency on Ubuntu's 1.28?
7. adb udev rule file and group on 26.04.
8. Discovery with UFW on and off.
9. Avalonia 11 under XWayland with GNOME fractional scaling: sharp or blurry?

## 14. Sources (checked 2026-11-23)

| Source | Confirms |
| --- | --- |
| [Ubuntu 26.04 LTS released (OMG! Ubuntu)](https://www.omgubuntu.co.uk/2026/04/ubuntu-26-04-lts-released) | 26.04 release, no Xorg session |
| [Ubuntu 25.10 drops support for GNOME on Xorg (Ubuntu Discourse)](https://discourse.ubuntu.com/t/ubuntu-25-10-drops-support-for-gnome-on-xorg/62538) | Official Wayland-only Ubuntu session |
| [GNOME 50 (The Register)](https://www.theregister.com/2026/03/19/gnome_50/) | X11 session removed upstream |
| [GNOME may rule Resolute Raccoon, X.org isn't roadkill (The Register)](https://www.theregister.com/oses/2026/05/06/gnome-may-rule-ubuntu-resolute-raccoon-but-xorg-isnt-roadkill-yet/5219477) | Flavours that keep X.org (Lubuntu etc.) |
| [Kubuntu 26.04 LTS (It's FOSS)](https://itsfoss.com/news/kubuntu-26-04-lts/) | Plasma 6.6, Wayland only by default |
| [gstreamer1.0 in resolute (packages.ubuntu.com)](https://packages.ubuntu.com/source/resolute/gstreamer1.0) | GStreamer 1.28.2 |
| [Available .NET versions (Ubuntu docs)](https://documentation.ubuntu.com/ubuntu-for-developers/reference/availability/dotnet) | .NET 10 in 26.04 |
| [What's new for .NET in Ubuntu 26.04 (Microsoft)](https://devblogs.microsoft.com/dotnet/whats-new-for-dotnet-in-ubuntu-2604/) | Same |
| [RemoteDesktop portal spec](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.RemoteDesktop.html) | Devices, touch gating, ConnectToEIS, persistence on SelectDevices (v2) |
| [libei portal tracking issue](https://gitlab.freedesktop.org/libinput/libei/-/issues/1) | ConnectToEIS merged in portal, portal-gnome, Mutter |
| [Persistent remote desktop sessions (xdg-desktop-portal #1004)](https://github.com/flatpak/xdg-desktop-portal/pull/1004) | Frontend persistence design |
| [Red Hat bug 2174946](https://bugzilla.redhat.com/show_bug.cgi?id=2174946) | ScreenCast persistence in portal-gnome 42 |
| [KDE bug 480235](https://bugs.kde.org/show_bug.cgi?id=480235), [517454](https://bugs.kde.org/show_bug.cgi?id=517454) | KDE persistence defects, single-use tokens |
| [KDE bug 485850](https://bugs.kde.org/show_bug.cgi?id=485850), [512620](https://bugs.kde.org/show_bug.cgi?id=512620) | KDE virtual screen 1920 × 1080 until Plasma/KWin 6.8 |
| [Ubuntu 26.04: virtual desktops no longer update without damage (GNOME Discourse)](https://discourse.gnome.org/t/ubuntu-26-04-virtual-desktops-no-longer-update-wthout-damage/34987) | Pointer-only updates missing; HW cursor workaround |
| [GNOME 50 virtual monitor / HiDPI (Phoronix)](https://phoronix.com/news/gnome-50-remote-desktop-hidpi) | Fixed modes on private RecordVirtual (page not fetchable; search summary only) |
| [Avalonia 12.1 release](https://avaloniaui.net/blog/release-12-1) | Native Wayland backend opt-in, experimental |
| [computer-use-linux #185](https://github.com/agent-sh/computer-use-linux/issues/185) | GNOME rejects persistence on SelectSources in RemoteDesktop sessions (third party) |

Not found (open): an authoritative portal-gnome NEWS entry for RemoteDesktop persistence; the GNOME
merge request 88 page (HTTP 406); per-flavour 26.04 session tables for Budgie, Unity, Kylin.
