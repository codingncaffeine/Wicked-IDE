# Wicked terminal integration for bash.
# bash runs this in place of ~/.bashrc (bash --rcfile). It starts with the user's own start-up files, then adds
# marks around each prompt, command and result (OSC 133 and 633) and reports the working directory (OSC 7).

if [ -n "$WICKED_SHELL_LOGIN" ]; then
    [ -r /etc/profile ] && . /etc/profile
    for __wicked_f in "$HOME/.bash_profile" "$HOME/.bash_login" "$HOME/.profile"; do
        if [ -r "$__wicked_f" ]; then
            . "$__wicked_f"
            break
        fi
    done
else
    [ -r /etc/bash.bashrc ] && . /etc/bash.bashrc
    [ -r "$HOME/.bashrc" ] && . "$HOME/.bashrc"
fi
unset __wicked_f

# Only an interactive shell with a history gets the marks, and only once.
if [[ $- != *i* || -n "$WICKED_SHELL_INTEGRATION_LOADED" ]]; then
    return 0 2>/dev/null
fi
WICKED_SHELL_INTEGRATION_LOADED=1

# Text for an OSC 633 E report: backslash, semicolon and control characters written as \xNN.
__wicked_escape() {
    local s=$1 i c
    __wicked_out=
    for ((i = 0; i < ${#s} && i < 2000; i++)); do
        c=${s:i:1}
        case $c in
            '\') __wicked_out+='\\' ;;
            ';') __wicked_out+='\x3b' ;;
            [[:cntrl:]])
                printf -v c '\\x%02x' "'$c"
                __wicked_out+=$c
                ;;
            *) __wicked_out+=$c ;;
        esac
    done
}

# A path as the path part of a file: URL.
__wicked_url_path() {
    local LC_ALL=C s=$1 i c
    __wicked_out=
    for ((i = 0; i < ${#s}; i++)); do
        c=${s:i:1}
        case $c in
            [a-zA-Z0-9/._~-]) __wicked_out+=$c ;;
            *)
                printf -v c '%%%02X' "'$c"
                __wicked_out+=$c
                ;;
        esac
    done
}

# Runs in PS0, after a command line was read and before it runs: reports the command and that its output starts.
__wicked_ps0() {
    local cmd
    cmd=$(HISTTIMEFORMAT='' builtin history 1)
    cmd=${cmd#"${cmd%%[![:space:]]*}"}
    cmd=${cmd#*[[:space:]]}
    cmd=${cmd#"${cmd%%[![:space:]]*}"}
    __wicked_escape "$cmd"
    printf '\e]633;E;%s\a\e]133;C\a' "$__wicked_out"
}

# First in PROMPT_COMMAND: reports how the last command ended, and where the shell is.
__wicked_precmd() {
    local status=$?
    if [ -n "$__wicked_histcmd" ] && [ "$__wicked_histcmd" != "$HISTCMD" ]; then
        printf '\e]133;D;%s\a' "$status"
    fi
    __wicked_histcmd=$HISTCMD
    __wicked_url_path "$PWD"
    printf '\e]7;file://%s%s\a' "${HOSTNAME:-}" "$__wicked_out"
    return "$status"
}

# Last in PROMPT_COMMAND: puts the marks round whatever prompt the user's own commands left in PS1.
__wicked_prompt_wrap() {
    local status=$?
    case $PS1 in
        *'133;A'*) ;;
        *) PS1='\[\e]133;A\a\]'"$PS1"'\[\e]133;B\a\]' ;;
    esac
    return "$status"
}

PS0='$(__wicked_ps0)'"$PS0"
if [[ "$(declare -p PROMPT_COMMAND 2>/dev/null)" == "declare -a"* ]]; then
    PROMPT_COMMAND=(__wicked_precmd "${PROMPT_COMMAND[@]}" __wicked_prompt_wrap)
else
    __wicked_pc=${PROMPT_COMMAND%"${PROMPT_COMMAND##*[![:space:];]}"}
    if [ -n "$__wicked_pc" ]; then
        PROMPT_COMMAND="__wicked_precmd;$__wicked_pc;__wicked_prompt_wrap"
    else
        PROMPT_COMMAND="__wicked_precmd;__wicked_prompt_wrap"
    fi
    unset __wicked_pc
fi
