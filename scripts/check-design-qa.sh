#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_dir="$(cd "${script_dir}/.." && pwd)"
capture_dir="$(mktemp -d "${TMPDIR:-/tmp}/ghostshell-design-qa.XXXXXX")"

cleanup() {
    status=$?
    if [[ "${status}" == "0" ]]; then
        rm -rf "${capture_dir}"
    else
        echo "Failed design QA captures preserved at ${capture_dir}" >&2
    fi
}
trap cleanup EXIT

if [[ -n "${GHOSTSHELL_DOTNET:-}" ]]; then
    dotnet="${GHOSTSHELL_DOTNET}"
else
    dotnet="${repository_dir}/.dotnet/dotnet"
fi

cd "${repository_dir}"
"${dotnet}" run \
    --project tools/GhostShell.DesignQa \
    --configuration Release \
    --no-build \
    --no-restore \
    -- \
    --gate \
    "${capture_dir}" \
    "${repository_dir}/tools/GhostShell.DesignQa/design-qa-baseline.json"
