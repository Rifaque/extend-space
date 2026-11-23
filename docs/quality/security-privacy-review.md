# Security and privacy review (Phase 6, 2026-11-22)

A review of the v1 USB Mirror build against the [security model](../security/security-model.md).
Read from the source; no penetration testing was done.

| Area | Finding | Status |
| --- | --- | --- |
| Network listeners | None. The PC connects out to a loopback port that `adb forward` binds on `127.0.0.1` only, for the length of a session, with a system-chosen port (D45), removed at teardown and now also when the window is closed (D70) | OK |
| Android local socket | Abstract socket `extendspace`; every connection's peer credentials are checked and anything but the adb shell (uid 2000) or root is refused before a byte is read (`UsbHostLink.allowedPeerUids`) | OK |
| Untrusted input | Frames are length-checked against per-channel limits before allocation and control messages decoded strictly on both sides (D47); malformed input ends the session | OK (unit-tested; not fuzzed) |
| Another program on the PC reaching the forwarded port | Possible during a session: it could act as the host and send pictures to the device. It cannot read the screen (the PC is the sender). Requires local code execution on the PC | Accepted for v1 (security model, "Open") |
| USB debugging | Required; widens what a trusted PC can do to the device. Stated in the beta privacy notice | Accepted, documented |
| Permissions | Android: none requested (the only manifest permission is androidx's own `DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION`). Windows: runs unelevated; the installer is per-user, no admin, no registry, no services | OK |
| Exported components | `MainActivity` (launcher) only; `SessionActivity` is not exported; debug screens are absent from the release APK (checked by `Build-Apk.ps1` and `dexdump`) | OK |
| Telemetry and network use | None on either side: no HTTP client, no analytics, no crash upload, no update check. Android has no `INTERNET` permission | OK |
| Logging | PC: Windows debug output only (`Trace`), no file; lines carry timings, counters, device names, never serials (the transport's serial-bearing log is not wired) and never pixels. Android: logcat, tag `ExtendSpaceSession`, device names and timings | OK |
| Data at rest | Nothing written: no screenshots, recordings, caches or settings files. `allowBackup=false` on Android | OK |
| Temporary files | `Build-Apk.ps1` uses a per-process temp folder and deletes it; the apps create none | OK |
| Secrets in the repository | None. `.gitignore` excludes `*.jks`, `*.keystore`, `*.pfx`, `*.snk` and now `keystore.properties` | OK |
| Signing | Neither app is signed for distribution (ES-060, ES-061). Testers are told to install only what they got from the developer and to check the SHA-256 | Owner action |
| Dependencies | Windows: .NET 10, Windows App SDK 2.5.1, CsWin32 (build-time). Android: AndroidX and Compose from the version catalog, `org.json` for tests only. No dependency was added in Phases 5–8. Lint reports newer AndroidX versions; no known vulnerability was looked up (no advisory source was queried) | Review advisories before release (ES-064) |
| adb | Not bundled (licence, D73). Found on `PATH`/`ANDROID_HOME`; an attacker who can write to those folders already controls the user's account | Accepted |
