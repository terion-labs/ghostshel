@echo off
if not defined GHOSTSHELL_CLAUDE_WRAPPER_HOST (
  echo GhostSHELL's Claude launcher is unavailable. 1>&2
  exit /b 126
)
"%GHOSTSHELL_CLAUDE_WRAPPER_HOST%" --ghostshell-claude-wrapper %*
exit /b %ERRORLEVEL%
