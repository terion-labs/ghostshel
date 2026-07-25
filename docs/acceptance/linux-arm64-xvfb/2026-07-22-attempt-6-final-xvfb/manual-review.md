# Manual visual review

- Reviewer: Codex
- Reviewed at: `2026-07-22T16:03:45Z`
- Named system: `docker-linux-arm64-xvfb`
- Package SHA-256: `3b8e05a44d575277c3195b1ecb7f64598ab2963ac1c01e5c6edb73fb7961fac5`

## Observations

- `terminal-alternate-active.png` visibly shows a cleared alternate buffer with only
  `ALTERNATE_SCREEN_ACTIVE` and its cursor.
- `terminal-alternate-restored.png` visibly returns to the primary buffer and shows
  both `PRIMARY_BEFORE_ALT` and `PRIMARY_AFTER_ALT`. This closes the visual review
  requested by the runner, while the machine-readable check remains
  `NOT_PROVEN` because the runner does not inspect screenshot pixels.
- `terminal-unicode.png` visibly contains Japanese, Ukrainian, an emoji glyph, and
  the accented grapheme produced from the combining-mark fixture. The matching
  fontconfig results are recorded in `fontconfig-matches.txt`. Exact fallback
  selection, combining-mark geometry, and double-width cell fidelity were not
  measured and remain `NOT_PROVEN`.
- `quick-terminal-x11.png` visibly shows the Quick Terminal frame. The automated
  evidence separately proves that a different X11 client held focus before the
  Xvfb passive-grab trigger and that Escape dismissed the frame.

## Boundary

This is a review of screenshots from an ephemeral Xvfb container. It does not add
physical-host, window-manager, compositor, IME, sleep/wake, Windows, headless, or
ACP coverage.
