# Wicked terminal integration for zsh: ZDOTDIR points here so zsh reads these files, which read the user's own first.
if [[ -z "$WICKED_USER_ZDOTDIR" ]]; then
    WICKED_USER_ZDOTDIR="$HOME"
fi
if [[ -f "$WICKED_USER_ZDOTDIR/.zshenv" ]]; then
    WICKED_ZDOTDIR="$ZDOTDIR"
    ZDOTDIR="$WICKED_USER_ZDOTDIR"
    . "$WICKED_USER_ZDOTDIR/.zshenv"
    # The user's file may have moved ZDOTDIR itself; their choice is where their other files are.
    WICKED_USER_ZDOTDIR="$ZDOTDIR"
    ZDOTDIR="$WICKED_ZDOTDIR"
fi
