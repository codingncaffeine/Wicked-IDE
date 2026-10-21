#!/usr/bin/env bash
# Builds the Windows release artifact into packaging/out, from this Linux machine:
#   WickedIDE-<ver>-win-x64.zip   self-contained, unzip anywhere, run wicked.exe (wickedd.exe and wicked-cli.exe beside it)
# .NET and ASP.NET Core ship inside. CI runs the suite on Windows; this only packages. It cleans bin/ and obj/ like
# build-release.sh: never run it while tools/gate.sh runs in this tree.
set -euo pipefail
cd "$(dirname "$0")/.."
[ -f scratch/env.sh ] && source scratch/env.sh
command -v zip > /dev/null || { echo "build-windows: needs zip" >&2; exit 2; }

VER=$(grep -oPm1 '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)
OUT=${WICKED_OUT:-packaging/out}
PUB=$OUT/publish-win
rm -rf "$PUB" && mkdir -p "$PUB"
for p in src/*/; do rm -rf "$p/bin" "$p/obj"; done

echo "── publish v$VER (self-contained win-x64)"
for project in Wicked.App Wicked.Daemon Wicked.Cli; do
    dotnet publish "src/$project/$project.csproj" -c Release -r win-x64 --self-contained true \
        -p:VersionPrefix="$VER" -o "$PUB" -v q --nologo
done

for exe in wicked.exe wickedd.exe wicked-cli.exe; do
    [ -f "$PUB/$exe" ] || { echo "!! $exe is missing from the publish output" >&2; exit 3; }
done
if ! grep -qa CompiledAvaloniaXaml "$PUB/wicked.dll"; then
    echo "!! wicked.dll carries no compiled XAML: refusing to package." >&2
    exit 3
fi

cp LICENSE "$PUB/LICENSE.txt"
cp packaging/README-windows.txt "$PUB/README.txt"
cp THIRD-PARTY-NOTICES.md "$PUB/THIRD-PARTY-NOTICES.md"

echo "── zip"
ZIP="$PWD/$OUT/WickedIDE-$VER-win-x64.zip"
rm -f "$ZIP"
(cd "$PUB" && zip -qr "$ZIP" .)
# Every file the publish made is in the zip: the zip is the whole folder, never a list of names.
expected=$(find "$PUB" -type f | wc -l)
actual=$(unzip -Z1 "$ZIP" | grep -vc '/$')
[ "$expected" -eq "$actual" ] || { echo "!! the zip has $actual files, the publish $expected" >&2; exit 3; }
sha256sum "$ZIP" | sed "s|  .*/|  |" > "$ZIP.sha256"
echo "── $OUT/WickedIDE-$VER-win-x64.zip ($(du -h "$ZIP" | cut -f1)), $actual files"
