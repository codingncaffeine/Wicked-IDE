#!/usr/bin/env bash
# The unit tests as CI runs them. A test that fails is run once more on its own; when it passes then, the job passes and
# the test is named as flaky (a warning on the run and a line in its summary), never hidden. A run that did not finish
# (a crash or a hang) and a run with more than five failures are real breakage and are not run again.
#   tools/ci-test.sh [RESULTS_DIR] [push|full]   push (the default): every test but the Window tier (tools/test-tiers.sh)
#
# full: the Window tier is shared out between SHARDS test processes (default: the CPUs, at most 6) that run beside the
# rest. A window test runs on its process's one UI thread, so in one process the 200-odd of them queue for 13–16 minutes;
# side by side they take a few. A shard is whole files, so the tests of a class stay together.
set -uo pipefail
cd "$(dirname "$0")/.."
results=${1:-TestResults}
mode=${2:-push}
mkdir -p "$results"
args=(tests/Wicked.Tests -c Release --no-build --blame-hang --blame-hang-timeout 2m --logger "console;verbosity=normal")

if [ "$mode" != full ]; then
    dotnet test "${args[@]}" --results-directory "$results" --filter "Tier!=Window" --logger "trx;LogFileName=first.trx" 2>&1 | tee "$results/first.log"
    first=${PIPESTATUS[0]}
    trxs=("$results/first.trx")
    logs=("$results/first.log")
else
    cpus=$(nproc 2>/dev/null || echo 4)
    shards=${SHARDS:-$(( cpus < 6 ? cpus : 6 ))}
    [ "$shards" -ge 1 ] || shards=1
    windows=$(tools/test-tiers.sh --window)
    # The files, most window tests first, each to the shard with the fewest so far.
    declare -a load filters
    for ((i = 0; i < shards; i++)); do load[i]=0; filters[i]=""; done
    while read -r count file; do
        best=0
        for ((i = 1; i < shards; i++)); do [ "${load[i]}" -lt "${load[best]}" ] && best=$i; done
        load[best]=$(( load[best] + count ))
        while IFS=$'\t' read -r f method; do
            [ "$f" = "$file" ] && filters[best]+="FullyQualifiedName~.$method|"
        done <<< "$windows"
    done < <(cut -f1 <<< "$windows" | sort | uniq -c | sort -rn)
    echo "── the fast tests and the Smoke walks, beside $shards window shards (${load[*]} tests)"
    pids=()
    dotnet test "${args[@]}" --results-directory "$results/fast" --filter "Tier!=Window" --logger "trx;LogFileName=first.trx" > "$results/fast.log" 2>&1 &
    pids+=($!)
    for ((i = 0; i < shards; i++)); do
        [ -n "${filters[i]}" ] || continue
        dotnet test "${args[@]}" --results-directory "$results/shard$i" --filter "Tier=Window&(${filters[i]%|})" --logger "trx;LogFileName=first.trx" > "$results/shard$i.log" 2>&1 &
        pids+=($!)
    done
    first=0
    for p in "${pids[@]}"; do wait "$p" || first=1; done
    trxs=("$results"/fast/first.trx "$results"/shard*/first.trx)
    logs=("$results"/fast.log "$results"/shard*.log)
    for t in "${trxs[@]}"; do
        [ -f "$t" ] || { echo "$(basename "$(dirname "$t")"): no results (it did not finish)"; continue; }
        count() { grep -o "<UnitTestResult [^>]*outcome=\"$1\"" "$t" | wc -l; }
        echo "$(basename "$(dirname "$t")"): $(count Passed) passed, $(count Failed) failed, $(count NotExecuted) skipped"
    done
fi

[ "$first" -eq 0 ] && exit 0
if cat "${logs[@]}" | grep -qiE 'test run (was )?aborted|testhost process (crashed|exited)|inactivity time'; then
    echo "The test run did not finish: not run again."
    # The hang detector's sequence file ends with the test that was running when the host hung or died.
    for s in $(find "$results" -name 'Sequence_*.xml' 2>/dev/null); do
        echo "  running when it stopped: $(grep -o 'Name="[^"]*"' "$s" | tail -1 | cut -d'"' -f2)"
    done
    cat "${logs[@]}" | grep -E '^\s+Failed |Error Message' -A3 | head -40
    exit 1
fi
# The failed tests by method (a theory's cases run again together); names in TRX are XML-escaped, cut before any '('.
present=()
for t in "${trxs[@]}"; do [ -f "$t" ] && present+=("$t"); done
[ "${#present[@]}" -gt 0 ] || { echo "No test results were written: not run again."; exit 1; }
mapfile -t failed < <(cat "${present[@]}" | grep -o '<UnitTestResult [^>]*outcome="Failed"[^>]*>' | sed -n 's/.* testName="\([^"(]*\).*/\1/p' | sort -u)
if [ "${#failed[@]}" -eq 0 ] || [ "${#failed[@]}" -gt 5 ]; then
    echo "${#failed[@]} failed tests: not run again."
    printf '  %s\n' "${failed[@]}"
    exit 1
fi
filter=""
for name in "${failed[@]}"; do filter+="FullyQualifiedName=$name|"; done
echo "── running again on their own: ${failed[*]}"
dotnet test "${args[@]}" --results-directory "$results" --filter "${filter%|}" --logger "trx;LogFileName=again.trx" 2>&1 | tee "$results/again.log"
again=${PIPESTATUS[0]}
[ "$again" -eq 0 ] || exit "$again"
for name in "${failed[@]}"; do
    echo "::warning title=Flaky test::$name failed, then passed when run again on its own"
    [ -n "${GITHUB_STEP_SUMMARY:-}" ] && echo "- Flaky: \`$name\` failed, then passed when run again on its own" >> "$GITHUB_STEP_SUMMARY"
done
exit 0
