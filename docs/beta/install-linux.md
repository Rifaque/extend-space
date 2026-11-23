# Install ExtendSpace on Linux (beta)

ExtendSpace for Linux runs as your own user; installing it needs no administrator rights.

## 1. System packages

ExtendSpace uses your distribution's GStreamer for video, and adb for USB:

| Distribution | Command |
| --- | --- |
| Ubuntu, Debian, Mint | `sudo apt install gstreamer1.0-plugins-base gstreamer1.0-plugins-good gstreamer1.0-plugins-ugly gstreamer1.0-x gstreamer1.0-pipewire adb` |
| Fedora | `sudo dnf install gstreamer1-plugins-good gstreamer1-plugins-ugly-free pipewire-gstreamer android-tools` |
| Arch, CachyOS | `sudo pacman -S gst-plugins-base gst-plugin-pipewire gst-plugin-va gst-plugins-ugly gst-plugins-bad android-tools android-udev` (add `gst-plugins-good` on X11) |

adb is only needed for USB; `android-udev` (Arch) gives your user access to the tablet without root.
On GNOME and KDE the desktop portal is installed already; on Hyprland install
`xdg-desktop-portal-hyprland`.

To build the package yourself on Linux: `sh packaging/linux/build-package.sh` (needs the .NET 10 SDK).

## 2. Install

```sh
tar -xzf ExtendSpace-0.2.0-beta.1-linux-x64.tar.gz
sh ExtendSpace-0.2.0-beta.1-linux-x64/install.sh
```

It installs into `~/.local/opt/extendspace`, adds `~/.local/bin/extendspace` and a menu entry, and
lists any missing package. Installing a newer package the same way replaces the old one.

## 3. What works where

| Your session | Mirror | Extend | Touch | Mouse |
| --- | --- | --- | --- | --- |
| GNOME or KDE Plasma on Wayland | Yes, after you allow screen sharing | Yes: the desktop asks to share a new virtual display, which disappears when you disconnect | Yes (real touch, on the shared display only) | Yes |
| X11 (Cinnamon, Xfce, others) | Yes | Experimental: a display area is added to the right of your screens while connected | No | Yes |
| Hyprland (verified on CachyOS) | Yes (pick the output in the dialog once; remembered) | Yes: ExtendSpace adds a tablet-shaped display `EXTENDSPACE-1` while connected; choose it in the dialog the first time | No (the desktop has no safe way; the option says so) | Yes (the compositor's virtual pointer, on the shared display only) |
| Sway | Yes (pick the output in the dialog) | Run `swaymsg create_output`, then mirror the new output | No | Untested |

## 4. Remove

`sh ~/.local/opt/extendspace/uninstall.sh` removes the program and keeps your pairing in
`~/.local/share/ExtendSpace`; add `--purge` to remove that too. On the tablet, remove the PC under
**Paired PCs**.

## Troubleshooting

* **"No H.264 encoder was found"**: install the x264 plugin (plugins-ugly) from the table above.
* **Screen sharing was not allowed**: connect again and choose Share in the desktop's dialog.
* **The tablet is not listed on Wi-Fi**: add it by address (the tablet shows its address under Pair
  with a PC). Some routers block discovery; the address always works.
* **USB: adb was not found / unauthorised**: install adb and accept the USB debugging prompt on the
  tablet. The distribution's package includes the udev rules for Android devices.
* **Lag**: Diagnostics shows the encoder. NVENC and x264 are preferred because the VA-API encoder adds
  about 67 ms when fed from PipeWire; force one with `EXTENDSPACE_ENCODER=vah264enc extendspace`
  (for example to save battery).
* **"already running"**: only one ExtendSpace runs per user; switch to its window.
* Logs: `~/.local/share/ExtendSpace/host.log`.
