#!/usr/bin/env bash
# Smoke-tests a built Wicked IDE artifact: unpacks it, checks the payload, then RUNS it in a private profile: the daemon
# starts, the CLI finds it and a second daemon refuses; the window opens on a virtual X screen, stays up and quits on
# SIGTERM; nothing in the logs is an error. A packaging slip (a narrowed copy, uncompiled XAML, a missing native library
# or the missing ASP.NET Core the daemon needs) installs cleanly and only shows when the app runs.
#
# Usage: packaging/smoke-test.sh <artifact> [...]     a release .tar.gz, a .deb, or an Arch .pkg.tar.zst
# On a desktop machine run it as:
#   xvfb-run -a dbus-run-session --config-file=packaging/isolated-bus.conf -- packaging/smoke-test.sh <artifact>
# (a private screen, then a private bus that starts no services, so nothing reaches the desktop); it refuses a real display.
# Exit: 0 all passed | 1 a check failed | 2 could not run
set -uo pipefail

MIN_ASSEMBLIES=150
STAY_SECONDS=10

fail() { printf '  FAIL  %s\n' "$*"; FAILED=1; }
ok()   { printf '  ok    %s\n' "$*"; }

# Never the user's screen, Wayland session, network or sound: Xvfb only, offline, nothing audible.
case "${DISPLAY:-}" in :9[0-9]*|:[1-9][0-9][0-9]*) ;; *) [ -n "${XVFB_OK:-}" ] || { echo "smoke-test: run under xvfb-run (DISPLAY=${DISPLAY:-none})"; exit 2; } ;; esac
unset WAYLAND_DISPLAY WAYLAND_SOCKET
export WICKED_WAYLAND=0 WICKED_OFFLINE=1 PULSE_SERVER=unix:/nonexistent/wicked PIPEWIRE_REMOTE=/nonexistent/wicked

smoke_one() {
    local artifact="$1" tmp payload
    FAILED=0
    printf '\n== %s ==\n' "$(basename "$artifact")"
    [[ -f $artifact ]] || { fail "no such file"; return 1; }
    tmp=$(mktemp -d) || return 2
    trap 'rm -rf "$tmp"; trap - RETURN' RETURN

    case $artifact in
        *.tar.gz|*.pkg.tar.zst|*.pkg.tar.xz) bsdtar -xf "$artifact" -C "$tmp" 2>/dev/null || tar -xf "$artifact" -C "$tmp" ;;
        *.deb) dpkg-deb -x "$artifact" "$tmp" ;;
        *) fail "unrecognised artifact type"; return 1 ;;
    esac || { fail "could not unpack"; return 1; }

    payload=$(find "$tmp" -name wicked -type f -perm -u+x -printf '%h\n' | grep -v '/usr/bin$' | head -1)
    [[ -n $payload ]] || { fail "no wicked apphost in the artifact"; return 1; }
    ok "payload at ${payload#"$tmp"}/"

    local n f bin
    n=$(find "$payload" -maxdepth 1 -name '*.dll' | wc -l)
    if (( n < MIN_ASSEMBLIES )); then fail "only $n managed assemblies (expected >= $MIN_ASSEMBLIES)"; else ok "$n managed assemblies"; fi
    for f in wicked.dll Wicked.Core.dll Wicked.Editor.dll Wicked.Contracts.dll wicked.runtimeconfig.json wicked.deps.json \
             wickedd wickedd.dll wickedd.runtimeconfig.json wickedd.deps.json Microsoft.AspNetCore.dll \
             wicked-cli wicked-cli.dll wicked-cli.runtimeconfig.json wicked-cli.deps.json \
             libcoreclr.so libhostfxr.so libSkiaSharp.so libHarfBuzzSharp.so; do
        [[ -f $payload/$f ]] || fail "$f missing"
    done
    (( FAILED )) || ok "core files present"
    grep -qa CompiledAvaloniaXaml "$payload/wicked.dll" && ok "views compiled into wicked.dll" || fail "wicked.dll has no compiled XAML"

    local profile=$tmp/profile version
    mkdir -p "$profile"
    export WICKED_HOME=$profile
    for bin in wicked wickedd wicked-cli; do
        version=$("$payload/$bin" --version 2>&1 | head -1)
        [[ $version == "${bin/wicked/Wicked} "* || $version == "$bin "* ]] && ok "$bin --version: $version" || fail "$bin --version said: $version"
    done

    # The daemon: one per profile, found by the CLI, stopped by SIGTERM.
    local dpid code
    "$payload/wicked-cli" status > /dev/null 2>&1 && fail "the CLI found a daemon before one was started"
    "$payload/wickedd" > "$tmp/daemon.txt" 2>&1 &
    dpid=$!
    for _ in $(seq 1 100); do [[ -S $profile/run/wickedd.sock ]] && break; sleep 0.1; done
    if "$payload/wicked-cli" status > "$tmp/status.txt" 2>&1; then ok "wicked-cli status: $(head -1 "$tmp/status.txt")"; else fail "the CLI did not find the daemon: $(tail -3 "$tmp/daemon.txt" | tr '\n' ' ')"; fi
    "$payload/wickedd" > /dev/null 2>&1
    code=$?
    (( code == 3 )) && ok "a second daemon refused" || fail "a second daemon exited $code, not 3 (already running)"
    kill -TERM "$dpid" 2>/dev/null
    wait "$dpid"
    code=$?
    (( code == 0 )) && ok "the daemon quit on SIGTERM" || fail "the daemon exited $code on SIGTERM"

    # The window: open, stay up, quit on SIGTERM.
    local pid
    "$payload/wicked" > "$tmp/out.txt" 2>&1 &
    pid=$!
    for _ in $(seq 1 120); do
        grep -q "Windowing:" "$tmp/out.txt" 2>/dev/null && break
        kill -0 "$pid" 2>/dev/null || break
        sleep 0.25
    done
    if grep -q "Windowing: X11" "$tmp/out.txt" 2>/dev/null; then ok "window opened ($(grep -m1 -o 'Windowing:.*' "$tmp/out.txt"))"; else fail "no window on Xvfb: $(tail -5 "$tmp/out.txt" | tr '\n' ' ')"; fi
    sleep "$STAY_SECONDS"
    if kill -0 "$pid" 2>/dev/null; then
        ok "still running after ${STAY_SECONDS} s"
        grep -m1 -o "Startup:.*" "$tmp/out.txt" | sed 's/^/        /'
        kill -TERM "$pid"
        for _ in $(seq 1 50); do kill -0 "$pid" 2>/dev/null || break; sleep 0.1; done
        if kill -0 "$pid" 2>/dev/null; then fail "still running 5 s after SIGTERM"; kill -KILL "$pid"; else wait "$pid"; code=$?; ok "quit on SIGTERM (exit $code)"; fi
    else
        wait "$pid"; code=$?
        fail "exited by itself (code $code): $(tail -5 "$tmp/out.txt" | tr '\n' ' ')"
    fi
    if cat "$profile"/state/logs/*.log 2>/dev/null | grep -E " (ERR|FTL) " > "$tmp/errors.txt"; then fail "errors in the logs:"; sed 's/^/        /' "$tmp/errors.txt" | head -10; else ok "no errors in the logs"; fi
    unset WICKED_HOME
    return $FAILED
}

[[ $# -ge 1 ]] || { sed -n '2,11p' "$0"; exit 2; }
rc=0
for a in "$@"; do smoke_one "$(realpath "$a")" || rc=1; done
printf '\n'; if (( rc == 0 )); then echo "SMOKE TEST PASSED"; else echo "SMOKE TEST FAILED"; fi
exit $rc
