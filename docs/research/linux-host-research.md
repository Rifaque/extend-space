# Linux host: research, plan, critique, revised plan

2026-11-23. Decisions D96–D103 in the [decision log](../architecture/decision-log.md); architecture in
[linux-host-architecture.md](../architecture/linux-host-architecture.md). Tags: **[Doc]** official
documentation or source; **[Lab]** measured here (WSL2 Ubuntu 24.04, Xvfb); **[Unverified]** not
confirmed.

## 1. What the repository already gives Linux

`ExtendSpace.Core` is plain `net10.0` (no Windows API): protocol, session coordinator, recovery state
machine, USB transport through adb (already looks for `adb` without `.exe` off Windows), Wi-Fi TLS 1.3
transport, pairing ceremony, trusted-device store, input model and gestures, display-mode model (D92),
Connect view model, diagnostics. A Linux host implements the same eight interfaces as the Windows one:
`IVideoPipeline`, `IDisplayCatalog`, `IVirtualDisplayCatalog`, `ITouchInjector`, `IMouseInjector`,
`IMonitorLocator`, `ISecretProtector`, `IServiceBrowser` — plus a UI. The Android client needs no change:
the protocol is the host's contract, not Windows'.

## 2. Distributions and environments

| Target | Display server default | Desktop | Portal backend | Notes |
| --- | --- | --- | --- | --- |
| Ubuntu 24.04 / 26.04 LTS | Wayland (X11 session available) | GNOME 46 / 50 | xdg-desktop-portal-gnome | Largest user base; GNOME 50 adds fixed virtual-monitor modes to `RecordVirtual` [Doc: Phoronix on GNOME 50] |
| Fedora Workstation 43/44 | Wayland only (GNOME dropped the X11 session) | GNOME | gnome | Newest GNOME; PipeWire, portals current |
| Debian 13 | Wayland (GNOME) or X11 | GNOME/KDE/Xfce | per desktop | Slower cadence |
| Linux Mint 22 | **X11** | Cinnamon | xapp/gtk | Cinnamon's Wayland is experimental |
| Arch-based | user's choice | any | any | Rolling; community packaging |
| KDE Plasma 6 (Kubuntu, Fedora KDE, Arch) | Wayland | KDE | xdg-desktop-portal-kde | KWin virtual outputs exist (used by KRdp) [Doc: portal-kde source has a virtual-output path; whether `AvailableSourceTypes` includes VIRTUAL is **Unverified**] |
| Sway/Hyprland (wlroots) | Wayland | — | xdg-desktop-portal-wlr / -hyprland | `swaymsg create_output` makes `HEADLESS-1` [Doc]; portal-wlr has **no RemoteDesktop** |
| Xfce 4.20 | X11 | Xfce | gtk | |

**Recommended official matrix (initial):** Ubuntu 24.04+ and Fedora 43+ on **GNOME Wayland**; KDE
Plasma 6 Wayland; any distribution on an **X11** session (Mint/Cinnamon, Xfce). wlroots compositors:
Mirror only, community-supported. Everything else: best effort.

## 3. Display servers: what differs

| Capability | X11 | Wayland (GNOME/KDE) | Wayland (wlroots) |
| --- | --- | --- | --- |
| Screen capture | Any client can read the root window (XShm/`ximagesrc`) — no consent, no security boundary | Only through the ScreenCast portal + PipeWire, after the user consents (restore token from portal v4 avoids asking every time) | Portal-wlr (ScreenCast) |
| Enumerate monitors | XRandR 1.5 monitors | Not directly; the portal picks | `swaymsg -t get_outputs` |
| Create a monitor | RandR `--setmonitor` on an enlarged framebuffer (no CRTC scans it out); WM-dependent; or a disconnected real output | **ScreenCast `VIRTUAL` source type** creates one for the session: GNOME maps it to Mutter `RecordVirtual` [Doc: xdg-desktop-portal-gnome MR 4] | `create_output` (sway), `hyprctl output create headless` |
| Inject input | XTest: any client, mouse/keyboard only (no touch) | RemoteDesktop portal: `NotifyPointerMotionAbsolute`, buttons, axis, **`NotifyTouchDown/Motion/Up`**, coordinates **relative to a ScreenCast stream** [Doc: portal spec]; newer: `ConnectToEIS` (libei) | wlr virtual-pointer protocol; no portal |
| uinput | Possible but needs `/dev/uinput` access (root or a udev rule) — not default | same | same |

