# Session fault to error mapping

`error-mapping.tsv` is the one table of how a session fault becomes the code the user is told
(architecture section 9.3). Each application has its own mapping, in its own language, and both
test suites read this file:

| Platform | Mapping | Test that reads the table |
| --- | --- | --- |
| Windows | `SessionErrors.From` in `apps/windows/src/ExtendSpace.Core/Sessions/SessionErrors.cs` | `SessionErrorMappingTableTests` in `apps/windows/tests/ExtendSpace.Core.Tests/Sessions/SessionPolicyTests.cs` |
| Android | `SessionErrors.from` in `apps/android/app/src/main/java/com/rsxdevelops/extendspace/connection/SessionErrors.kt` | `SessionErrorMappingTableTest` in `apps/android/app/src/test/java/com/rsxdevelops/extendspace/connection/SessionErrorMappingTableTest.kt` |

## Format

Tab-separated, one mapping per line; lines starting with `#` are comments. Names are written in
lower case, and each suite converts them to its own spelling: `transport_unavailable` is
`SessionErrorCode.TransportUnavailable` on Windows and `ConnectionErrorCode.TRANSPORT_UNAVAILABLE`
on Android.

## Where the platforms differ

- **Categories.** Each platform's fault categories are what that device can observe: the host
  has `capture` and `encoder` and no `decoder`; the client the other way round. A line for a
  category that exists on one side only names that platform in `platforms`.
- **Codes.** Likewise `capture_unavailable` and `encoder_unavailable` exist only on Windows and
  `decoder_unavailable` only on Android.
- **Coverage.** Each suite also checks that every category it has and every user-facing code it
  has (except `authentication_failed`, which nothing raises until pairing exists) appears in a line
  for its platform, so that a new value cannot be added without deciding how it maps.
