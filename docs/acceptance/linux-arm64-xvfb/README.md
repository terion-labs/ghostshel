# Linux arm64 Xvfb packaged acceptance

The most complete historical run archived in this directory is
`2026-07-22-attempt-6-final-xvfb`. It names the tested system
`docker-linux-arm64-xvfb` and records both the declared name and the actual
ephemeral container identity.

The current harness adds Openbox, active-work confirmation, PID/start-time
identity checks, causal guarded-paste evidence, atomic receipts, and stale-output
protection. Its latest local artifact is
`artifacts/platform-acceptance/20260723-renderer-focus-linux-arm64-xvfb`, as
described in `docs/platform-terminal-acceptance.md`. That stronger run remains
`NOT_PASSING`: ten bounded checks pass, seven are `NOT_PROVEN`, and the final
lifecycle input is not delivered by the synthetic focus sequence. Do not use the
older lifecycle result below to override the current receipt.

## Authoritative result

- Overall: `NOT_PASSING` (bounded supplementary evidence, not a physical-host
  release sign-off)
- Architecture: native Docker `linux/aarch64`
- Package: self-contained `linux-arm64` ELF
- Package SHA-256:
  `3b8e05a44d575277c3195b1ecb7f64598ab2963ac1c01e5c6edb73fb7961fac5`
- Source snapshot SHA-256:
  `afb6d6ab05fbff11a63e9e545ba73ba1e59468935460ee45e6a69c2f67307e04`
- Machine-readable results: `2026-07-22-attempt-6-final-xvfb/evidence.json`
- Human-readable results: `2026-07-22-attempt-6-final-xvfb/evidence.md`
- Historical manual screenshot review:
  `2026-07-22-attempt-6-final-xvfb/manual-review.md`

That historical packaged run passed startup, real PTY allocation, UTF-8 byte roundtrip, PTY
resize propagation, an interactive `less` TUI, SGR mouse press/release/drag/wheel,
guarded multiline paste cancellation and confirmation, brokerless OSC 52
fail-closed behavior, Xvfb cross-client Quick Terminal activation/dismissal, and
normal desktop/child-PTY lifecycle.

The evidence deliberately leaves exact Unicode glyph/cell fidelity, process-side
OSC 52 query bytes, physical/compositor global-hotkey behavior, IME, a physical
X11 compositor, and sleep/wake as `NOT_PROVEN`. Windows, headless mode, and ACP
were not exercised. The alternate-screen before/active/after screenshots were
manually reviewed and are described in the authoritative run's review file.

## Attempt history

- `attempt-1-dbus-type-failure`: preserved the startup failure caused by the
  incompatible direct `Tmds.DBus.Protocol` pin.
- `attempt-2-avalonia-12.0.1`: confirmed the supported Avalonia dependency fix
  opened the packaged window; the first clipboard-owner harness then stalled.
- `attempt-3-full-xvfb`: completed the broader fixture; its helper Xterm did not
  map, so the Quick Terminal result was a harness failure.
- `attempt-4-full-xvfb`: replaced the helper with a mapped Xmessage client and
  passed the scoped Quick Terminal check; CJK glyphs were absent from the image's
  font stack.
- `attempt-5-final-xvfb`: installed the intended JetBrains Mono and Noto CJK/emoji
  fonts and captured visible Unicode glyphs.
- `attempt-6-final-xvfb`: added recorded font matches and a render delay that
  captured the alternate screen while active; this is the most complete archived
  pre-Openbox run.
