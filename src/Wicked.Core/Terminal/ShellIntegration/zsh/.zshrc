# Wicked terminal integration for zsh: the user's .zshrc first, then marks round each prompt, command and result.
WICKED_ZDOTDIR="$ZDOTDIR"
ZDOTDIR="$WICKED_USER_ZDOTDIR"
if [[ -f "$WICKED_USER_ZDOTDIR/.zshrc" ]]; then
    . "$WICKED_USER_ZDOTDIR/.zshrc"
fi

if [[ -o interactive && -z "$WICKED_SHELL_INTEGRATION_LOADED" ]]; then
    WICKED_SHELL_INTEGRATION_LOADED=1
    autoload -Uz add-zsh-hook

    __wicked_escape() {
        local s=${1[1,2000]} out= c i
        for ((i = 1; i <= ${#s}; i++)); do
            c=${s[i]}
            case $c in
                '\') out+='\\' ;;
                ';') out+='\x3b' ;;
                $'\n') out+='\x0a' ;;
                *) out+=$c ;;
            esac
        done
        REPLY=$out
    }

    __wicked_precmd() {
        local status=$?
        if [[ -n "$__wicked_ran" ]]; then
            print -n -- "\e]133;D;${status}\a"
        fi
        __wicked_ran=
        print -n -- "\e]7;file://${HOST}${${PWD//\%/%25}// /%20}\a"
    }

    __wicked_preexec() {
        __wicked_ran=1
        __wicked_escape "$1"
        print -n -- "\e]633;E;${REPLY}\a\e]133;C\a"
    }

    __wicked_wrap() {
        if [[ "$PS1" != *'133;A'* ]]; then
            PS1=$'%{\e]133;A\a%}'"$PS1"$'%{\e]133;B\a%}'
        fi
    }

    add-zsh-hook precmd __wicked_precmd
    add-zsh-hook preexec __wicked_preexec
    # After every other prompt hook, so a theme that rewrites PS1 is wrapped as it stands.
    precmd_functions+=(__wicked_wrap)
fi
ZDOTDIR="$WICKED_USER_ZDOTDIR"
