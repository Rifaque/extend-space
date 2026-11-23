# Security model

Status: principles, and since Phases 10–11 (November 2026) their implementation for Wi-Fi and input.
The USB transport, the Wi-Fi transport with pairing, and touch input exist in both apps; the display
driver does not. The Android app requests `INTERNET` and `CHANGE_WIFI_MULTICAST_STATE` for Wi-Fi and
listens on the network only while its user has it waiting with Wi-Fi allowed, or pairing. Design:
[transport architecture](../architecture/transport-architecture.md), [input architecture](../architecture/input-architecture.md),
[Phase 9 research section 7](../research/phase-9-research.md#7-security-research).

## What is being protected

ExtendSpace streams the contents of a person's screen, and will later accept input that controls
their computer. An attacker who can join a session can read everything on screen; one who can inject
input can act as the user. An attacker who can talk to the driver can add or remove displays.

## Principles

1. **Local only.** No account, no cloud service, no relay. Devices talk directly over a cable or the
   local network.
2. **No unauthenticated listeners.** Nothing accepts a network connection and then processes
   application data before the peer has proven it is a paired device. The USB path uses loopback and
   local sockets, never a network interface.
3. **Explicit pairing.** Trust between two devices is created only by a deliberate action the user
   confirms on both, and can be revoked on either.
4. **Keys stay on the device.** Long-term keys live in platform-protected storage, are never
   exported, and are excluded from backups. The Android app already opts out of backup and device
   transfer for this reason.
5. **The peer is untrusted input.** Every message is bounded in size and validated before use. A
   malformed message ends the session; it does not crash the process or leave capture, the encoder
   or a virtual display running.
6. **Least privilege.** The host app runs unelevated. Driver installation is a separate, explicit,
   user-initiated, elevated step. The app never installs a driver silently.
7. **Input is opt-in.** When input forwarding exists it will be off by default and limited to
   authenticated sessions.

## Threats and intended mitigations

| Threat | Mitigation | Status |
| --- | --- | --- |
| A device on the same Wi-Fi connects and views the screen | The tablet listens, the PC connects; mutual TLS 1.3; each side pins the other's key from pairing; an unpaired PC gets `not_paired` before any session message is read (D76) | Implemented; TLS loopback tests both sides; deliberate breakage of the trust check and of the pin check caught. Not attacked on a real network |
| An active attacker intercepts pairing | Commit-reveal numeric comparison bound to both TLS identities; both users confirm six digits; one attempt per window (D77) | Implemented; vectors, tests, cross-implementation run on one PC; commitment-check breakage caught. **Not externally reviewed (ES-120)** |
| A LAN device floods the tablet with connections | At most 4 TLS handshakes at once, 5 s each; more are closed before any TLS work; one session at a time | Implemented and tested |
| Someone pairs while the owner is not looking | The tablet accepts pairing only after its user taps *Pair with a PC*, for 120 s, and both users must confirm | Implemented |
| A paired device must be revoked | *Forget* on either side; the tablet then refuses the PC (`not_paired`), the PC no longer connects to the tablet | Implemented and tested |
| A stolen key file on the PC | DPAPI (current user) protects the PC's private key; the trust list holds no secrets | Implemented; DPAPI round trip run on this PC |
| The tablet's identity is tracked on the LAN | The DNS-SD TXT record carries a 16-digit key prefix and the device name while the tablet waits; an active prober could read the certificate anyway | Accepted, documented |
| The tablet injects input the PC user did not allow | Input is Off by default, offered per connection only when the PC user turns it on, accepted only on an authenticated route, and released on every end (D82, D84) | Implemented and tested; injection into Windows not yet run on hardware |
| Another app on the phone connects to the app's local socket | Accept only peers whose credentials are the ADB shell user | Implemented in experiment 01's debug-only live screen (shell or root user ID, anything else is closed). Connections from `adb` were accepted; a refusal has not been seen on a device, because no second app tried |
| Another program on the PC connects to the forwarded loopback port | The port exists only for the life of a session; *Open:* whether to require protocol-level authentication on USB as well | Open |
| A malicious or buggy peer sends oversized or malformed frames | Length checked against per-channel limits before allocation; strict parsing; fatal error on violation | Implemented and unit-tested on both sides in the apps, including pairing values and touch frames; not attacked on a device |
| A local unprivileged program drives the display driver | Restrictive security descriptor on the driver's control interface; every IOCTL input validated in the driver | Open; see [IddCx feasibility](../architecture/iddcx-feasibility.md#4-boundary-between-the-driver-and-the-c-host) |
| The host crashes and leaves a virtual monitor attached | The driver removes monitors itself when its controller disappears | Designed, unproven |
| Users are asked to trust a self-signed driver certificate | Not acceptable for distribution; self-signed installs are for the developer's own test machine only | Policy |
| Secrets committed by accident | No secrets exist yet; `.gitignore` excludes key and certificate file types | In place |

## Known cost of the USB prototype

The ADB transport needs USB debugging enabled on the Android device. That widens what a connected
computer can do to the phone well beyond ExtendSpace. It is acceptable for development and must be
stated plainly to anyone who tries an early build; a release-quality USB transport should not depend
on it.
