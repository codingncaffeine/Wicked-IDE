#!/usr/bin/env bash
# One command from a green main to a published release: the checks, the artifacts, every smoke test, the tag, the
# GitHub release and the AUR package. It stops at the first failure, and nothing outward (the tag, the release, the
# AUR push) happens before every local check has passed. Releases are the user's call: run it only when asked.
#
#   packaging/release.sh            the version is VersionPrefix in Directory.Build.props;
#                                   packaging/RELEASE-NOTES-<version>.md is the release's text, and the metainfo
#                                   must list the version (build-release.sh refuses otherwise)
#
# Before it: tools/gate.sh, then tools/push.sh; the full CI run of that commit is waited for here. Needs gh (logged in),
# podman or docker, xvfb-run, dbus-run-session, makepkg and an SSH key for aur.archlinux.org. The Windows zip is built
# here too; the full CI run has run the suite on Windows.
set -euo pipefail
cd "$(dirname "$0")/.."
VER=$(grep -oPm1 '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)
TAG="v$VER"
TARBALL="packaging/out/WickedIDE-$VER-linux-x64.tar.gz"
DEB="packaging/out/wicked-ide_${VER}_amd64.deb"
ZIP="packaging/out/WickedIDE-$VER-win-x64.zip"
NOTES="packaging/RELEASE-NOTES-$VER.md"
AUR=scratch/aur-wicked-ide-bin
SMOKE=(xvfb-run -a dbus-run-session --config-file=packaging/isolated-bus.conf -- packaging/smoke-test.sh)
say() { printf '\n── %s\n' "$*"; }

say "checks for $TAG"
[ -z "$(git status --porcelain --untracked-files=no)" ] || { echo "there are uncommitted changes"; exit 1; }
git fetch -q origin
[ "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)" ] || { echo "HEAD is not origin/main: run tools/gate.sh, then tools/push.sh, then this"; exit 1; }
if git rev-parse -q --verify "refs/tags/$TAG" > /dev/null; then echo "the tag $TAG already exists"; exit 1; fi
[ -f "$NOTES" ] || { echo "no $NOTES"; exit 1; }
tools/precommit-scan.sh --all > /dev/null || { echo "precommit-scan found something"; exit 1; }
tools/identity-audit.sh > /dev/null || { echo "identity-audit found something"; exit 1; }
gh auth status > /dev/null 2>&1 || { echo "gh is not logged in"; exit 1; }
echo "ok"

# A push runs every test but the Window tier; a release needs them all, on Linux and Windows. A full run (nightly, or
# started by hand) of this very commit counts; with none, one is started here and waited for.
say "CI for $(git rev-parse --short HEAD): the full suite, Window tier included"
head=$(git rev-parse HEAD)
full_run() {
    gh run list --commit "$head" --json databaseId,event,status,conclusion \
        -q '[.[] | select(.event == "schedule" or .event == "workflow_dispatch")][0] // empty | "\(.databaseId) \(.status) \(.conclusion)"' 2>/dev/null || true
}
if [ -z "$(full_run)" ]; then
    gh workflow run ci.yml --ref main -f tier=full
    for _ in $(seq 1 20); do [ -n "$(full_run)" ] && break; sleep 15; done
fi
for i in $(seq 1 90); do
    read -r run status conclusion <<< "$(full_run)"
    case "$status $conclusion" in
        "completed success") echo "green (run $run)"; break ;;
        "completed "*) echo "the full CI run $run: $conclusion (gh run view $run --log-failed)"; exit 1 ;;
    esac
    [ "$i" = 90 ] && { echo "the full CI run did not finish in 45 minutes"; exit 1; }
    sleep 30
done

say "artifacts"
packaging/build-release.sh
packaging/build-windows.sh

# One at a time: two Xvfb servers started together (or one in a host-network container) can race for a display.
say "smoke: the tarball and the .deb, on a private screen and bus"
"${SMOKE[@]}" "$TARBALL" "$DEB"

say "smoke: the AUR package, built from the local tarball"
rm -rf scratch/aur-build && mkdir -p scratch/aur-build
cp packaging/aur/PKGBUILD "$TARBALL" scratch/aur-build/
(cd scratch/aur-build && sed -i 's|^source=.*|source=("WickedIDE-$pkgver-linux-x64.tar.gz")|; s|^sha256sums=.*|sha256sums=("SKIP")|; s|^pkgver=.*|pkgver='"$VER"'|' PKGBUILD \
    && makepkg -f --nodeps > makepkg.log 2>&1) || { tail -20 scratch/aur-build/makepkg.log; exit 1; }
"${SMOKE[@]}" scratch/aur-build/wicked-ide-bin-"$VER"-1-x86_64.pkg.tar.zst

say "the .deb installed in clean Debian and Ubuntu containers"
packaging/deb-install-test.sh "$DEB"

say "tag $TAG"
git tag -a "$TAG" -m "Wicked IDE $VER"
git push -q origin "$TAG"

say "GitHub release"
gh release create "$TAG" --title "Wicked IDE $VER" --notes-file "$NOTES" "$TARBALL" "$DEB" "$ZIP"
gh release view "$TAG" --json url -q .url

say "AUR: wicked-ide-bin $VER-1"
sha=$(sha256sum "$TARBALL" | cut -d' ' -f1)
sed -i "s|^pkgver=.*|pkgver=$VER|; s|^pkgrel=.*|pkgrel=1|; s|^sha256sums=.*|sha256sums=('$sha')|" packaging/aur/PKGBUILD
[ -d "$AUR/.git" ] || git clone -q ssh://aur@aur.archlinux.org/wicked-ide-bin.git "$AUR"
git -C "$AUR" config user.name codingncaffeine
git -C "$AUR" config user.email codingncaffeine@users.noreply.github.com
cp packaging/aur/PKGBUILD "$AUR/PKGBUILD"
(cd "$AUR" && makepkg --printsrcinfo > .SRCINFO && git add PKGBUILD .SRCINFO && git commit -q -m "Update to $VER-1" && git push -q origin HEAD:master)

say "the AUR package as a user builds it: a fresh clone, the tarball downloaded from the release"
rm -rf scratch/aur-user
git clone -q https://aur.archlinux.org/wicked-ide-bin.git scratch/aur-user
(cd scratch/aur-user && makepkg -f --nodeps > makepkg.log 2>&1 && ls -1 ./*.pkg.tar.zst) || { tail -20 scratch/aur-user/makepkg.log; exit 1; }
"${SMOKE[@]}" scratch/aur-user/wicked-ide-bin-"$VER"-1-x86_64.pkg.tar.zst

say "the checksum, kept in the repository (through the gate branch, like every push)"
git add packaging/aur/PKGBUILD
git commit -q -m "AUR: the $VER tarball's checksum" || true
tools/push.sh

say "released $TAG"
sha256sum "$TARBALL" "$DEB" "$ZIP"
