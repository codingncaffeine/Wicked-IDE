#!/usr/bin/env bash
# The window tests (a [Fact] or [Theory] whose body runs the app's window through Ui.Run) carry a tier:
#   [Trait("Tier", "Smoke")]    one end-to-end walk per area: runs on every push (tools/gate.sh, CI)
#   [Trait("Tier", "Window")]   the rest: runs before a release and nightly (tools/gate.sh --full, CI's full run)
# Every other test runs on every push. This lists the window tests that have no tier and fails if there is one, so a new
# window test is placed on purpose, never added to every push by default.
#   tools/test-tiers.sh           check (tools/gate.sh runs it)
#   tools/test-tiers.sh --count   the window tests per tier
set -uo pipefail
cd "$(dirname "$0")/.."

# One line per window test: "<tier or ->  <file>: <method>". A test is the [Fact]/[Theory] line up to the next one; its
# tier is a Tier trait among the attribute lines just above its [Fact]/[Theory].
list() {
    grep -rlE "Ui\.Run\(" tests --include=*.cs | sort | while read -r f; do
        awk -v f="$f" '
            function flush() { if (name != "" && window) print (tier == "" ? "-" : tier) "\t" f ": " name }
            /^\s*\[Trait\("Tier", "[A-Za-z]+"\)\]/ { match($0, /"Tier", "[A-Za-z]+"/); pending = substr($0, RSTART + 9, RLENGTH - 10); next }
            /^\s*\[(Fact|Theory)/ { flush(); name = "?"; window = 0; tier = pending; pending = ""; inattr = 1; next }
            inattr && /(void|Task) [A-Za-z_0-9]+\(/ { match($0, /(void|Task) [A-Za-z_0-9]+\(/); name = substr($0, RSTART, RLENGTH); sub(/^(void|Task) /, "", name); sub(/\($/, "", name); inattr = 0 }
            /^\s*\[/ { next }
            !/^\s*$/ && !inattr { pending = "" }
            /Ui\.Run\(/ { window = 1 }
            END { flush() }' "$f"
    done
}

# --window: the Window-tier tests as "file<TAB>method", for tools/ci-test.sh to share out between processes.
if [ "${1:-}" = --window ]; then
    list | awk -F'\t' '$1 == "Window" { split($2, a, ": "); print a[1] "\t" a[2] }'
    exit 0
fi

if [ "${1:-}" = --count ]; then
    list | cut -f1 | sort | uniq -c
    exit 0
fi

missing=$(list | awk -F'\t' '$1 == "-" { print "  " $2 }')
if [ -n "$missing" ]; then
    echo "window tests with no tier (add [Trait(\"Tier\", \"Window\")], or \"Smoke\" for an area's one end-to-end walk):"
    echo "$missing"
    exit 1
fi
echo "test tiers: $(list | cut -f1 | sort | uniq -c | awk '{printf "%s%s %s", sep, $1, $2; sep=", "}')"
