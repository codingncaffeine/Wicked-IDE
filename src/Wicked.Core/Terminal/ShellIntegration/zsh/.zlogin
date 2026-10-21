if [[ -f "$WICKED_USER_ZDOTDIR/.zlogin" ]]; then
    ZDOTDIR="$WICKED_USER_ZDOTDIR"
    . "$WICKED_USER_ZDOTDIR/.zlogin"
fi