## 4. Capture and encoding options

| Option | X11 | Wayland | HW encode | Dependency | Verdict |
| --- | --- | --- | --- | --- | --- |
| **GStreamer** (`ximagesrc` / `pipewiresrc` → convert/scale → `x264enc` or `vah264enc`/`vaapih264enc`/`nvh264enc` → `appsink`) | yes | yes (portal fd + node) | VA-API, NVENC when the plugins exist | Installed by default on GNOME/KDE distros (plugins-good, -ugly for x264) | **Chosen** (D97) |
| FFmpeg (`x11grab`) | yes | **no PipeWire input** | VA-API/NVENC | ffmpeg | Not chosen: no Wayland |
| Direct PipeWire + own encoder | — | yes | own work | libpipewire + an encoder | Too much new native code |

[Lab] GStreamer 1.24 from Ubuntu's packages, unpacked into a user folder: `ximagesrc` → `x264enc`
captured an X region and produced 60 frames of Constrained Baseline H.264 (`ffprobe`), including a region
right of a 1920-wide "physical" area. The capture → shared pipeline join point is the Annex-B access
unit: Core's `EncodedVideoSink` takes exactly that, so the session, transport and protocol are reused
unchanged.

## 5. Extend options

| Candidate | Independent display | Windows move there | Captured | Dynamic create/remove | Root | Wayland | X11 | Distributable | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **ScreenCast portal `VIRTUAL`** (GNOME, KDE) | yes (compositor's own virtual monitor) | yes | yes (the stream) | **yes, lives exactly as long as the session** | no | yes | no | yes (standard API) | **Tier 1** (D98) |
| Mutter `RecordVirtual` direct | yes | yes | yes | yes | no | GNOME only | no | private API "subject to change" [Doc: GNOME Discourse] | Not used: the portal wraps it |
| `swaymsg create_output` | yes | yes | via portal-wlr | yes | no | wlroots only | no | yes | Tier 3 (documented, Mirror of the output) |
| X11 RandR `--setmonitor` + `--fb` | logical monitor only; no CRTC | depends on WM honouring RandR 1.5 monitors | yes (`ximagesrc` region) [Lab] | yes (restore fb size) | no | no | yes | yes | Tier 2, **experimental** (D99): [Lab] Xvfb's RandR ignored the monitor, so WM behaviour is **Unverified** |
| evdi (DisplayLink kernel module) | yes (real DRM connector) | yes | yes | yes | **DKMS module, root** | yes | yes | GPL, needs Secure Boot signing | Rejected for now: kernel module install |
| vkms | separate DRM device the desktop does not use | no | — | — | root | — | — | — | Rejected |
| Xorg `dummy` driver / `VirtualHeads` (intel) | yes | yes | yes | no (Xorg config + restart) | root | no | yes | — | Documented workaround only |

## 6. Input options

| Mode | X11 | GNOME/KDE Wayland | wlroots |
| --- | --- | --- | --- |
| Mouse | **XTest** (absolute root coordinates; works without permissions) — mouse emulation | RemoteDesktop portal pointer (stream-relative absolute motion, buttons, axis) | none without uinput (limitation) |
| Touch | **Not offered** (XTest has no touch; uinput would need root/udev and cannot be bound to the virtual monitor reliably) | RemoteDesktop portal **touch** (`NotifyTouchDown/Motion/Up`) on the stream: genuine compositor touch events, confined to the stream's monitor by construction | none |

Input stays Off by default (D82) on Linux too. The portal asks the user to allow remote control; no
uinput, no root, no udev rule is installed by ExtendSpace (D100).

## 7. USB, wireless, storage

* **USB**: the same adb transport. Users install `adb` from the distribution
  (`android-tools-adb`/`android-tools`), whose package ships udev rules for Android devices; the host
  runs unprivileged. Core already finds `adb` on `PATH` or under `ANDROID_HOME`.
* **Wi-Fi**: the tablet is the TLS server; the host connects out, so no listener and no firewall rule on
  Linux. Discovery: a **managed mDNS one-shot querier** in Core (RFC 6762 §5.1 legacy unicast queries
  from an ephemeral port, answered by unicast) — no Avahi dependency, coexists with avahi-daemon on 5353,
  same `IServiceBrowser`; add-by-address remains (ES-134).
* **Identity at rest**: no DPAPI. Initial: the PKCS#12 identity in `~/.local/share/ExtendSpace` with
  `0700`/`0600` permissions (as `~/.ssh` keys) — **weaker than DPAPI against an offline copy of the
  home directory**; libsecret/Secret Service is the follow-up (ES-151).

## 8. UI framework

| Option | Reuse of `ConnectViewModel` | Wayland | Packaging | Accessibility | Verdict |
| --- | --- | --- | --- | --- | --- |
| **Avalonia 11** (C#, XAML, MIT) | Direct binding to the existing INotifyPropertyChanged view model | runs via XWayland (native Wayland backend not yet stable) | self-contained | AT-SPI on Linux | **Chosen** (D96) |
| GTK 4 via GirCore | Bindings by hand | native | needs GTK runtime | native | More native, far less reuse |
| Qt | C++/QML, no reuse | native | heavy | native | No |

## 9. Packaging

| Format | Portals | adb/udev | GStreamer | Effort | Verdict |
| --- | --- | --- | --- | --- | --- |
| **Self-contained tarball + per-user install script** (`~/.local`, .desktop entry, uninstall script) | host's | host's packages | host's | low | **Initial** (D102), mirrors the Windows ZIP |
| Flatpak | best (sandbox-friendly) | USB needs `--device=all` and host adb | runtime extension | medium | Next |
| AppImage | host's | host's | bundle or host | medium | Later |
| deb/rpm | host's | dependencies declared | declared | per-distro | After the beta |

## 10. Initial plan

L1 foundation · L2 full Avalonia UI · L3 GStreamer capture for X11 and portal · L4 streaming · L5 mouse
(XTest + portal) · L6 Extend on **all** environments with the same "create a monitor" button, including
X11 via RandR and wlroots via `create_output` · L7 touch via **uinput** everywhere · L8 modes · L9
recovery · L10 packages deb, rpm, Flatpak, AppImage.

## 11. Critique

| Perspective | Objection |
| --- | --- |
| Wayland specialist | One "create monitor" button cannot hide that GNOME/KDE create it through the portal (with a consent dialog) and X11 through RandR (no consent, WM-dependent). Say which environment the user is in. |
| Security | uinput needs `/dev/uinput` write access: a udev rule granting it lets any user process synthesise keyboards — a privilege ExtendSpace must not hand out by default. The portal gives touch without it. |
| X11 specialist | `--setmonitor` creates no CRTC; GNOME-on-Xorg (Mutter) and Cinnamon may ignore it for maximise/panels. Unverified here (Xvfb). Mark experimental; restore the framebuffer on every exit path. |
| Graphics | Capture must not assume a fixed size: portal streams can renegotiate. Treat a size change like Windows (end the session, ES-057) until measured. |
| QA | Xvfb/WSL is not a desktop: classify those tests as "Lab", never as hardware acceptance of GNOME/KDE. |
| Package maintainer | Four package formats on day one is maintenance nobody owns. One format, done properly. |
| Maintainer | Do not fork Core. Platform text ("Windows reports no display") must become host-neutral in Core, not copied. |
| Normal user | Needs plain setup steps: install GStreamer plugins and adb with one distro command; what to click in the consent dialog. |

## 12. Revised plan (the implementation authority)

| Initial | Critique | Revised |
| --- | --- | --- |
| Same Extend button everywhere | Environments differ | **Capability-driven** Extend (D98, D99): GNOME/KDE Wayland → portal `VIRTUAL` (Tier 1, hardware pending); X11 → RandR monitor on an enlarged framebuffer, **experimental**, restored on every exit (Tier 2); wlroots → guidance only (Tier 3). The UI names the environment and its limits |
| uinput touch everywhere | Privilege | **No uinput** (D100): touch only through the RemoteDesktop portal; X11 offers Mouse only; documented as mouse emulation |
| Full feature set at once | Too much | L-M1 foundation, L-M2 UI, L-M3 capture X11 (Lab-verified) + portal, L-M4 streaming, L-M5 mouse, L-M6 Extend, L-M7 portal touch, L-M8 modes (reuse D92), L-M10 tarball |
| deb/rpm/Flatpak/AppImage | Maintenance | Tarball + per-user installer now (D102); Flatpak next (ES-153) |
| Copy Windows strings | Duplication | Host-neutral wording in Core via `HostPlatform` (D101) |
| Test on Xvfb = done | Misclassified | Lab tests in WSL Xvfb are labelled Lab; GNOME/KDE/real-X11 acceptance is a separate owner checklist |
