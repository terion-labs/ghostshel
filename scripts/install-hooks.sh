#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_dir="$(cd "${script_dir}/.." && pwd)"

git -C "${repository_dir}" rev-parse --show-toplevel >/dev/null
git -C "${repository_dir}" config --local core.hooksPath .githooks

echo "Installed repository hooks from .githooks."
