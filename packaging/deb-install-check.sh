#!/usr/bin/env bash
# Runs INSIDE a clean Debian/Ubuntu container (packaging/deb-install-test.sh mounts it with the .deb and smoke-test.sh):
# installs the package with apt, checks every shipped native binary resolves its libraries, then runs it on a virtual
# X screen with smoke-test.sh. Exit 0 = everything passed.
set -uo pipefail
apt-get update -qq >/dev/null
if ! apt-get install -y -q /tmp/wicked-ide.deb xvfb xauth libarchive-tools dbus > /tmp/apt.log 2>&1; then
    tail -25 /tmp/apt.log
    exit 1
fi
. /etc/os-release
echo "installed $(dpkg-query -W -f='${Package} ${Version}' wicked-ide) on $PRETTY_NAME"

problems=0
for f in /usr/bin/wicked /usr/bin/wickedd /usr/bin/wicked-cli; do [ -x "$f" ] || { echo "FAIL $f missing"; problems=1; }; done
[ -f /usr/share/applications/io.github.codingncaffeine.WickedIDE.desktop ] || { echo "FAIL no desktop entry"; problems=1; }
[ -f /usr/share/metainfo/io.github.codingncaffeine.WickedIDE.metainfo.xml ] || { echo "FAIL no metainfo"; problems=1; }
[ -f /usr/share/icons/hicolor/512x512/apps/io.github.codingncaffeine.WickedIDE.png ] || { echo "FAIL no icon"; problems=1; }

# Every shipped native binary must resolve its libraries, symbol versions included, on this distro. Exception: .NET's
# LTTng trace provider, which the runtime loads only when LTTng is installed.
for f in /usr/lib/wicked-ide/wicked /usr/lib/wicked-ide/wickedd /usr/lib/wicked-ide/wicked-cli /usr/lib/wicked-ide/*.so*; do
    case $(basename "$f") in libcoreclrtraceptprovider.so) continue ;; esac
    bad=$(ldd "$f" 2>&1 | grep -E "not found|version .* not found" || true)
    if [ -n "$bad" ]; then
        echo "FAIL $(basename "$f") does not load here:"
        echo "$bad" | sed 's/^/        /'
        problems=1
    fi
done
[ $problems = 1 ] || echo "ok   every shipped library resolves"

# The engines the app downloads need these from the distro (Depends): check they are really there.
for lib in libgomp.so.1 libssl.so.3 libcrypto.so.3; do
    ldconfig -p | grep -q "$lib" && echo "ok   $lib for the engine" || { echo "FAIL $lib (the engine needs it) is not installed"; problems=1; }
done

for bin in wicked wickedd wicked-cli; do
    v=$(/usr/bin/$bin --version 2>&1 | head -1)
    case $v in *0.*) echo "ok   /usr/bin/$bin --version: $v" ;; *) echo "FAIL /usr/bin/$bin --version said: $v"; problems=1 ;; esac
done

xvfb-run -a dbus-run-session -- /tmp/smoke-test.sh /tmp/wicked-ide.deb || problems=1
exit $problems
