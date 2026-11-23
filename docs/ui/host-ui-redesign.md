# Host UI redesign (Windows and Linux)

2026-11-23. A design proposal, **not implemented**. The audit is from the source
(`apps/windows/src/ExtendSpace.Host`, `apps/linux/src/ExtendSpace.Host.Linux`) and the shared
`ConnectViewModel`; no screenshots were taken in this mission, so pixel details are not claimed.
Shared design language: section 9. Tablet: [android-tablet-redesign.md](android-tablet-redesign.md).

## 1. Current UI audit

### Windows (WinUI 3)

| Surface | What is there today |
| --- | --- |
| Window | Mica, custom 48 px title bar, `NavigationView` left pane (220 px): **Home**; footer **Diagnostics**; the built-in **Settings** item |
| Home (title "Connect") | One scrolling column of cards, in this order: (1) status card — icon, `StatusTitle`, `StatusDetail`, stream health line, **Connect**/**Disconnect** at its right; (2) error `InfoBar` repeating `StatusDetail`; (3) "Not available in this build" `InfoBar`; (4) **Android device** card — list, refresh, "Not listed? Tablet's IP address" + Add, **Pair…**, **Forget**, pairing status; (5) **Extended display** card — status text, mode advice, "Use 1920 × 1080 at 60 Hz", "Bring windows back", "Display settings" link; (6) **Display to mirror** combo; (7) two cards side by side: **Connection** (USB/Wireless radio with descriptions) and **Mode** (Mirror/Extend radio); (8) **Input from the tablet** (Off/Touch/Mouse radio, note) |
| Diagnostics | "Application", **"Features in this build"**, "Stream (updated every second)" with an explanatory paragraph |
| Settings | "Wireless" text and an About card that says **"Extend mode is not implemented yet"** — stale: Extend exists (D87–D94) |

### Linux (Avalonia 11.3, under XWayland)

| Surface | What is there today |
| --- | --- |
| Window | 820 × 860, `TabControl` with top tabs **Home · Diagnostics · About**; no Settings |
| Home | Same view model, **different order**: status (Connect/Disconnect) → error card "The last connection failed; the details are above." → **Connection** → **Android device** → **Mode** → **Extended display** → **Display to mirror** → **Input** |
| Diagnostics | Code-built: "This computer" (version, desktop, runtime, log path, input paths, adb) and "Stream" |
| About | Version and notes |

## 2. Problems

| # | Problem | Evidence | Effect |
| --- | --- | --- | --- |
| H1 | **The order of decisions is backwards on Windows.** The device list (whose content depends on USB vs Wireless) comes before the Connection choice, and Mode comes after Display | HomePage.xaml card order | Users choose a device, then change transport and the list changes under them |
| H2 | **Windows and Linux differ** in order, navigation (left pane vs top tabs), page set and wording | XAML vs AXAML | Two products, double documentation |
| H3 | **Mirror vs Extend is a radio row** with paragraph descriptions, at the same weight as the transport choice | Mode card | The product's central choice is not visible at a glance |
| H4 | **Primary action is above the choices it depends on**; on a small window Connect scrolls away | status card first, choices below | Configure → scroll up → Connect |
| H5 | **Developer language**: "Not available in this build", "Features in this build", "Implemented" | InfoBar, Diagnostics | Feels like a test harness |
| H6 | **Errors repeated or deferred**: the Windows InfoBar repeats `StatusDetail`; Linux says "the details are above" | both Home pages | Noise; no action button tied to the fix |
| H7 | **Device management is hidden**: trusted tablets appear only as list rows with Pair/Forget below; no list of paired tablets, no last-used device | device card | Users cannot tell what is trusted |
| H8 | **Extend controls are scattered**: status in one card, virtual-display buttons in it, mode choice in another | Extend card + Mode card | Extend setup is hard to follow |
| H9 | **Settings is nearly empty and wrong** (Windows), missing (Linux) | SettingsPage.xaml | No home for preferences; stale claim |
| H10 | **Nothing is remembered** (ES-175): transport, mode, input reset every start | issue ledger | Every session starts with setup |
| H11 | **Capability limits are text notes** (orange caption under an option) rather than states with a fix | `StatusNote` | Limits are read late or not at all |
| H12 | **Input can be changed during a session but nothing says so**; its effect is not visible on Home's status | Input card | Uncertainty whether the tablet controls the PC |
| H13 | **Linux-specific**: consent dialogs (portal) are not prepared for; users meet a system dialog without warning | flow | Surprise; cancelled dialogs look like failures |

## 3. Design goals

1. Understandable in five seconds: *which tablet, Mirror or Extend, is it on, is input on*.
2. One primary action, placed after the choices and always visible.
3. Same structure, same words on Windows and Linux; native controls on each.
4. Limits are **states with a reason and a next step**, never footnotes.
5. Diagnostics complete but out of the way; no developer wording in user surfaces.
6. Implementable over the existing `ConnectViewModel` (views rewritten, view model extended, Core
   untouched otherwise).

## 4. Initial information architecture (proposal v1)

Home · Devices · Display · Input · Diagnostics · Settings · About (seven entries).

## 5. Critique of v1

| Perspective | Critique |
| --- | --- |
| Product designer | Seven destinations for a utility that is used for one action. Input as a page is overkill: it is three options. About belongs in Settings |
| Desktop UX | Splitting Display from Home means Extend setup happens away from the Start button. Keep the mode choice on Home; Display holds *detail* (resolution, virtual display management) |
| First-time user | Needs a guided first connection, not a page tour. "Devices" must not be the first stop when nothing is paired |
| Power user | Wants Start with remembered settings in one click, and a tray/notification-area entry to stop without opening the window |
| Accessibility | Mode tiles must be real radio buttons (arrow keys, announced as "Mirror, 1 of 2, selected"), not clickable cards |
| Maintainer | Two XAML dialects (WinUI, Avalonia) must stay in sync: the structure has to be simple and driven by view-model state, not code-behind |
| Linux reviewer | GNOME's portal chooses the monitor. A "Display to mirror" combo on GNOME is misleading; the screen must say "you'll choose in the next dialog" |

## 6. Revised information architecture (final)

```
Home            the one place to start, stop and change a session
Devices         tablets: paired, nearby/USB, add by address, pair, forget
Display         Mirror source / Extend virtual display detail, resolution, refresh, limits
─────────────── (footer)
Diagnostics     stream figures, environment, logs, copy report
Settings        General · Connection · Input · Privacy & security · Startup · Advanced · About
```

Input lives on Home (a segmented control) with its explanation in Settings → Input. A **first-run
setup** replaces Home until one tablet has connected once. Optional later: a tray/indicator menu
(Windows notification area; Linux needs StatusNotifierItem, which GNOME shows only with an extension —
so on Linux the window stays primary).

Navigation: left pane on both platforms (WinUI `NavigationView`; Avalonia a `SplitView` with a
`ListBox` pane styled to match). Pane collapses to icons below 840 px window width, and to a top
`ComboBox`-style switcher below 560 px.

## 7. Screens

### 7.1 Home

**Purpose:** start, watch and stop a session. **User:** everyone, every time.

```
┌──────────────────────────────────────────────────────────────────────────┐
│ ▣ ExtendSpace                                                   ─ □ ✕   │
├────────────┬─────────────────────────────────────────────────────────────┤
│ ⌂ Home     │  ┌───────────────────────────────────────────────────────┐  │
│ ▭ Devices  │  │ [tablet glyph]  Xiaomi Pad 7            ● Ready        │  │
│ ▢ Display  │  │                 USB · paired          Change tablet ›  │  │
│            │  └───────────────────────────────────────────────────────┘  │
│            │                                                             │
│            │  How should the tablet work?                                │
│            │  ┌────────────────────────┐  ┌────────────────────────┐     │
│            │  │ ◉  [▭][▭] same picture │  │ ○  [▭][▭→] new space   │     │
│            │  │ Mirror                 │  │ Extend                 │     │
│            │  │ Show this screen       │  │ Add a second screen    │     │
│            │  │ Main display (2560×1440)│ │ 1616 × 1080 · 60 Hz    │     │
│            │  └────────────────────────┘  └────────────────────────┘     │
│            │                                                             │
│            │  Control from the tablet   [ Off | Touch | Mouse ]           │
│            │  Touch works like a touchscreen on the shown display.  ⓘ    │
│            │                                                             │
│            │  Connection  [ USB | Wi-Fi ]                                │
│            │                                                             │
│            │                                   [   Start mirroring   ]   │
├────────────┤                                                             │
│ ⚕ Diagnost.│                                                             │
│ ⚙ Settings │                                                             │
└────────────┴─────────────────────────────────────────────────────────────┘
```

| Element | Detail |
| --- | --- |
| Device header | Tablet name, transport and trust chips, status pill (Ready / Connecting / Mirroring / Extended / Reconnecting / Needs attention), "Change tablet ›" opens a flyout list (same data as Devices) |
| Mode tiles | Two radio tiles, 1:1 width, illustration + title + one-line purpose + current target line (display name or virtual size). Unavailable tile: dimmed with **reason + action** ("Needs the free Virtual Display Driver · Set up…") — still focusable, announced as unavailable |
| Input | Segmented control Off/Touch/Mouse. Unavailable segment disabled with tooltip and the reason in the helper line. Changeable during a session (as today) — the status pill shows "· Touch" while active |
| Connection | Segmented USB/Wi-Fi; hidden when only one is possible for the chosen tablet |
| Primary button | Bottom-right, sticky (stays visible when the page scrolls): label follows state — "Start mirroring", "Start extended display", "Stop", "Cancel" (while connecting), "Try again" |
| During a session | Tiles and connection collapse to a summary row; a **live strip** appears: fps, latency class (Good/Fair/Poor), transport, duration, "Details ›" (Diagnostics) |
| Before a portal dialog (Linux) | Inline note above the button: "Next, Ubuntu asks which screen to share. Choose your main display." with "Don't show again" |

States: **Empty** (no tablet ever connected) → first-run screen (7.7). **No tablet available now** →
header shows last tablet greyed "Not found — connect the cable or open ExtendSpace on the tablet" + "Find
tablets" button. **Loading** (adb scan) → header skeleton, button disabled with "Looking for tablets…".
**Error** → status pill "Needs attention", one `InfoBar`/banner with the plain cause and **one action
button** matching the fix (Pair again / Retry / Open Display settings / Install adb guide); no
duplicated detail text. **Reconnecting** → pill animates; button "Stop".

Responsive: ≥ 1000 px: content max width 880 px centred. 640–1000 px: tiles side by side, pane
collapsed to icons. < 640 px: tiles stack, segmented controls full width, button full width.
Minimum window 480 × 560.

Keyboard/accessibility: Tab order header → tiles (one stop, arrows switch) → input (one stop, arrows) →
connection → primary button. Ctrl+Enter = primary action. Esc during connecting = Cancel. Status pill
changes are announced via a live region (UIA `LiveSetting=Polite` / Avalonia `AutomationProperties.LiveSetting`).
Never colour-only: pill has text and icon.

### 7.2 Devices

**Purpose:** see and manage tablets. **User:** first setup, occasional management.

```
 Devices                                              [ + Add by address ]
 ── This tablet is connected ─────────────────────────────────────────────
 [▭] Xiaomi Pad 7        USB  ·  Paired  ·  last used today       [ ⋯ ]
 ── Available ────────────────────────────────────────────────────────────
 [▭] Galaxy Tab S9       Wi-Fi · Not paired                      [ Pair ]
 ── Paired, not available ────────────────────────────────────────────────
 [▭] Old tablet          last seen 12 Oct                         [ ⋯ ]
                                          ⋯ = Use · Rename · Forget…
```

Pairing flow (dialog): step 1 "Open ExtendSpace on the tablet and tap Pair with a PC"; step 2 large
six-digit code on both, "Do the codes match?" [Pair] [Cancel]; step 3 done. Forget: confirmation dialog
naming the consequence ("The tablet must pair again before it can connect over Wi-Fi.").
Empty: illustration + "No tablets yet" + two paths: "USB cable" (steps, adb check inline) and "Wi-Fi"
(pair). Loading: per-section progress line. Errors inline per row ("Not responding at 192.168.1.40").
Discovery failing is normal: "Add by address" is always visible, not a fallback hidden in a text box.

### 7.3 Display

**Purpose:** what is shown and in what shape. **User:** setting up Extend; changing resolution.

| Section | Windows | Linux GNOME/KDE | Linux X11 |
| --- | --- | --- | --- |
| Mirror source | Display picker with a small layout preview of monitors (from `IDisplayCatalog`) | "Chosen in the sharing dialog each time (or remembered)" + "Forget remembered screen" | Monitor picker |
| Extend: virtual display | Status of the Virtual Display Driver, current mode, "Use 1616 × 1080 at 60 Hz" style advice (D92), Bring windows back, Display settings | "Created by your desktop while connected"; requested vs actual size; KDE 6.6 limit stated | Experimental RandR, warning |
| Resolution / quality | Stream size follows tablet (D16/D92); bitrate preset Low/Balanced/High (future) | same | same |
| Refresh rate | Requested 60 or 30; measured shown during session | compositor decides; measured shown | same |
| Limits | Plain list of what this computer cannot do and why | | |

States: unsupported → the section shows the reason and a "Learn more" link, never an empty control.

### 7.4 Diagnostics

Sections: **Session** (state, transport, mode, input, duration), **Video** (capture backend, encoder +
hardware yes/no + fallback reason, GPU, resolution, delivered fps, capture/encode p50/p95, bitrate,
drops), **Connection** (adb version and device, Wi-Fi address, pairing state), **This computer** (OS,
desktop/session, portal versions on Linux, runtime), **Warnings** (plain sentences), **Logs** (open
folder, copy report to clipboard — redacted serials and addresses by default). Updated every second
while visible. Remove "Features in this build".

### 7.5 Settings

| Group | Items |
| --- | --- |
| General | Theme (System/Light/Dark), remember last session (on), start minimised |
| Connection | Preferred connection (USB/Wi-Fi/Ask), auto-reconnect window, adb location (auto/custom) |
| Input | Default input mode, explanation of Touch vs Mouse with a diagram, "Pause input with Ctrl+Alt+Pause" (future) |
| Privacy & security | Paired tablets count → Devices; "Forget screen-sharing permissions" (Linux tokens); pairing key protection status (DPAPI / Secret Service / file permissions — disclosed, ES-151) |
| Startup | Start with Windows / at login (Linux autostart .desktop) |
| Advanced | Encoder override (Auto / list), diagnostics logging level |
| About | Version, build, platform, licenses, documentation and support links |

### 7.6 Dialogs and notifications

Pairing code dialog; Forget confirmation; "Stop sharing?" only when windows sit on the virtual display
("2 windows are on the tablet display. They will move to your main display."). Notifications (Windows
toast / Linux `org.freedesktop.Notifications`): session started (off by default), connection lost and
recovered, ended by the other side.

### 7.7 First run

Three steps in the content area, skippable: (1) "Choose how you'll connect" USB / Wi-Fi with one-line
requirements; (2) the chosen path's checklist with live checks (adb found ✓, tablet authorised ✓ / Wi-Fi
tablet found, pair); (3) "Ready" → Home with the tablet selected. Linux adds a check for missing GStreamer
plugins and portal backend with the exact `apt install` line (copy button).

## 8. Windows / Linux adaptation

| Topic | Windows | Linux |
| --- | --- | --- |
| Toolkit | WinUI 3, Fluent controls, Mica | Avalonia 11 Fluent theme (`FluentTheme`), solid background |
| Navigation | `NavigationView` | `SplitView` + `ListBox` pane (replaces `TabControl`) |
| Segmented control | `SelectorBar` or styled `RadioButtons` | styled `ListBox`/`RadioButton` group |
| Font | Segoe UI Variable | Inter (Avalonia.Fonts.Inter) for consistent metrics on every desktop |
| Icons | Segoe Fluent Icons | Fluent System Icons (same shapes, bundled) |
| Theme | Follows system | Follows `org.freedesktop.appearance color-scheme` via the Settings portal when available |
| System dialogs | none in the flow | portal consent prepared by an inline note (7.1) |
| Tray | notification area icon (later) | not relied on (GNOME hides tray icons) |
| Shared | Same pages, same order, same strings (strings move to Core `HostText`, D101 pattern), same view-model state |

## 9. Design system (shared with Android)

| Token | Value |
| --- | --- |
| Brand personality | Calm, precise, quiet when working; explains itself when not |
| Brand colour | Indigo `#3B5BDB` (light) / `#B9C3FF` (dark) — already Android's fallback primary. Used for the primary button, the selected mode tile and focus rings only |
| Semantic | Success `#1E7F4F`, Warning `#9D5D00` (already used on Linux), Error `#C42B1C`, Info `#005FB8`; dark-mode variants at ≥ 4.5:1 on their surface |
| Neutrals | Platform surfaces (Mica / Avalonia Fluent / Material surfaces) |
| Spacing | 4-pt scale: 4, 8, 12, 16, 24, 32, 48; page padding 24 (desktop), card padding 16 |
| Radius | Desktop: controls 4, cards 8, tiles and dialogs 12 |
| Elevation | Desktop: borders not shadows (Fluent); one shadow level for flyouts |
| Type ramp (desktop) | Title 28/36 semibold · Subtitle 20/28 semibold · Body 14/20 · Body strong 14/20 semibold · Caption 12/16 |
| Status pill | 24 px high, 12 px radius, icon + word, colour from semantic set |
| Motion | 150 ms fades for state text, 250 ms for tile selection; respect "reduce motion" (Windows animation setting, GNOME `enable-animations`) |
| Shared vocabulary | Ready · Connecting · Mirroring · Extended · Reconnecting · Needs attention · Disconnected; Mirror · Extend; Off · Touch · Mouse; USB · Wi-Fi (not "Wireless") |

## 10. Implementation notes

- View-model additions (Core, additive): `ModeTile` items with `IsAvailable`, `Reason`, `ActionLabel`,
  `TargetSummary`; `PrimaryActionLabel`; `LastDevice`; persisted preferences (ES-175); `LiveStrip`
  figures from `StreamDiagnostics`. Windows behaviour must not change except where the redesign intends.
- Order of work: Settings/About fix (stale text) → Home rebuild on both → Devices → Display → Diagnostics
  → first run. Each step ships on both platforms together.
- Test: view-model unit tests for every state → label/availability mapping (as `sessionOverlay` is tested
  on Android); UI Automation smoke reads on Windows (existing technique); Avalonia headless tests on Linux.
