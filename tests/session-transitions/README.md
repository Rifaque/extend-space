# Session state-machine transition cases

`cases.tsv` is the one table of what the session state machine must do. Both applications have
their own implementation of that machine, in their own language, and both test suites read this
file and run every line of it:

| Platform | State machine | Test that runs the table |
| --- | --- | --- |
| Windows | `apps/windows/src/ExtendSpace.Core/Sessions/SessionStateMachine.cs` | `apps/windows/tests/ExtendSpace.Core.Tests/Sessions/SessionTransitionCasesTests.cs` |
| Android | `apps/android/app/src/main/java/com/rsxdevelops/extendspace/connection/ConnectionStateMachine.kt` | `apps/android/app/src/test/java/com/rsxdevelops/extendspace/connection/SessionTransitionCasesTest.kt` |

Neither suite keeps a copy, so the two implementations cannot come to disagree about a transition
without one of them failing. To change the machine's behaviour, change the table first.

## Format

Tab-separated, one case per line; lines starting with `#` are comments. The columns are described
at the top of the file. Each case applies the `given` events to a new machine, all of which must be
accepted, then applies `when` and checks:

- whether it was accepted;
- the phase afterwards;
- for a rejected event, that the state is exactly what it was;
- `has_streamed` and `attempt_fault`, where the phase has them. `attempt_fault` is the fault of the
  last failed attempt or of the loss that began reconnection; both suites use the same retryable
  fault for `attempt_failed` and `connection_lost`, and check that the one kept is that fault.

## Event names

| In the table | Windows `SessionEvent` | Android `ConnectionEvent` |
| --- | --- | --- |
| `connect` | `ConnectRequested` | `ConnectRequested` |
| `transport_connected` | `TransportConnected` | `TransportConnected` |
| `negotiation_completed` | `NegotiationCompleted` | `NegotiationCompleted` |
| `connection_lost` | `ConnectionLost` with a retryable fault | `ConnectionLost` with a retryable fault |
| `connection_lost_terminal` | `ConnectionLost` with a terminal fault | `ConnectionLost` with a terminal fault |
| `attempt_failed` | `AttemptFailed` with a retryable fault | `AttemptFailed` with a retryable fault |
| `attempt_failed_terminal` | `AttemptFailed` with a terminal fault | `AttemptFailed` with a terminal fault |
| `disconnect` | `DisconnectRequested` | `DisconnectRequested` |
| `teardown_completed` | `TeardownCompleted` | `TeardownCompleted` |
| `failed` | `Failed` | `Failed` |
| `acknowledge` | `ErrorAcknowledged` | `ErrorAcknowledged` |

These are events inside one application. None of them is a message of the wire protocol.

## Where the platforms differ

- **How "has streamed" is held.** Windows has one state record with a `HasStreamed` field. Android
  has a class per state, so the fact follows from the state (`Connecting` has not, `Streaming` and
  `Reconnecting` have) except in `Negotiating`, which carries it as `resuming`. The table's
  `has_streamed` column is checked against whichever applies.
- **`-` in a column.** In `Disconnected`, `Disconnecting` and `Failed` neither value exists. On
  Android the state cannot express them; on Windows the fields exist and the test asserts they are
  false and null.
- **How the cases are reported.** The Windows suite reports each case as its own test. The Android
  suite runs them all in one test and lists every case that failed.
- **Notification.** The Windows machine raises an event per accepted transition, and its test checks
  one is raised for an accepted event and none for a rejected one. The Android machine publishes a
  `StateFlow`, which by design does not re-emit an equal state, so that check has no Android
  counterpart.
