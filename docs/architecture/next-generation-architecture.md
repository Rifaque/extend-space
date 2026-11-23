# Next-generation architecture (Phases 9–11 and after)

Status: the target shape after Phases 10 and 11, decided in Phase 9. It extends, and does not
replace, the [architecture overview](overview.md) and the
[production session architecture](production-session-architecture.md).

## 1. Layers

| Layer | Windows | Android | Changes in Phases 10–11 |
| --- | --- | --- | --- |
| UI | WinUI 3 pages, `ConnectViewModel` | Compose screens, `ConnectViewModel`, `SessionActivity` | Wireless choice live; pairing; trusted lists; input choice; touch on the picture |
| Session | `SessionCoordinator` + `SessionStateMachine` | `ClientSessionController` + `ConnectionStateMachine` | none (transport-independent by construction) |
| Connection | `HostConnectionController` (handshake, keepalive, queue, recovery) | `UsbHostLink` (same roles) | `Link` over `Stream`; input frames read (host) and written (client) |
| Transport | `RoutedTransport` → `UsbTransport` / `WirelessTransport` | `CompositeHostListener` → local socket / `WifiEndpoint` | new; see [transport architecture](transport-architecture.md) |
| Security | `DeviceIdentity`, `TrustedDeviceStore`, pairing client | `KeystoreIdentity`, `TrustedHostStore`, pairing handler | new |
| Discovery | `DnssdBrowser` (OS DNS-SD) | `NsdAdvertiser` | new |
| Media | `DesktopVideoPipeline` | `MediaCodecVideoReceiver` | none |
| Input | `HostInput`, `InputMapper`, injectors | `TouchCapture` | new; see [input architecture](input-architecture.md) |
| Display creation | — | — | Extend, later: [Extend mode research](extend-mode-research.md) |

## 2. Rules carried forward

1. One session pipeline; transports differ only in route and security.
2. The PC owns the session and the choice of device; the tablet listens only when asked.
3. Nothing is accepted from the network before mutual authentication; nothing reaches the session
   from an unpaired peer.
4. Input is off by default, bound to one streaming connection, and released on every end path.
5. `SessionCapabilities.CurrentBuild` enables a feature only when it works end to end in code; what is
   still to be shown on hardware is in ISSUES.md.
6. Windows-only adapters live in `ExtendSpace.Media` (capture, encoder, DNS-SD, DPAPI, injection);
   the logic they serve lives in `ExtendSpace.Core`, where it is unit-tested.

## 3. Deliberately not built

A plugin or provider framework for transports (two routes do not justify one); a second connection
for input (section 11 of the research); custom mDNS; any cloud component; a virtual display driver.
