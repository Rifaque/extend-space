# ExtendSpace beta: installing on Windows

ExtendSpace shows your Windows screen on an Android tablet or phone over a USB cable. This beta
mirrors one display; it does not extend the desktop, use Wi-Fi, or send touch back to the PC.

## What you need

- Windows 11 (64-bit, x64). Windows 10 and ARM PCs have not been tried.
- An Android device running Android 10 or later, and a USB cable that carries data.
- **Android SDK Platform-Tools** (this provides `adb`, the program ExtendSpace uses to reach the
  device over USB). ExtendSpace does not include it.

## 1. Install Platform-Tools (once)

1. Download "SDK Platform-Tools for Windows" from Google:
   <https://developer.android.com/tools/releases/platform-tools>.
2. Extract it, for example to `C:\platform-tools`.
3. Tell Windows where it is, either way:
   - add the folder to your `PATH` (Settings → System → About → Advanced system settings →
     Environment Variables → Path → Edit → New → `C:\platform-tools`), **or**
   - if you already have the Android SDK, set `ANDROID_HOME` to the SDK folder.
4. Sign out and back in (or restart) so ExtendSpace sees the change.

ExtendSpace's **Diagnostics** page shows the adb it found and its version.

## 2. Install ExtendSpace

1. Download `ExtendSpace-<version>-win-x64.zip` and, if you were given one, its `.sha256` file. To
   check the download: in PowerShell, `Get-FileHash .\ExtendSpace-<version>-win-x64.zip` and compare
   with the file.
2. Right-click the ZIP → **Extract All**.
3. In the extracted folder, double-click **Install.cmd**.
   - Windows may warn that the file is from the internet or from an unknown publisher: the beta is
     not signed yet. Choose **More info → Run anyway** only if you got the ZIP from the developer.
4. Start **ExtendSpace** from the Start menu.

It installs for your user only, in `%LOCALAPPDATA%\Programs\ExtendSpace`. It needs no administrator
rights, changes no system settings, and opens no network ports.

**Upgrading:** close ExtendSpace and run the new version's Install.cmd. **Removing:** Start menu →
ExtendSpace → **Uninstall ExtendSpace**.

## 3. Prepare the Android device (once)

1. Install the ExtendSpace app (see the Android guide).
2. Turn on **Developer options**: Settings → About → tap **Build number** seven times (on some
   devices it is under "Software information").
3. In Developer options, turn on **USB debugging**.
4. Connect the device to the PC. When the device asks **Allow USB debugging?**, tick **Always allow
   from this computer** and tap **Allow**.

## 4. Connect

1. On the device, open ExtendSpace and tap **Wait for PC**.
2. On the PC, in ExtendSpace's **Home** page, check that your device is listed as **Ready** and
   selected, choose the display to mirror, and click **Connect**.
3. The PC's screen appears on the device within a second or two.

To stop: click **Disconnect** on the PC, or press Back on the device. Closing ExtendSpace on the PC
also ends the session cleanly.

If something goes wrong, see [troubleshooting](troubleshooting.md).
