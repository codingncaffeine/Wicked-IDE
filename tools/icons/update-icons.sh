#!/usr/bin/env bash
# Rebuilds src/Wicked.App/Themes/Icons.axaml from Lucide's SVGs at the pinned version. Add a name to ICONS and run it
# again to bring in another icon; the SVGs are fetched into scratch/ and never committed.
#   tools/icons/update-icons.sh
set -euo pipefail
cd "$(dirname "$0")/../.."
VERSION=1.60.0
ICONS=(bot boxes circle-alert circle-check copy cpu files git-branch message-square minus panel-left panel-right search
       settings sparkles square terminal x)
dir=scratch/lucide-$VERSION
mkdir -p "$dir"
for name in "${ICONS[@]}"; do
    [ -s "$dir/$name.svg" ] || curl -sfL "https://cdn.jsdelivr.net/npm/lucide-static@$VERSION/icons/$name.svg" -o "$dir/$name.svg" \
        || { echo "no Lucide icon called $name"; exit 1; }
done
node tools/icons/lucide-to-axaml.mjs "$dir" src/Wicked.App/Themes/Icons.axaml
