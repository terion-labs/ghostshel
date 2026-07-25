#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_dir="$(cd "${script_dir}/.." && pwd)"
artifact_dir="${repository_dir}/native/artifacts/osx-arm64"
smoke_test="${artifact_dir}/ghostshell-ghostty-smoke"

if [[ ! -x "${smoke_test}" ]]; then
    echo "Run ./scripts/build-native-macos.sh first." >&2
    exit 1
fi

GHOSTTY_RESOURCES_DIR="${artifact_dir}/ghostty" "${smoke_test}"
