#!/usr/bin/env bash
# Builds the Linux release artifacts into packaging/out:
#   WickedIDE-<ver>-linux-x64.tar.gz   self-contained, extract anywhere, run ./wicked (./wickedd and ./wicked-cli beside it)
#   wicked-ide_<ver>_amd64.deb          system install (/usr/lib/wicked-ide; wicked, wickedd, wicked-cli in /usr/bin)
# The version is VersionPrefix in Directory.Build.props. Self-contained: .NET and ASP.NET Core (wickedd's web host) ship
# inside, so nothing .NET is a dependency. Check every artifact with packaging/smoke-test.sh and the .deb with
# packaging/deb-install-test.sh before it is released. It cleans bin/ and obj/: never run it while tools/gate.sh runs.
set -euo pipefail
cd "$(dirname "$0")/.."
[ -f scratch/env.sh ] && source scratch/env.sh

VER=$(grep -oPm1 '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)
OUT=${WICKED_OUT:-packaging/out}
PUB=$OUT/publish
ID=io.github.codingncaffeine.WickedIDE

grep -q "<release version=\"$VER\"" "packaging/$ID.metainfo.xml" || { echo "!! packaging/$ID.metainfo.xml has no <release> for $VER" >&2; exit 3; }
rm -rf "$OUT" && mkdir -p "$PUB"

# Stale obj/ output can publish XAML that was never compiled: every release starts from clean trees.
for p in src/*/; do rm -rf "$p/bin" "$p/obj"; done

echo "── publish v$VER (self-contained linux-x64, ReadyToRun)"
for project in Wicked.App Wicked.Daemon Wicked.Cli; do
    dotnet publish "src/$project/$project.csproj" -c Release -r linux-x64 --self-contained true \
        -p:VersionPrefix="$VER" -o "$PUB" -v q --nologo
done

# The views must be compiled into the app; a publish that fell back to loading XAML at run time is not shipped.
if ! grep -qa CompiledAvaloniaXaml "$PUB/wicked.dll"; then
    echo "!! wicked.dll carries no compiled XAML: refusing to package." >&2
    exit 3
fi

cp LICENSE "$PUB/LICENSE"
cp packaging/README.txt "$PUB/README.txt"
mkdir -p "$PUB/share"
cp "packaging/$ID.desktop" "packaging/$ID.metainfo.xml" "$PUB/share/"
for size in 16 24 32 48 64 128 256 512; do cp "packaging/icons/wicked-$size.png" "$PUB/share/"; done

echo "── tarball"
tar -C "$PUB" -czf "$OUT/WickedIDE-$VER-linux-x64.tar.gz" .

echo "── deb"
DEB=$OUT/debroot
LIB=$DEB/usr/lib/wicked-ide
rm -rf "$DEB"
mkdir -p "$DEB/DEBIAN" "$LIB" "$DEB/usr/bin" "$DEB/usr/share/applications" "$DEB/usr/share/metainfo" "$DEB/usr/share/doc/wicked-ide"
# The whole publish output, never a list of names: a narrowed copy installs cleanly and fails at launch.
cp -a "$PUB/." "$LIB/"
rm -rf "$LIB/README.txt" "$LIB/LICENSE" "$LIB/share"
cp packaging/README.txt "$DEB/usr/share/doc/wicked-ide/README.txt"
cp LICENSE "$DEB/usr/share/doc/wicked-ide/copyright"
for bin in wicked wickedd wicked-cli; do
    printf '#!/bin/sh\nexec /usr/lib/wicked-ide/%s "$@"\n' "$bin" > "$DEB/usr/bin/$bin"
    chmod 755 "$DEB/usr/bin/$bin"
done
for size in 16 24 32 48 64 128 256 512; do
    install -Dm644 "packaging/icons/wicked-$size.png" "$DEB/usr/share/icons/hicolor/${size}x${size}/apps/$ID.png"
done
cp "packaging/$ID.desktop" "$DEB/usr/share/applications/"
cp "packaging/$ID.metainfo.xml" "$DEB/usr/share/metainfo/"
INSTALLED_KB=$(du -sk "$DEB/usr" | cut -f1)
# The engines llama.cpp ships (downloaded at run time) need glibc 2.34, libstdc++ from GCC 12, OpenMP and OpenSSL 3
# (measured on b11765, 2026-10-20); Vulkan builds need the loader.
cat > "$DEB/DEBIAN/control" <<CTRL
Package: wicked-ide
Version: $VER
Section: devel
Priority: optional
Architecture: amd64
Installed-Size: $INSTALLED_KB
Depends: libc6 (>= 2.34), libgcc-s1, libstdc++6 (>= 12), libgomp1, libssl3 | libssl3t64, libicu76 | libicu74 | libicu72 | libicu70, libfontconfig1, libfreetype6, libx11-6, libx11-xcb1, libxcb1, libxi6, libxcursor1, libxext6, libxrandr2, libxrender1, libice6, libsm6, libegl1, libgl1, libwayland-client0, libxkbcommon0, zlib1g
Recommends: libvulkan1
Maintainer: Wicked IDE <codingncaffeine@users.noreply.github.com>
Homepage: https://github.com/codingncaffeine/Wicked-IDE
Description: Code editor that runs AI models on your own machine
 Wicked IDE downloads, runs and keeps up to date the open AI models your
 computer can handle, then uses them to help you write code. It picks two or
 three models for your hardware, installs the right llama.cpp engine for your
 GPU, and keeps everything on your machine.
CTRL
dpkg-deb --build --root-owner-group "$DEB" "$OUT/wicked-ide_${VER}_amd64.deb" > /dev/null
rm -rf "$DEB"

echo "── artifacts:"
ls -sh1 "$OUT" | grep -v publish
