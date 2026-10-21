#!/usr/bin/env bash
# Rebuilds src/Wicked.App/Themes/Icons.axaml from Lucide's SVGs at the pinned version. Add a name to ICONS and run it
# again to bring in another icon; the SVGs are fetched into scratch/ and never committed.
#   tools/icons/update-icons.sh
set -euo pipefail
cd "$(dirname "$0")/../.."
VERSION=1.60.0
ICONS=(arrow-down arrow-up bot boxes brain check paperclip pencil history image text-cursor-input sliders-horizontal power case-sensitive chevron-down chevron-right chevrons-down-up circle-alert circle-check copy cpu file-code file-plus file-text files folder folder-open folder-plus git-branch message-square minus panel-left panel-right plus refresh-cw regex replace replace-all search whole-word
       settings sparkles square terminal x
       download ellipsis external-link hard-drive key-round pause play trash-2 undo-2
       chevron-up circle-x triangle-alert info lightbulb list-tree bug circle-dot redo arrow-down-to-dot arrow-up-from-dot rotate-ccw
       step-forward list-checks flask-conical bookmark list-filter braces box variable square-function wrench palette puzzle keyboard
       columns-2 plug zap circle-play diff git-commit-horizontal git-merge git-pull-request-arrow archive tag cloud-download cloud-upload list file-diff arrow-down-up eye bell server)
dir=scratch/lucide-$VERSION
mkdir -p "$dir"
for name in "${ICONS[@]}"; do
    [ -s "$dir/$name.svg" ] || curl -sfL "https://cdn.jsdelivr.net/npm/lucide-static@$VERSION/icons/$name.svg" -o "$dir/$name.svg" \
        || { echo "no Lucide icon called $name"; exit 1; }
done
node tools/icons/lucide-to-axaml.mjs "$dir" src/Wicked.App/Themes/Icons.axaml
