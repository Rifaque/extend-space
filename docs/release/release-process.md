# Release process

How a beta build is versioned, built, checked and handed over. Decisions D71–D73 in the
[decision log](../architecture/decision-log.md).

## Versioning (D71)

- One version for both apps, Semantic Versioning with a pre-release label: **0.2.0-beta.1** now.
- Windows: `apps/windows/Directory.Build.props` → `VersionPrefix` (what the host sends in `hello`)
  and `VersionSuffix` (the label). The file version is `0.2.0.0`; the product version shown on the
  Diagnostics page is `0.2.0-beta.1`.
- Android: `apps/android/app/build.gradle.kts` → `versionName` = the full version; `versionCode` =
  `major × 1,000,000 + minor × 10,000 + patch × 100 + n`, where `n` is the beta number (1–98) and 99
  for the final release of that version. 0.2.0-beta.1 is 20001; 0.2.0 final would be 20099; 0.2.1-beta.1
  20101. Always higher than the previous build, so Android accepts it as an upgrade.
- Bump both files together. Next beta: `beta.2` and 20002.

## Building

From the repository root, on the development PC:

```powershell
powershell -ExecutionPolicy Bypass -File packaging\windows\Build-Package.ps1   # Windows ZIP
powershell -ExecutionPolicy Bypass -File packaging\android\Build-Apk.ps1        # Android APK
```

Both write to `artifacts/release/` (git-ignored) with a `.sha256` beside each file, and never
overwrite an earlier build: move the old one away first.

- **Windows (D72):** a clean Release build, then a self-contained publish for `win-x64` (the .NET and
  Windows App SDK runtimes are inside, so testers install neither), with `Install.cmd`,
  `Install-ExtendSpace.ps1`, `Uninstall-ExtendSpace.ps1`, the install guide as `README.md`, the
  licence and `VERSION.txt`. The script copies the app's `ExtendSpace.pri` and `*.xbf`, which an
  unpackaged WinUI publish leaves out (ES-110), only from this run's build, and refuses to package
  without them.
- **Android:** `assembleRelease`; the script refuses an APK containing any `livestream`,
  `decodertest` or `sessiontest` class, and names an unsigned APK `-unsigned`.

## Signing (owner action)

Neither certificate exists; nothing in the repository holds a key.

- **Android (required: an unsigned APK cannot be installed).** Create a release key once and keep it
  safe and backed up (losing it means testers must uninstall to upgrade):
  `keytool -genkeypair -keystore extendspace-release.jks -alias extendspace -keyalg RSA -keysize 4096 -validity 10000`.
  Then create `apps/android/keystore.properties` (git-ignored) with `storeFile=<path to the .jks>`,
  `storePassword=…`, `keyAlias=extendspace`, `keyPassword=…`, and run `Build-Apk.ps1` again. The
  configuration was proven with a throwaway key on 2026-11-22: the APK was signed and `apksigner
  verify` accepted it.
- **Windows (recommended, not required).** Unsigned, the ZIP's files carry the internet zone mark and
  SmartScreen warns about an unknown publisher; the installer unblocks the installed copies. A
  code-signing certificate (an OV/EV certificate, or Azure Trusted Signing) would let
  `signtool sign /fd SHA256 /tr <timestamp URL> /td SHA256 ExtendSpace.exe` be added to the script
  before zipping. MSIX was not chosen because an MSIX cannot be installed at all without a trusted
  signature (D72).

## Checks before handing a build over

1. Both suites and both Release builds green (commands in [PROJECT_STATUS.md](../../PROJECT_STATUS.md)).
2. The package validation: extract the ZIP to a scratch folder; run `Install-ExtendSpace.ps1
   -InstallRoot <scratch>\install -ShortcutFolder <scratch>\links`; start the installed
   `ExtendSpace.exe`, check the Diagnostics page shows the version and adb, close it (exit code 0);
   install again (upgrade); run the installed `Uninstall-ExtendSpace.ps1` with the same parameters.
   This was done for 0.2.0-beta.1 on 2026-11-22 (it found ES-110).
3. On a device: install the signed APK, run the acceptance matrix's core rows
   ([beta acceptance](../quality/beta-acceptance.md)).
4. Publish the files and their SHA-256 values to testers privately, with the `docs/beta/` guides.

## adb (D73)

Not bundled: Google's prebuilt Platform-Tools are distributed under the Android SDK licence terms,
whose redistribution permission was not confirmed. The host finds adb on `PATH` or under
`ANDROID_HOME`, says plainly when it is missing, shows its version on the Diagnostics page, and the
install guide links Google's download. Bundling later needs the owner to confirm the licence (or to
build adb from AOSP source under Apache 2.0).
