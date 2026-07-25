#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_dir="$(cd "${script_dir}/.." && pwd)"
dotnet="${repository_dir}/.dotnet/dotnet"

if [[ ! -x "${dotnet}" ]]; then
    echo "Run ./scripts/bootstrap.sh first." >&2
    exit 1
fi

"${dotnet}" build "${repository_dir}/GhostShell.slnx"
"${dotnet}" test "${repository_dir}/GhostShell.slnx" --no-build
"${dotnet}" format "${repository_dir}/GhostShell.slnx" --verify-no-changes --no-restore

if [[ "${GHOSTSHELL_RUN_NATIVE_SMOKE-0}" == "1" ]]; then
    "${script_dir}/smoke-terminal.sh"
fi
