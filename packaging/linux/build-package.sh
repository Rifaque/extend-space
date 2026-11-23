#!/bin/sh
# Builds the ExtendSpace Linux package on Linux (D102): a self-contained linux-x64 tarball with the
# per-user installer, and its SHA-256, in artifacts/release. The same package as
# Build-LinuxPackage.ps1 (Windows), without PowerShell. Never overwrites an earlier copy.
# Needs: the .NET 10 SDK, tar, gzip, sha256sum.
set -eu
HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO=$(CDPATH= cd -- "$HERE/../.." && pwd)
OUT=${1:-$REPO/artifacts/release}
PROPS=$REPO/apps/windows/Directory.Build.props
prefix=$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$PROPS")
suffix=$(sed -n 's:.*<VersionSuffix>\(.*\)</VersionSuffix>.*:\1:p' "$PROPS")
version=$prefix${suffix:+-$suffix}
name=ExtendSpace-$version-linux-x64
archive=$OUT/$name.tar.gz
if [ -e "$archive" ]; then
    echo "$archive exists; remove it or bump the version." >&2
    exit 1
fi

staging=$(mktemp -d)
trap 'rm -rf "$staging"' EXIT
root=$staging/$name
mkdir -p "$root/app"
dotnet publish "$REPO/apps/linux/src/ExtendSpace.Host.Linux" -c Release -r linux-x64 --self-contained -o "$root/app" -nologo -v q
for file in install.sh uninstall.sh extendspace.desktop extendspace.svg; do
    # LF line endings whatever the checkout uses: the scripts run under /bin/sh.
    tr -d '\r' < "$HERE/$file" > "$root/$file"
done
chmod 755 "$root/install.sh" "$root/uninstall.sh"
printf '%s\n' "$version" > "$root/VERSION"
mkdir -p "$OUT"
tar -czf "$archive" -C "$staging" "$name"
hash=$(sha256sum "$archive" | cut -d' ' -f1)
printf '%s  %s\n' "$hash" "$name.tar.gz" > "$archive.sha256"
echo "$archive"
echo "SHA-256 $hash"
