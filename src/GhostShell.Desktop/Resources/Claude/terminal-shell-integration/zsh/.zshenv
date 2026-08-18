# Delegate to the pinned Ghostty bootstrap when it is active. Otherwise restore
# the user's original ZDOTDIR and source their real .zshenv before Zsh continues
# with the normal .zprofile/.zshrc sequence. This keeps Claude interception
# independent from Ghostty shell integration without editing user files.
if [[ -r "${GHOSTSHELL_GHOSTTY_ZSH_BOOTSTRAP:-}" ]]; then
    builtin source -- "$GHOSTSHELL_GHOSTTY_ZSH_BOOTSTRAP"
elif [[ "${GHOSTSHELL_CLAUDE_ZSH_ZDOTDIR_PRESENT:-0}" == "1" ]]; then
    builtin export ZDOTDIR="$GHOSTSHELL_CLAUDE_ZSH_ZDOTDIR"
    if [[ -r "$ZDOTDIR/.zshenv" ]]; then
        builtin source -- "$ZDOTDIR/.zshenv"
    fi
else
    builtin unset ZDOTDIR
    if [[ -n "${HOME:-}" && -r "$HOME/.zshenv" ]]; then
        builtin source -- "$HOME/.zshenv"
    fi
fi
builtin unset GHOSTSHELL_GHOSTTY_ZSH_BOOTSTRAP
builtin unset GHOSTSHELL_CLAUDE_ZSH_ZDOTDIR
builtin unset GHOSTSHELL_CLAUDE_ZSH_ZDOTDIR_PRESENT

_ghostshell_claude_after_startup() {
    if [[ -n "${GHOSTSHELL_CLAUDE_SHIM_DIRECTORY:-}" ]]; then
        path=("$GHOSTSHELL_CLAUDE_SHIM_DIRECTORY" "${(@)path:#$GHOSTSHELL_CLAUDE_SHIM_DIRECTORY}")
        builtin export PATH
        builtin rehash
        builtin unalias claude 2>/dev/null || true
        function claude {
            "$GHOSTSHELL_CLAUDE_WRAPPER_HOST" --ghostshell-claude-wrapper "$@"
        }
    fi
    add-zsh-hook -d precmd _ghostshell_claude_after_startup
}

autoload -Uz add-zsh-hook
add-zsh-hook precmd _ghostshell_claude_after_startup
