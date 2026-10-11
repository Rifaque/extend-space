# Security model

Status: principles and intended design. Pairing, transports and the driver do not exist yet, so none
of the protections below are implemented. The current apps open no sockets and request no
permissions.

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
| A device on the same Wi-Fi connects and views the screen | Mutual authentication against keys stored at pairing, before any other message | Designed in the [protocol draft](../../protocol/specification/protocol.md#6-device-identity-and-pairing) |
| An active attacker intercepts pairing | A ceremony that binds both devices' keys to something the user verifies on both screens | Open |
| Another app on the phone connects to the app's local socket | Accept only peers whose credentials are the ADB shell user | To confirm in experiment 01 |
| Another program on the PC connects to the forwarded loopback port | The port exists only for the life of a session; *Open:* whether to require protocol-level authentication on USB as well | Open |
| A malicious or buggy peer sends oversized or malformed frames | Length checked against per-channel limits before allocation; strict parsing; fatal error on violation | Designed |
| A local unprivileged program drives the display driver | Restrictive security descriptor on the driver's control interface; every IOCTL input validated in the driver | Open; see [IddCx feasibility](../architecture/iddcx-feasibility.md#4-boundary-between-the-driver-and-the-c-host) |
| The host crashes and leaves a virtual monitor attached | The driver removes monitors itself when its controller disappears | Designed, unproven |
| Users are asked to trust a self-signed driver certificate | Not acceptable for distribution; self-signed installs are for the developer's own test machine only | Policy |
| Secrets committed by accident | No secrets exist yet; `.gitignore` excludes key and certificate file types | In place |

## Known cost of the USB prototype

The ADB transport needs USB debugging enabled on the Android device. That widens what a connected
computer can do to the phone well beyond ExtendSpace. It is acceptable for development and must be
stated plainly to anyone who tries an early build; a release-quality USB transport should not depend
on it.
