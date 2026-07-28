#!/usr/bin/env bash
# Compiles the terminal corner-mask geometry out of the shim and checks which
# corners it carves. Runs only on macOS, where the shim is built.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
[ "$(uname -s)" = "Darwin" ] || { echo "corner mask geometry: skipped (not macOS)"; exit 0; }
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT
clang -fobjc-arc -framework CoreGraphics -framework Foundation \
  "$root/native/macos/tests/corner-mask-test.m" -o "$out/corner-mask-test"
"$out/corner-mask-test"
