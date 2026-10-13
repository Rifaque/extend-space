# ExtendSpace protocol — draft 0

Status: **draft, partly implemented.** The USB binding, the framing, the handshake, video delivery,
keyframe requests, keepalive, statistics and reconnection are implemented by the
[first streaming experiment](../../docs/architecture/streaming-experiment-01.md#step-3--live-stream-over-adb-forward)
— a Windows console program and a debug-only Android screen — and have carried live video between a
PC and one tablet. Neither application uses any of it yet. Pairing, authentication, Wi-Fi and input
are still design only. [Section 16](#16-implementation-status) lists exactly what exists and where
the implementation added to, or stopped short of, this text. Sections marked *Open* are undecided on
purpose.

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

A receiver skips frames on a channel it does not know; the length prefix makes that safe. It skips
at most 8 MiB per frame: a longer frame on an unknown channel is a fatal `message_too_large` error,
so that a corrupt length cannot make a receiver read and discard without end.

## 3. Transport bindings

Requirements on any binding:

- Reliable, ordered delivery of bytes for the control channel.
- The listening side must not be reachable by an unauthenticated peer on a network.
- Both ends must be able to detect a dead connection within the keepalive timeout.

| Binding | How it connects | Authentication |
| --- | --- | --- |
| **USB (ADB)** | The host asks `adb` to forward a host loopback TCP port to a local (non-network) socket owned by the Android app, then connects to that port. Implemented as `adb forward tcp:27183 localabstract:extendspace`: the app listens on the abstract local socket `extendspace`, the host connects to `127.0.0.1:27183`, and the host removes the forward when it stops. The port is a default, not part of the protocol. | The cable and the device's ADB authorisation gate access. The Android app additionally accepts only connections that come from the ADB daemon: it reads the peer's credentials and closes the connection unless the peer's user ID is the shell user (2000) or root (0). Loopback and local sockets only; nothing listens on a network interface, and the Android app needs no network permission. |
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
| 0 | 4 | `sequence` | Unsigned; increments by one per frame the encoder produces while a connection is up, starting at 0 on each connection and wrapping at 2^32. A gap tells the client that frames were dropped upstream: the host numbers a frame before deciding whether it can be sent. |
| 4 | 8 | `timestamp_us` | Signed. Capture time in microseconds on the host's monotonic clock. The clock's zero is arbitrary; only differences mean anything, and [`ping`](#11-keepalive-and-connection-health) is what relates it to the client's clock. |
| 12 | rest | data | Exactly one H.264 access unit in Annex B byte-stream format. |

"Capture time" as implemented is the moment the capture API handed the frame to the host process.
Windows also stamps each frame itself, and on the one machine measured that stamp ran a median of
1.6 to 3.4 ms *ahead* of the hand-over, so the two are not interchangeable; the hand-over is used
because it is an event the host observes.

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
3. Transport-specific authentication completes. (Nothing to do over USB today: there are no
   authentication messages yet, and the binding's own gate is described in section 3.)
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

*Implemented so far:* the client sends the display's size and refresh rate and, per decoder, its
name, whether it is hardware-backed, whether it reports a low-latency feature and its maximum width
and height. Density, profile and level limits and frame-rate limits are not sent. The host does not
choose anything from this yet: the person running it picks the size, rate and bitrate, and the host
only checks that the size fits within some listed decoder's maximum, sending
`no_common_configuration` if it does not. `stream_ready` names the decoder the client will use.

The host may send `stream_config` again during `Streaming`, for example when the mirrored display
changes resolution. The client replies `stream_ready`; the new configuration takes effect at the
next video frame flagged `CONFIG`. *Not implemented:* the experiment sends `stream_config` once per
connection, and a change of the source's size ends its run.

## 9. Video delivery and backpressure

- Frames are sent in order, one access unit each. The encoder does not reorder frames (no B-frames).
- The host keeps a **bounded** send queue of at most a few frames. It never lets latency grow by
  buffering.
- When the queue is full the host prefers to **skip capturing or encoding** a frame, which is
  harmless. If an already-encoded frame must be discarded, every later frame depends on it, so the
  host discards up to the next keyframe and forces the encoder to produce one.
- The client decodes and presents frames as they arrive. It does not buffer for smoothness.
- A `sequence` gap or a decoder error makes the client request a keyframe.
- The client's backlog is bounded too. If more than a few access units are waiting for its decoder,
  it discards what it has not yet submitted and requests a keyframe (`decoder_backlog`), instead of
  showing an ever older picture. After a discard, a gap or an error it ignores everything up to the
  next `CONFIG | KEYFRAME` frame.

*As implemented:* the host's queue holds 4 frames. The client allows 4 access units waiting for the
decoder, and 30 until the decoder has returned its first picture, because a hardware decoder takes
on the order of 100 ms to start and discarding the opening keyframe would only delay the picture
further.

## 10. Keyframe requests

`request_keyframe` (client → host) carries a reason: `startup`, `decode_error`, `sequence_gap`,
`surface_recreated` or `decoder_backlog`. The reason is for logs; the host treats them alike and
accepts reasons it does not know. The host responds by producing a `CONFIG | KEYFRAME` frame as soon
as it can. The host rate-limits these (no more than about two per second; implemented as at most one
per 500 ms) and may coalesce several requests into one keyframe. The host can also emit keyframes
unprompted, and does so at the start of every connection and at its regular keyframe interval.

## 11. Keepalive and connection health

- The host sends `ping` once per second carrying an ID and its send time. The client answers `pong`
  with the same values. This gives the host a round-trip time.
- `pong` also carries the client's own monotonic clock at the moment it answered. From the fastest
  round trip seen so far the host estimates the difference between the two clocks (client clock
  minus host clock, taking the reply to have been written halfway through the round trip) and sends
  that estimate, with the round trip it came from, in its next `ping`. The estimate is good to plus
  or minus half that round trip, and it is what lets the client turn a frame's `timestamp_us` into a
  time on its own clock. The host sends its first five pings 100 ms apart so that an estimate exists
  within the first half second.
- If either side receives nothing at all on any channel for 5 seconds it treats the connection as
  lost.
- The client sends `stats` once per second: frames received, decoded, presented and dropped, decode
  time, and its receive-queue depth. These feed diagnostics and, later, bitrate adaptation. They are
  never required for correctness. The counters are totals for the connection, so a lost report costs
  nothing; the latency figures cover only the time since the previous report. The client sends one
  last `stats` before a `bye` of its own. A host that wants complete totals stops sending video and
  waits for a report that accounts for it before sending its own `bye`.

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

**Fields, as implemented.** Every payload is one JSON object. Fields marked optional may be absent;
a receiver ignores fields it does not know. Times ending `_us` are whole microseconds on the
sender's monotonic clock unless stated otherwise; figures ending `_ms` are milliseconds and may have
fractions.

| Message | Fields |
| --- | --- |
| `hello` | `protocol_min`, `protocol_max` (major versions, integers); optional `role` (`"host"` or `"client"`), `device_name`, `app_version`. No device ID yet: that arrives with pairing. |
| `capabilities` | `display`: `width`, `height`, optional `refresh_hz`. `decoders`: an array of `codec` (`"h264"`; other codecs are ignored), `name`, `hardware`, optional `low_latency`, `max_width`, `max_height`. |
| `stream_config` | `codec`, `profile` (`"baseline"`, `"main"` or `"high"`), `width`, `height`, `frame_rate`, `bitrate` (bit/s), `max_video_payload` (bytes); optional `encoder` and `encoder_acceleration` (`"hardware"`, `"software"` or `"unknown"`), for the client's diagnostics only. |
| `stream_ready` | Optional `decoder` (its name) and `hardware`. |
| `request_keyframe` | Optional `reason`. |
| `ping` | `id`, `host_time_us`; optional `clock_offset_us` (client clock minus host clock) and `round_trip_us`, both absent until the first `pong` has come back. |
| `pong` | `id` and `host_time_us` copied from the `ping`; `client_time_us`. |
| `stats` | `frames_received`, `bytes_received`, `frames_decoded`, `frames_rendered`; optional `frames_missing` (sequence gaps), `frames_discarded` (received but not given to the decoder), `frames_submitted`, `frames_displayed`, `decoder_errors`, `queue_depth` (access units waiting for the decoder), and `receive_latency_ms`, `render_latency_ms`, `display_latency_ms`, each an object of `count`, `min`, `mean`, `p95`, `max`. A latency object is absent when there is nothing to report, including before a clock estimate exists. |
| `error` | `code`; optional `message`, `fatal`. |
| `bye` | Optional `reason`, a sentence for logs. |

`render_latency_ms` is the time from a frame's `timestamp_us` to the client releasing the decoded
frame to its display surface. `receive_latency_ms` is the time to the frame having been read from
the socket. `display_latency_ms` is reserved for the time to the frame actually being shown; the
Android client never sends it, because the platform callback it would come from reported the
timestamp the frame was queued with rather than a display time on the device tested.

## 15. Open questions

1. Pairing ceremony and the exact key agreement used over Wi-Fi.
2. Whether USB sessions require pairing.
3. Whether Wi-Fi video should move off TCP to avoid head-of-line blocking on lossy networks.
4. Binary encoding for control messages.
5. Clock relationship between host and client for end-to-end latency measurement. *Partly settled:*
   `ping`/`pong` now carries a clock estimate good to half a round trip (about 1 to 2 ms over ADB on
   the machines tried), which is enough to time a frame as far as the decoder's output. It cannot
   see the display on either device; that still needs a camera.
6. Whether to carry dirty-region or cursor metadata alongside video.
7. Whether `stats` latency should be per report, as now, or a running histogram, and whether the
   host should send its own statistics to the client.
8. A frame that is not sent still uses a sequence number, so the client sees every host-side drop
   as a gap and asks for a keyframe the host was already going to produce. Harmless with the rate
   limit, but one of the two is redundant.

## 16. Implementation status

What exists is in the first streaming experiment only:
`apps/windows/experiments/ExtendSpace.Experiments.CaptureEncode/Streaming/` (host) and
`apps/android/app/src/debug/java/com/rsxdevelops/extendspace/livestream/` (client, debug builds
only). Both have unit tests, and three byte-exact example messages (a `hello`, a `ping` and a video
frame) are decoded by both test suites, so the two implementations cannot drift apart on framing
without a test failing.

| Section | State |
| --- | --- |
| 2 Channels | Control and video implemented. Channels with an ID above 2 are skipped. A frame on the input channel is checked against its size limit and then refused as `unexpected_message`, which ends the connection: nothing sends input yet. |
| 3 Transport bindings | USB (ADB) implemented and exercised on one PC and one tablet. Wi-Fi not started. |
| 4 Framing | Implemented on both sides, including every limit, checked before the payload is read. |
| 5 Versioning | `hello` carries the range and a host or client with no common major version refuses. Only version 0 exists, so no real negotiation has happened. |
| 6 Identity and pairing | Not implemented. `hello` carries no device ID. |
| 7 Session lifecycle | Implemented in the experiment's own host session and client session, with the states of this section. The applications' session state machines are not connected to it. The handshake timeout closes the connection; it does not send a `timeout` error first. |
| 8 Capabilities and configuration | A minimal subset; see the note in that section. No reconfiguration during a stream. |
| 9 Video delivery and backpressure | Implemented. The host's skip-before-encode path is used only while no client is streaming; when the send queue is full it drops encoded frames up to the next keyframe and forces one, which is the second-choice behaviour in this text. In every measured run the queue never held more than one frame, so that path has unit tests but has not run against a real device. |
| 10 Keyframe requests | Implemented, including the rate limit. |
| 11 Keepalive | Implemented, with the clock estimate described there. |
| 12 Errors and reconnection | Host reconnection with backoff implemented and exercised. Malformed input ends the connection on both sides without a crash (unit-tested on both; not attacked on a device). The Extend-mode clause does not apply yet. |
| 13 Input | Not implemented. |

Where the implementation differs from the text above, the text has been changed to say so in place.
The one difference in *who does what* is outside this document: the plan for the experiment once
said the Windows side would listen. It does not. Section 3 already had the Android app listening on
a local socket and the host connecting through the forward, and that is what was built, because it
keeps every listening socket off the network on both devices.
