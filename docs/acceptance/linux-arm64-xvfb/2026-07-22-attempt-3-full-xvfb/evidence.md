# GhostSHELL Linux arm64 Xvfb packaged acceptance

- Declared system: `docker-linux-arm64-xvfb`
- Actual container host: `008f17add977`
- Environment: Docker Linux arm64 under Xvfb (no window manager)
- OS: `Linux-7.0.11-orbstack-00360-gc9bc4d96ac70-aarch64-with-glibc2.39`
- Package SHA-256: `3b8e05a44d575277c3195b1ecb7f64598ab2963ac1c01e5c6edb73fb7961fac5`
- Source snapshot SHA-256: `2a26756bc023de3a70bdf22b3410bb8452c358677d5b6e0048f2c1bcede4e84f`
- Overall: **NOT_PASSING**

This evidence is deliberately bounded. A passing automated observation under Xvfb does not imply physical-host, compositor, IME, sleep/wake, or Windows coverage.

| Check | Result | Evidence notes |
| --- | --- | --- |
| Packaged Avalonia desktop startup | PASS | The self-contained package opened its real X11 launcher and remained alive. Artifacts: `launcher.png`, `launcher-window-geometry.txt`, `ghostshell.log`. |
| Managed renderer to real PTY | PASS | tty=/dev/pts/0; is_tty=yes Artifacts: `terminal-pty.png`, `runtime/pty.txt`. |
| Unicode input and output through the managed renderer | PASS | UTF-8 Japanese, Ukrainian, emoji, and a combining accent round-tripped through X11 clipboard paste, the terminal input contract, the PTY, and shell output. Artifacts: `terminal-unicode.png`, `runtime/unicode.txt`. |
| X11 viewport to PTY grid resize | PASS | stty size changed from 26x76 to 19x56. Artifacts: `terminal-resized.png`, `runtime/size-before.txt`, `runtime/size-after.txt`. |
| Interactive less TUI through the packaged renderer | PASS | less entered an interactive screen, accepted PageDown and q, then returned control to the shell. Artifacts: `terminal-less-tui.png`, `runtime/tui.txt`. |
| Alternate-screen entry and restoration | NOT_PROVEN | The real PTY fixture completed without crashing and before/after screenshots were captured, but this runner does not use OCR or a screen-snapshot API to assert restoration. Manual review is still required. Artifacts: `terminal-alternate-active.png`, `terminal-alternate-restored.png`, `runtime/alternate-completed.txt`. |
| SGR mouse reporting through X11 and the PTY | PASS | Captured 5 SGR reports; required press, release, drag, and wheel reports were present. Artifacts: `runtime/mouse-reporting.json`. |
| Unsafe multiline paste confirmation | PASS | A multiline paste stayed pending, Escape cancelled it without execution, and an explicitly confirmed paste executed exactly once. Artifacts: `terminal-paste-confirmation.png`, `runtime/paste-confirmed.txt`. |
| Brokerless OSC 52 clipboard write policy | PASS | The managed adapter has no safe process-originated clipboard broker; its documented contract discarded OSC 52 and preserved the existing clipboard. Artifacts: `osc52-clipboard-observation.txt`. |
| Brokerless OSC 52 clipboard read response | NOT_PROVEN | Unit conformance covers the empty denial response, but this packaged run did not capture process-side bytes for an OSC 52 query. |
| X11-global Quick Terminal registration and Escape dismissal | FAIL | No visible X11 window matched '^GhostSHELL Acceptance Other Client$'. |
| Normal desktop and child PTY lifecycle | PASS | Desktop exit code: 0; surviving captured descendants: []. Artifacts: `runtime/lifecycle.txt`. |
| IME preedit, candidate placement, and committed composition | NOT_PROVEN | Xvfb has no desktop input-method compositor. Unicode clipboard/input coverage does not prove IME composition. |
| Physical X11 desktop and compositor behavior | NOT_PROVEN | This named system is an Xvfb server inside an arm64 Docker VM, not a physical/self-hosted X11 desktop. Window-manager focus, compositor effects, and human interaction remain unproven. |
| Host sleep and wake recovery | NOT_PROVEN | A Docker/Xvfb container cannot suspend and resume the named physical host. |
