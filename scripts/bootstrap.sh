#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_dir="$(cd "${script_dir}/.." && pwd)"
sdk_dir="${repository_dir}/.dotnet"
sdk_version="10.0.302"

if [[ -x "${sdk_dir}/dotnet" ]] &&
   [[ "$("${sdk_dir}/dotnet" --version)" == "${sdk_version}" ]]; then
    echo ".NET SDK ${sdk_version} is already installed in ${sdk_dir}."
else
    installer="$(mktemp -t ghostshell-dotnet-install.XXXXXX)"
    trap 'rm -f "${installer}"' EXIT

    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${installer}"
    bash "${installer}" --version "${sdk_version}" --install-dir "${sdk_dir}" --no-path
    echo "Installed .NET SDK ${sdk_version} in ${sdk_dir}."
fi

if [[ "${GHOSTSHELL_SKIP_NATIVE-0}" != "1" ]]; then
    host_os="$(uname -s)"
    host_arch="$(uname -m)"
    case "${host_os}:${host_arch}" in
        Darwin:arm64)
            native_rid="osx-arm64"
            ;;
        Darwin:x86_64)
            native_rid="osx-x64"
            ;;
        Linux:aarch64|Linux:arm64)
            native_rid="linux-arm64"
            ;;
        Linux:x86_64)
            native_rid="linux-x64"
            ;;
        *)
            echo "GhostSHELL has no native terminal build for ${host_os} ${host_arch}." >&2
            exit 1
            ;;
    esac

    "${script_dir}/build-libghostty-vt.sh" --rid "${native_rid}"
fi
