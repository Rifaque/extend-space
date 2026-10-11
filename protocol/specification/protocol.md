# ExtendSpace protocol — draft 0

Status: **design draft. Nothing in this document is implemented.** It exists so the first streaming
experiment and both apps aim at the same target. Expect it to change once real frames flow; sections
marked *Open* are undecided on purpose.

The protocol is the same over USB and Wi-Fi. Transports differ only in how a connection is opened
and authenticated (see [Transport bindings](#3-transport-bindings)).

## 1. Roles

| Role | Device | Responsibility |
| --- | --- | --- |
| **Host** | Windows | Produces video. Owns the session: chooses the stream configuration and decides when the session ends. |
| **Client** | Android | Decodes and displays video. Reports its capabilities and its health. Later, sends input. |

Roles are independent of which side opens the transport connection. One host holds at most one
session with one client; multi-device sessions are out of scope.

## 2. Channels

Messages belong to a logical channel so that video and control never depend on each other's
encoding or scheduling:

| ID | Channel | Direction | Content |
| --- | --- | --- | --- |
| 0 | Control | both | Small structured messages: handshake, configuration, keepalive, errors. |
| 1 | Video | host → client | Encoded video access units. |
| 2 | Input | client → host | Reserved. See [Future input](#13-future-input-messages). |

A transport binding decides whether channels share one connection or use several. Draft 0 puts them
on one ordered byte stream. Keeping the channel in the frame header leaves room to move video to its
own connection, or to a datagram transport, without changing message definitions.

A receiver skips frames on a channel it does not know; the length prefix makes that safe.

## 3. Transport bindings

Requirements on any binding:

- Reliable, ordered delivery of bytes for the control channel.
- The listening side must not be reachable by an unauthenticated peer on a network.
- Both ends must be able to detect a dead connection within the keepalive timeout.

| Binding | How it connects | Authentication |
| --- | --- | --- |
| **USB (ADB)** | The host asks `adb` to forward a host loopback TCP port to a local (non-network) socket owned by the Android app, then connects to that port. | The cable and the device's ADB authorisation gate access. The Android app additionally accepts only connections that come from the ADB daemon. Loopback and local sockets only; nothing listens on a network interface. |
| **Wi-Fi** | *Open.* Likely: client connects to a host TCP listener found by local discovery. | Mandatory mutual authentication with keys established at pairing ([section 6](#6-device-identity-and-pairing)). The listener accepts no application data before authentication succeeds. |

## 4. Framing

All integers are big-endian.

**Connection preface.** Each side sends five bytes immediately after connecting: ASCII `EXSP`
followed by one byte, the framing version (`0x00` for this draft). A peer that sends anything else
is disconnected without a reply.

**Frame.** Everything after the preface is a sequence of frames:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| 0 | 1 | `channel` | See [Channels](#2-channels). |
| 1 | 1 | `type` | Message type within the channel. |
| 2 | 2 | `flags` | Channel-specific. Unknown bits are ignored. |
| 4 | 4 | `length` | Payload size in bytes, excluding this header. |
| 8 | `length` | payload | |

**Limits.** A receiver checks `length` against the channel's limit *before* allocating or reading
the payload. Exceeding it is a fatal `message_too_large` error.

| Channel | Maximum payload |
| --- | --- |
| Control | 64 KiB |
| Video | 8 MiB by default; the negotiated stream configuration may lower it |
| Input | 4 KiB |

**Control payloads** are UTF-8 JSON objects. Unknown fields are ignored. Nesting deeper than 8
levels is rejected. JSON is chosen for draft 0 because control traffic is tiny and being able to read
it in a log matters more than its size. *Open:* replace with a schema-driven binary encoding before
version 1.

**Video payloads** are binary:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| 0 | 4 | `sequence` | Increments by one per frame within a session. A gap tells the client that frames were dropped upstream. |
| 4 | 8 | `timestamp_us` | Capture time in microseconds on the host's monotonic clock. |
| 12 | rest | data | Exactly one H.264 access unit in Annex B byte-stream format. |

Video `flags`: bit 0 `KEYFRAME` (the access unit is an IDR picture), bit 1 `CONFIG` (it begins with
SPS and PPS).

## 5. Versioning

- The protocol version is `major.minor`. This draft is `0.1`. While the major version is 0, any
  change may be breaking.
- Each side states the range of major versions it supports in `hello`. The session uses the highest
  major version both support. No overlap is a fatal `protocol_version_unsupported` error.
- From version 1 on, a minor version only adds things a peer can ignore: new optional fields, new
  control message types, new flag bits.
- A receiver ignores control message types it does not know.

## 6. Device identity and pairing

*Design intent. The pairing ceremony is Open.*

- Each installation generates a long-term key pair on first run and keeps the private key in
  platform-protected storage (Android Keystore; DPAPI- or TPM-protected on Windows). Keys are never
  exported, backed up or committed. P-256 is proposed because hardware-backed support is broad.
- The **device ID** is the SHA-256 hash of the public key. A user-visible device name is advisory
  only and is never used to make a trust decision.
- **Pairing** binds two device IDs to each other with the user's explicit confirmation on both
  devices, and must resist an active attacker on the same network. Candidate ceremony: the host
  displays a short code or QR, the client confirms it, and both store the other's public key.
- After pairing, every Wi-Fi connection is mutually authenticated against the stored keys before any
  other message is accepted. An unknown or mismatched key is a fatal `not_paired` or
  `authentication_failed` error.
- Either side can unpair, which deletes the stored key and ends any session.

Over USB the user has already authorised the computer on the device. *Open:* whether USB sessions
also require an ExtendSpace pairing, or whether a USB session is how first pairing happens.

## 7. Session lifecycle

Both apps model a session with the same states:

| State | Meaning |
| --- | --- |
| `Disconnected` | No session and none requested. |
| `Connecting` | Opening the transport. |
| `Negotiating` | Transport is open; handshake, authentication and stream setup in progress. |
| `Streaming` | Video is flowing. |
| `Reconnecting` | The connection was lost unexpectedly and is being re-established. |
| `Disconnecting` | An orderly teardown is in progress. |
| `Failed` | The session ended with an error the user has not yet acknowledged. |

**Establishment**

1. Transport connects; both sides exchange the preface.
2. Both send `hello`.
3. Transport-specific authentication completes.
4. Client sends `capabilities`.
5. Host sends `stream_config`.
6. Client replies `stream_ready`, or `error` if it cannot honour the configuration.
7. Host starts sending video. The first frame carries `CONFIG | KEYFRAME`.

If any step does not complete within 5 seconds the side that is waiting sends a fatal `timeout`
error and closes.

**Teardown.** Either side sends `bye` with a reason and closes the connection after it is written.
The receiver stops immediately and does not attempt to reconnect. A connection that closes without
`bye` is treated as lost.

## 8. Capability negotiation and stream configuration

The client describes what it can do; the host decides.

`capabilities` (client → host) carries: the display's pixel size, density and refresh rate; and for
each supported decoder its codec, profile and level limits, maximum resolution and frame rate,
whether it is hardware-backed, and whether it supports a low-latency mode.

`stream_config` (host → client) carries: codec (`h264` in draft 0), profile, width, height, target
frame rate, target bitrate, and the maximum video payload size. The host chooses values within the
client's stated limits. If no acceptable combination exists the host sends a fatal
`no_common_configuration` error.

The host may send `stream_config` again during `Streaming`, for example when the mirrored display
changes resolution. The client replies `stream_ready`; the new configuration takes effect at the
next video frame flagged `CONFIG`.

## 9. Video delivery and backpressure

- Frames are sent in order, one access unit each. The encoder does not reorder frames (no B-frames).
- The host keeps a **bounded** send queue of at most a few frames. It never lets latency grow by
  buffering.
- When the queue is full the host prefers to **skip capturing or encoding** a frame, which is
  harmless. If an already-encoded frame must be discarded, every later frame depends on it, so the
  host discards up to the next keyframe and forces the encoder to produce one.
- The client decodes and presents frames as they arrive. It does not buffer for smoothness.
- A `sequence` gap or a decoder error makes the client request a keyframe.

## 10. Keyframe requests

`request_keyframe` (client → host) carries a reason: `startup`, `decode_error`, `sequence_gap` or
`surface_recreated`. The host responds by producing a `CONFIG | KEYFRAME` frame as soon as it can.
The host rate-limits these (no more than about two per second) and may coalesce several requests
into one keyframe. The host can also emit keyframes unprompted.

## 11. Keepalive and connection health

- The host sends `ping` once per second carrying an ID and its send time. The client answers `pong`
  with the same values. This gives the host a round-trip time.
- If either side receives nothing at all on any channel for 5 seconds it treats the connection as
  lost.
- The client sends `stats` once per second: frames received, decoded, presented and dropped, decode
  time, and its receive-queue depth. These feed diagnostics and, later, bitrate adaptation. They are
  never required for correctness.

## 12. Errors and reconnection

`error` carries a `code`, a human-readable `message` for logs, and `fatal`. After a fatal error the
sender closes the connection.

| Code | Meaning |
| --- | --- |
| `protocol_version_unsupported` | No common major version. |
| `malformed_message` | Bad preface, unparseable payload, or a missing required field. |
| `message_too_large` | A frame exceeded its channel limit. |
| `unexpected_message` | A valid message arrived in a state where it is not allowed. |
| `not_paired` | The peer's device ID is unknown. |
| `authentication_failed` | The peer could not prove its identity. |
| `no_common_configuration` | Capabilities and host requirements do not overlap. |
| `stream_config_rejected` | The client cannot apply the configuration it was sent. |
| `timeout` | A handshake step or a keepalive did not arrive in time. |
| `busy` | A session with another device is already active. |
| `internal_error` | Anything else. |

**Failing safely.** Input from the peer is untrusted at every step. A malformed, oversized or
out-of-order message ends the session with a fatal error; it never crashes the process, is never
partially applied, and never leaves capture, encoder or a virtual display running.

**Reconnection.** When a connection is lost without `bye`:

- The host enters `Reconnecting` and retries the transport with backoff starting at 250 ms and
  capped at 4 s, for at most 30 seconds in total, then gives up and the session becomes `Failed`.
- A re-established connection runs the complete handshake again. Nothing is resumed; the stream
  restarts with a `CONFIG | KEYFRAME` frame.
- In Extend mode the virtual display stays attached during `Reconnecting` so windows do not jump to
  another monitor during a brief interruption, and is removed when reconnection is abandoned.
- Authentication, version and configuration errors are never retried automatically.

## 13. Future input messages

Channel 2 is reserved for input from the client: touch (pointer ID, action, position, pressure),
then keyboard and mouse. Positions will be normalised to the video frame so they survive resolution
changes. Input is accepted only in an authenticated session and only when the user has enabled it on
the host. Nothing here is specified further yet.

## 14. Control message summary

| `type` | Name | Direction | Purpose |
| --- | --- | --- | --- |
| 1 | `hello` | both | Protocol versions, device ID, device name, app version. |
| 2 | `capabilities` | client → host | Display and decoder capabilities. |
| 3 | `stream_config` | host → client | Chosen stream parameters. |
| 4 | `stream_ready` | client → host | Configuration accepted; decoder prepared. |
| 5 | `request_keyframe` | client → host | Ask for an IDR frame. |
| 6 | `ping` | host → client | Keepalive and round-trip measurement. |
| 7 | `pong` | client → host | Reply to `ping`. |
| 8 | `stats` | client → host | Client-side stream statistics. |
| 9 | `error` | both | Report an error; fatal errors end the session. |
| 10 | `bye` | both | Orderly end of session. |

Authentication messages will be added with the pairing design.

## 15. Open questions

1. Pairing ceremony and the exact key agreement used over Wi-Fi.
2. Whether USB sessions require pairing.
3. Whether Wi-Fi video should move off TCP to avoid head-of-line blocking on lossy networks.
4. Binary encoding for control messages.
5. Clock relationship between host and client for end-to-end latency measurement (the round trip
   from `ping`/`pong` bounds it but does not measure it).
6. Whether to carry dirty-region or cursor metadata alongside video.
