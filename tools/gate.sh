#!/usr/bin/env bash
# The gate before a push: the whole suite the way CI runs it, N times (default 2).
# Run it in the background and keep working; push once it says GATE PASSED.
#   - on two CPUs (the CI runners have two to four; this machine's thirty-two hide the interleavings they see),
#   - offline and with no sound server, as CI is,
#   - warnings as errors.
# Every run must pass. A failed run prints its failed tests; the logs are in scratch/gate/. This machine is Linux:
# what only Windows sees (paths, timing) is caught by tools/push.sh, which runs CI on both before main moves.
# Every push runs every test but the Window tier (tools/test-tiers.sh); --full adds it, as a release needs.
#   tools/gate.sh [--full] [N]
set -uo pipefail
cd "$(dirname "$0")/.."
tier=(--filter "Tier!=Window")
if [ "${1:-}" = --full ]; then tier=(); shift; fi
N="${1:-2}"
export PULSE_SERVER=unix:/nonexistent/wicked PIPEWIRE_REMOTE=/nonexistent/wicked WICKED_OFFLINE=1
command -v taskset > /dev/null || { echo "gate: needs taskset"; exit 2; }
# One gate at a time on this machine, from any worktree: two on the same two CPUs each take twice as long.
exec 9> "${XDG_RUNTIME_DIR:-/tmp}/wicked-gate.lock"
flock -n 9 || { echo "gate: another gate is running"; exit 2; }
# Children never get the lock (9>&-): the compiler server dotnet leaves running would hold it for every later gate.
mkdir -p scratch/gate

echo "── paths that would break the Windows job"
tools/windows-paths.sh || { echo "GATE FAILED: Windows paths"; exit 1; }

echo "── window tests placed in a tier"
tools/test-tiers.sh || { echo "GATE FAILED: test tiers"; exit 1; }

echo "── build, warnings as errors"
if ! dotnet build Wicked.slnx -c Release -warnaserror --nologo -v q > scratch/gate/build.log 2>&1 9>&-; then
    grep -E "error" scratch/gate/build.log | sort -u | head -20
    echo "GATE FAILED: the build"
    exit 1
fi

rc=0
for i in $(seq 1 "$N"); do
    log="scratch/gate/run-$i.log"
    taskset -c 0,1 dotnet test tests/Wicked.Tests -c Release --no-build "${tier[@]}" > "$log" 2>&1 9>&-
    line=$(grep -E "^(Passed|Failed)!" "$log" | tail -1 | cut -c1-100)
    echo "run $i of $N: ${line:-no result, see $log}"
    if ! grep -qE "^Passed!" "$log"; then
        rc=1
        grep -E "^\s+Failed " "$log" | sed 's/^/    /'
    fi
done

if (( rc == 0 )); then echo "GATE PASSED: $N runs on 2 CPUs, CI-style${tier[*]:+ (push tier)}"; else echo "GATE FAILED"; fi
exit $rc
