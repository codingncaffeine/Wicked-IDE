# Wicked terminal integration for fish: marks round each prompt, command and result, and the working directory.
if set -q WICKED_SHELL_INTEGRATION_LOADED
    exit
end
set -g WICKED_SHELL_INTEGRATION_LOADED 1

function __wicked_status
    return $argv[1]
end

if functions -q fish_prompt
    functions -c fish_prompt __wicked_fish_prompt_original
else
    function __wicked_fish_prompt_original
    end
end

function fish_prompt
    set -l last_status $status
    printf '\e]133;A\a'
    __wicked_status $last_status
    __wicked_fish_prompt_original
    printf '\e]133;B\a'
end

function __wicked_preexec --on-event fish_preexec
    set -l cmd (string replace -a '\\' '\\\\' -- $argv[1] | string replace -a ';' '\\x3b' | string replace -a \n '\\x0a')
    printf '\e]633;E;%s\a\e]133;C\a' $cmd
end

function __wicked_postexec --on-event fish_postexec
    set -l last_status $status
    printf '\e]133;D;%s\a' $last_status
end

function __wicked_cwd --on-variable PWD
    printf '\e]7;file://%s%s\a' (hostname) (string escape --style=url -- $PWD | string replace -a '%2F' '/')
end
__wicked_cwd
