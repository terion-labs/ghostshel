function __ghostshell_claude_after_startup --on-event fish_prompt
    functions -e __ghostshell_claude_after_startup
    if test -z "$GHOSTSHELL_CLAUDE_SHIM_DIRECTORY"
        return
    end

    set --local next_path "$GHOSTSHELL_CLAUDE_SHIM_DIRECTORY"
    for entry in $PATH
        if test "$entry" != "$GHOSTSHELL_CLAUDE_SHIM_DIRECTORY"
            set --append next_path "$entry"
        end
    end
    set --global --export PATH $next_path

    function claude
        command "$GHOSTSHELL_CLAUDE_WRAPPER_HOST" --ghostshell-claude-wrapper $argv
    end
end
