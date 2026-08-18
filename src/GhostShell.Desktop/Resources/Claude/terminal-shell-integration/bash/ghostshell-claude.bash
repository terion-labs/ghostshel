# GhostSHELL owns this small companion bootstrap. The pinned Ghostty bootstrap
# runs first and recreates Bash's normal startup sequence, including user rc.
if [[ -r "${GHOSTSHELL_GHOSTTY_BASH_BOOTSTRAP:-}" ]]; then
    builtin source -- "$GHOSTSHELL_GHOSTTY_BASH_BOOTSTRAP"
fi
builtin unset GHOSTSHELL_GHOSTTY_BASH_BOOTSTRAP

if [[ "$-" == *i* && -n "${GHOSTSHELL_CLAUDE_SHIM_DIRECTORY:-}" ]]; then
    IFS=: builtin read -r -a __ghostshell_path_entries <<< "${PATH-}"
    __ghostshell_path="$GHOSTSHELL_CLAUDE_SHIM_DIRECTORY"
    for __ghostshell_path_entry in "${__ghostshell_path_entries[@]}"; do
        if [[ -n "$__ghostshell_path_entry" && "$__ghostshell_path_entry" != "$GHOSTSHELL_CLAUDE_SHIM_DIRECTORY" ]]; then
            __ghostshell_path="$__ghostshell_path:$__ghostshell_path_entry"
        fi
    done
    builtin export PATH="$__ghostshell_path"
    builtin unset __ghostshell_path __ghostshell_path_entry __ghostshell_path_entries
    builtin hash -r 2>/dev/null || true
    builtin unalias claude 2>/dev/null || true
    function claude {
        "$GHOSTSHELL_CLAUDE_WRAPPER_HOST" --ghostshell-claude-wrapper "$@"
    }
fi
