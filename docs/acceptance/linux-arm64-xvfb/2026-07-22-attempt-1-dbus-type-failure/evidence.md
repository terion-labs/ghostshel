# GhostSHELL Linux arm64 Xvfb packaged acceptance

- Declared system: `docker-linux-arm64-xvfb`
- Actual container host: `bf44871e2be9`
- Environment: Docker Linux arm64 under Xvfb (no window manager)
- OS: `Linux-7.0.11-orbstack-00360-gc9bc4d96ac70-aarch64-with-glibc2.39`
- Package SHA-256: `3b8e05a44d575277c3195b1ecb7f64598ab2963ac1c01e5c6edb73fb7961fac5`
- Source snapshot SHA-256: `b0fb4ca774285870cbd4e2fef6e44c903a8ecb129fd65aba8517e15fbec5445f`
- Overall: **NOT_PASSING**

This evidence is deliberately bounded. A passing automated observation under Xvfb does not imply physical-host, compositor, IME, sleep/wake, or Windows coverage.

| Check | Result | Evidence notes |
| --- | --- | --- |
| Packaged Avalonia desktop startup | FAIL | No visible X11 window matched '^GhostSHELL$'. Artifacts: `ghostshell.log`. |
| IME preedit, candidate placement, and committed composition | NOT_PROVEN | Xvfb has no desktop input-method compositor. Unicode clipboard/input coverage does not prove IME composition. |
| Physical X11 desktop and compositor behavior | NOT_PROVEN | This named system is an Xvfb server inside an arm64 Docker VM, not a physical/self-hosted X11 desktop. Window-manager focus, compositor effects, and human interaction remain unproven. |
| Host sleep and wake recovery | NOT_PROVEN | A Docker/Xvfb container cannot suspend and resume the named physical host. |
