#!/usr/bin/env bash
# The built binaries, run the way a user runs them, in a throwaway profile and nowhere near the desktop:
#   - the daemon starts, the CLI finds it, a second daemon for the same profile refuses, SIGTERM stops it and its socket goes;
#   - the window opens under Xvfb on an isolated D-Bus (the Wayland variables are removed, so it cannot reach the real
#     desktop), stays up, and closes on SIGTERM.
#   tools/smoke.sh            (after a Release build)
set -uo pipefail
cd "$(dirname "$0")/.."
root="$PWD"
# The runtime the binaries were built against (this machine builds with an SDK mirror; see scratch/env.sh).
[ -f "$root/scratch/env.sh" ] && source "$root/scratch/env.sh"
bin="$root/src"
daemon="$bin/Wicked.Daemon/bin/Release/net10.0/wickedd"
cli="$bin/Wicked.Cli/bin/Release/net10.0/wicked-cli"
app="$bin/Wicked.App/bin/Release/net10.0/wicked"
for f in "$daemon" "$cli" "$app"; do [ -x "$f" ] || { echo "smoke: $f is missing; build Release first"; exit 2; }; done

home="$root/scratch/smoke/home"
rm -rf "$root/scratch/smoke"
mkdir -p "$home"
export WICKED_HOME="$home"
unset WAYLAND_DISPLAY WAYLAND_SOCKET
export WICKED_WAYLAND=0
fail() { echo "SMOKE FAILED: $*"; [ -n "${dpid:-}" ] && kill -TERM "$dpid" 2>/dev/null; [ -n "${apid:-}" ] && kill -TERM "$apid" 2>/dev/null; exit 1; }

"$cli" status > /dev/null && fail "the CLI found a daemon before one was started"

"$daemon" > "$root/scratch/smoke/daemon.out" 2>&1 &
dpid=$!
for _ in $(seq 1 100); do [ -S "$home/run/wickedd.sock" ] && break; sleep 0.1; done
[ -S "$home/run/wickedd.sock" ] || fail "the daemon made no socket"
"$cli" status || fail "the CLI did not find the daemon"
"$daemon" > /dev/null 2>&1
second=$?
[ "$second" -eq 3 ] || fail "a second daemon exited $second, not 3 (already running)"
echo "smoke: a second daemon refused"

xvfb-run -a dbus-run-session --config-file="$root/packaging/isolated-bus.conf" "$app" > "$root/scratch/smoke/app.out" 2>&1 &
wrapper=$!
apid=""
for _ in $(seq 1 100); do
    apid=$(pgrep -P "$(pgrep -P "$wrapper" -d, || echo 0)" -x wicked 2>/dev/null || true)
    [ -z "$apid" ] && apid=$(pgrep -x -n wicked || true)
    grep -q "Windowing:" "$root/scratch/smoke/app.out" 2>/dev/null && break
    sleep 0.1
done
grep -q "Windowing: X11" "$root/scratch/smoke/app.out" || fail "the window did not open on Xvfb: $(grep Windowing "$root/scratch/smoke/app.out")"
sleep 3
[ -n "$apid" ] && kill -0 "$apid" 2>/dev/null || fail "the window did not stay up"
kill -TERM "$apid"
for _ in $(seq 1 50); do kill -0 "$apid" 2>/dev/null || break; sleep 0.1; done
kill -0 "$apid" 2>/dev/null && fail "the window ignored SIGTERM"
wait "$wrapper" 2>/dev/null
echo "smoke: the window opened on Xvfb and closed on SIGTERM"

kill -TERM "$dpid"
wait "$dpid"
code=$?
dpid=""
[ "$code" -eq 0 ] || fail "the daemon exited $code on SIGTERM"
[ -e "$home/run/wickedd.sock" ] && fail "the daemon left its socket behind"
echo "SMOKE PASSED"
grep -E "WRN|ERR" "$root/scratch/smoke/app.out" "$root/scratch/smoke/daemon.out" | sed 's/^/  log: /'
exit 0
