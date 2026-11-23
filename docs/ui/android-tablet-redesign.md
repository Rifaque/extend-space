# Android tablet redesign

2026-11-23. A design proposal, **not implemented**. Audit from the source
(`apps/android/app/src/main/java/com/rsxdevelops/extendspace/ui`, `res/values/strings.xml`); no new
screenshots were taken in this mission. Host side and the shared design system:
[host-ui-redesign.md](host-ui-redesign.md) §9.

**Constraint kept:** the protocol does not change in the first redesign milestone. Every proposal below
is marked **[UI]** (views only), **[Client]** (tablet logic, no protocol change) or **[Protocol]** (needs a
protocol change: deferred).

## 1. Current UI audit

| Surface | What is there today |
| --- | --- |
| Shell | `Scaffold` with a **bottom `NavigationBar`** of two items: **Connect** and **About**, on every screen size |
| Connect | One `LazyColumn` (20 dp side padding, no max width): title "ExtendSpace" + "Use this device as a display for your PC"; **status card** (state, detail, notice, **Wait for PC** or **Open session**); **pairing card** while pairing; **How to connect** card (three paragraphs of instructions, always shown); **PCs** section (only when hosts are known); **Connection methods** card (USB row with an "Implemented" style badge; Wi-Fi row with **Allow Wi-Fi connections** switch, this tablet's address, **Pair with a PC**); **Paired PCs** rows with **Forget** |
| Session (`SessionActivity`) | Full-screen, system bars hidden (swipe to show). While not streaming: a centred card (progress, title, detail, Cancel / Wait again / Try again / Close). While streaming: the picture only; tapping the picture (input off) toggles a controls bar ("Mirroring your PC" / "Extended display of … · W × H at N fps", **End session**); with input on, taps go to the PC and a **handle** opens the controls; Back shows the controls first, then leaves (D84). Controls flash briefly when the first picture appears |
| About | Version, developer, **"Build status"** with features marked implemented/not implemented |
| Theme | Material 3, dynamic colour on Android 12+, indigo fallback |

## 2. Tablet UX problems

| # | Problem | Effect |
| --- | --- | --- |
| A1 | **The tablet must be told to "Wait for PC" every time** before the PC can connect | The most common action needs two devices and a specific order; the PC user often fails first ("no app listening") |
| A2 | Bottom navigation bar on a large tablet, for two destinations, one of which is About | Phone pattern; wastes vertical space in landscape; About does not deserve top-level |
| A3 | Full-width single column on a ~11" landscape screen | Very long lines, cards stretched; most of the screen is empty or text |
| A4 | Instructions as a permanent text card | Noise after the first use; still not a guided flow for the first use |
| A5 | Developer wording: "Implemented"/"Not implemented" badges, "Build status" | Feels like a test build |
| A6 | Wi-Fi is a switch the user must find ("Allow Wi-Fi connections") before pairing makes sense | Pairing fails silently if the switch is off |
| A7 | Streaming controls are minimal and hidden; no indication of input state, link quality or transport | Users don't know whether touches reach the PC, or why it lags |
| A8 | With input on, the controls handle competes with the picture's edge; with input off, any tap toggles controls | Two different behaviours to learn |
| A9 | Session states and the Connect status use different words (Connected / Mirroring / Streaming) | Inconsistent with the host |

## 3. Android adaptive guidance applied

- **Window size classes** (Material 3 / Jetpack WindowSizeClass): width Compact < 600 dp, Medium
  600–840, Expanded 840–1200, Large 1200–1600, Extra-large ≥ 1600. Layout decisions use the **window**
  size, not the device: split-screen on a tablet can be Compact.
- **Navigation:** bar for Compact, **navigation rail** for Medium and up (`NavigationSuiteScaffold`
  picks automatically) — but with the IA below there are only two destinations, so no persistent
  navigation is needed at all.
- **Canonical layouts:** *list-detail* for "PCs + selected PC"; *supporting pane* for streaming info.
- **Edge-to-edge** is enforced for apps targeting SDK 35+ (this app targets 37): handle insets with
  `WindowInsets.safeDrawing` everywhere except the picture.
- **Android 16 large-screen behaviour:** for apps targeting 36+, orientation locks and resizability
  restrictions are ignored on displays ≥ 600 dp. The session screen must already cope with any size and
  rotation (D69 fits the picture; keep it so).
- Touch targets ≥ 48 dp; text scaling to 200 %; TalkBack labels; mouse/keyboard: hover states and
  `Esc` = Back.

## 4. Proposed product experience

### 4.1 Key behaviour change: "Ready" by default **[Client]**

While the Home screen is in the foreground, the tablet **is waiting** — over USB always, and over Wi-Fi
for already-paired PCs when Wi-Fi is on. The PC connects; the tablet switches to the session screen by
itself. "Wait for PC" disappears as a step. Security does not change: the Wi-Fi endpoint still accepts
only paired PCs (pinned TLS), and pairing still needs the explicit **Pair with a PC** window. Stopping
when Home leaves the foreground keeps the current "no background listener" property.
*Needs an owner decision (new D-number); it changes when the tablet listens, not how.*

### 4.2 Navigation structure

```
Home (single screen, adaptive)
 ├─ Pair with a PC (full-screen dialog / sheet)
 ├─ Session (separate activity, full screen)
 └─ Settings (screen)  ── About (section inside Settings)
```

No bottom bar, no rail: Settings is an icon button in the top app bar.

### 4.3 Home

**Expanded/Large (landscape tablet):** two panes, 5:7.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ ExtendSpace                                                        ⚙        │
├──────────────────────────────┬───────────────────────────────────────────────┤
│                              │ Your PCs                       [ + Pair ]     │
│        [ tablet + ✓ ]        │ ┌───────────────────────────────────────────┐ │
│                              │ │ [▣] DESKTOP-PC    Windows · Wi-Fi ✓       │ │
│      Ready to connect        │ │     Last connected today 21:04            │ │
│                              │ └───────────────────────────────────────────┘ │
│  Open ExtendSpace on your    │ ┌───────────────────────────────────────────┐ │
│  PC and choose this tablet.  │ │ [▣] ubuntu-laptop  Linux · USB only       │ │
│                              │ └───────────────────────────────────────────┘ │
│  ● USB ready   ● Wi-Fi ready │                                               │
│  This tablet: 192.168.1.40   │ Connecting with a cable? USB debugging must  │
│                              │ be on.  How ›                                  │
└──────────────────────────────┴───────────────────────────────────────────────┘
```

**Compact / Medium (phone, split-screen, portrait small tablet):** one column, max content width
600 dp, centred: hero "Ready to connect" → readiness chips → PCs list → Pair button.

| Element | Detail |
| --- | --- |
| Hero | Illustration, state word (shared vocabulary: Ready / Connecting / Mirroring / Extended / Reconnecting / Needs attention), one-sentence instruction |
| Readiness chips | "USB ready" (or "USB debugging off — How ›"), "Wi-Fi ready" (or "Wi-Fi off for ExtendSpace — Turn on"), the tablet's IP (copyable, used for Add by address on the PC) |
| PC list | Paired PCs with platform, transport capability, last connected; overflow: Rename, Forget… (confirmation) |
| Empty state | No paired PCs: "Use a USB cable now, or pair over Wi-Fi" with two buttons: "USB setup" (steps sheet: developer options, USB debugging, authorise) and "Pair with a PC" |
| Error state | Hero becomes "Needs attention" + the plain cause (the existing `ConnectionErrorCode` messages) + one action (Try again / Pair again) |
| Loading | Hero "Getting ready…" with progress for the brief listener start |

### 4.4 Pair with a PC

Full-screen dialog (Compact) or centred dialog 560 dp (Medium+): step 1 "On your PC, open Devices and
choose this tablet" with the tablet name and address; step 2 the **six-digit code in display type
(57 sp, tabular digits)**, "Does your PC show the same code?" [Pair] [Cancel]; step 3 "Paired with
DESKTOP-PC". Wi-Fi off → step 0 asks to turn it on in-place.

### 4.5 Session (streaming)

Principle: **the picture owns the screen.** Nothing resizes it; overlays float above it and fade.

```
 ┌───────────────────────────────────────────────────────────────────────────┐
 │                               ▔▔▔ (handle, 32×4 dp, 40 % opacity)          │
 │ ┌─────────────────────────────────────────────────────────────────────┐   │
 │ │ ● Extended · DESKTOP-PC · Wi-Fi · Good   Touch on  [ ⏸ Input ] [ ⓘ ] [ ✕ End ] │
 │ └─────────────────────────────────────────────────────────────────────┘   │
 │                                                                           │
 │                      (PC picture, letterboxed to fit)                     │
 │                                                                           │
 └───────────────────────────────────────────────────────────────────────────┘
```

| Element | Detail |
| --- | --- |
| Handle | Always present at the top centre (inside the safe area), 48 dp touch target; tap or swipe down opens the session bar. **One behaviour** whether input is on or off (fixes A8) **[UI]** |
| Session bar | Mode, PC name, transport, link quality (from received/shown fps and latency the tablet already measures — `LatencyTracker`), input state, Pause input, Info, End. Auto-hides after 4 s of no interaction |
| Pause input | Stops sending contact frames locally so the user can rest a hand on the screen **[Client]** — no protocol change |
| Info sheet | Bottom sheet (Compact) / side sheet 360 dp (Expanded): resolution, fps received/shown, decoder, transport, latency, host name; "Copy details" |
| Input state | The tablet knows only whether input is **offered** (`inputOffered`), not Touch vs Mouse: show "Control on" / "View only". Showing the mode, or changing it from the tablet, is **[Protocol]**, deferred |
| First session | One-time coach mark on the handle: "Swipe down here for controls" |
| Back | First Back opens the bar (as D84); second Back from the bar ends after a 3 s undo snackbar |
| Not streaming | Keep today's centred overlay card (tested mapping `sessionOverlay`), restyled; same words as Home |
| Rotation / resize | Existing fit logic (D69); bar repositions; never locks orientation |
| Immersive | Keep transient system bars by swipe; the handle sits below the status-bar swipe zone to avoid conflicts (≥ 24 dp from the top edge) |

Mouse-mode helpers (floating right-click / scroll buttons) are **not** proposed for the first milestone:
the PC's Mouse gestures (tap, drag, long-press, two-finger scroll) already cover them and floating buttons
cover the picture. Reconsider after hardware feedback.

### 4.6 Settings

| Group | Items |
| --- | --- |
| Connection | Allow Wi-Fi connections (moved here; on by default once paired), this tablet's name (used in the PC's list) |
| Paired PCs | List, Forget |
| Display | Keep screen on during session (on), show session bar on start (on) |
| Input | Pause input by default (off); haptic on tap (future) |
| About | Version, licences, privacy, help links (replaces "Build status") |

## 5. Critique of the first proposal and revisions

| Perspective | Critique | Revision |
| --- | --- | --- |
| Product designer | Initial idea had a "Nearby computers" list: the tablet cannot discover PCs (PCs find the tablet) | Removed; the list is **paired** PCs; discovery lives on the PC |
| Android tablet designer | Initial idea used a navigation rail with Home/Devices/Settings/About | Two destinations do not justify a rail; Settings as a top-bar action |
| Accessibility | A 4 dp handle is too small and low-contrast | 48 dp touch target, visible 32 × 4 dp pill with a dark scrim behind it, TalkBack label "Session controls" |
| First-time user | "Ready to connect" with no PC paired is confusing for USB users who don't need pairing | Empty state offers USB setup first, pairing second |
| Power user | Wants to end a session quickly without the bar | Two-finger long-press opens the bar (input off only); End remains one tap away in the bar |
| Maintainer | Auto-listening changes lifecycle code in `ClientSessionController` | Scoped as its own milestone with tests mirroring today's Wait-for-PC tests; UI-only work can ship first |
| Streaming architecture | Overlays must not trigger `SurfaceView` resizes (decoder restarts) | Overlays are Compose layers above an unchanged `SurfaceView`; no padding is applied to the picture |

## 6. Design system (Android expression)

| Token | Value |
| --- | --- |
| Colour | Dynamic colour on 12+ for surfaces; **brand indigo for the primary action and the hero illustration** regardless of wallpaper; semantic success/warning/error from the shared set (host doc §9), mapped to M3 roles |
| Type | Material 3 type scale, system font (Roboto Flex on most devices); pairing digits `displayLarge` with tabular figures |
| Shape | M3: small 8, medium 12, large 16, extra-large 28 (dialogs/sheets) |
| Spacing | 4-dp scale; screen margins 16 (Compact) / 24 (Medium) / 32 (Expanded+) |
| Icons | Material Symbols Rounded, same concepts as the desktop (Mirror, Extend, USB, Wi-Fi, Touch, Mouse) |
| Motion | M3 standard easing; session bar 200 ms slide/fade; respect "Remove animations" |
| Status | Same pill as desktop: icon + word |

## 7. Implementation order

1. [UI] Remove developer wording, About into Settings, top bar with Settings, max content width,
   two-pane Home at Expanded+ (WindowSizeClass). Tests: Compose UI tests do not exist yet — add
   screenshot/semantics tests for the state mapping (pure functions, as `sessionOverlay`).
2. [UI] Session handle + bar + info sheet.
3. [Client] Pause input.
4. [Client] Ready-by-default listening (decision + tests + hardware run on the Pad 7).
5. [Protocol] Input mode shown/changed from the tablet — after Ubuntu Phase B.
