#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_dir="$(cd -- "${script_dir}/.." && pwd -P)"
dotnet="${repository_dir}/.dotnet/dotnet"
configuration="Release"
version=""
build_version=""
output=""
component_catalog="${repository_dir}/licenses/managed-components.json"
native_component_catalog="${repository_dir}/licenses/native-macos-components.json"
native_build_receipt="${repository_dir}/native/artifacts/osx-arm64/native-macos-build-receipt.json"
native_build_evidence="${repository_dir}/native/ghostty/provenance/macos-arm64-build-evidence.json"
native_resource_evidence="${repository_dir}/native/ghostty/provenance/macos-arm64-resource-evidence.json"
nuget_packages="${NUGET_PACKAGES:-${HOME}/.nuget/packages}"

usage() {
    cat >&2 <<'EOF'
Usage:
  ./scripts/package-macos.sh \
    --version <major.minor.patch> \
    --build-version <number[.number...]> \
    --output <path/to/GhostShell.app> \
    [--configuration Release]

Creates an unsigned, self-contained macOS arm64 release candidate. The
destination must not already exist. The script never launches the application.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            version="$2"
            shift 2
            ;;
        --build-version)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            build_version="$2"
            shift 2
            ;;
        --output)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            output="$2"
            shift 2
            ;;
        --configuration)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            configuration="$2"
            shift 2
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage
            exit 64
            ;;
    esac
done

if [[ -z "${version}" || -z "${build_version}" || -z "${output}" ]]; then
    usage
    exit 64
fi

if [[ "$(uname -s)" != "Darwin" || "$(uname -m)" != "arm64" ]]; then
    echo "The current macOS release candidate requires an arm64 macOS host." >&2
    exit 1
fi

if [[ ! -x "${dotnet}" ]]; then
    echo "Run ./scripts/bootstrap.sh before packaging GhostSHELL." >&2
    exit 1
fi

if [[ "$(basename "${output}")" != "GhostShell.app" ]]; then
    echo "The --output path must end in GhostShell.app." >&2
    exit 64
fi

output_parent="$(dirname "${output}")"
if [[ ! -d "${output_parent}" ]]; then
    echo "The package destination parent does not exist." >&2
    exit 1
fi
output_parent="$(cd "${output_parent}" && pwd -P)"
output="${output_parent}/GhostShell.app"

if [[ -e "${output}" ]]; then
    echo "The package destination already exists and will not be overwritten." >&2
    exit 1
fi

required_native=(
    "${repository_dir}/native/artifacts/osx-arm64/libghostshell-ghostty.dylib"
    "${repository_dir}/native/artifacts/osx-arm64/libghostty.dylib"
    "${repository_dir}/native/artifacts/osx-arm64/GHOSTTY-LICENSE"
    "${repository_dir}/native/artifacts/osx-arm64/ghostty"
    "${repository_dir}/native/artifacts/osx-arm64/terminfo"
    "${native_component_catalog}"
    "${native_build_receipt}"
    "${native_build_evidence}"
    "${native_resource_evidence}"
)
for required in "${required_native[@]}"; do
    if [[ ! -e "${required}" ]]; then
        echo "The pinned Ghostty payload is incomplete; missing $(basename "${required}")." >&2
        exit 1
    fi
done

working_dir="$(mktemp -d "${TMPDIR:-/tmp}/ghostshell-package-macos.XXXXXX")"
candidate_parent="$(mktemp -d "${output_parent}/.ghostshell-package.XXXXXX")"
candidate="${candidate_parent}/GhostShell.app"
cleanup() {
    rm -rf -- "${working_dir}"
    rm -rf -- "${candidate_parent}"
}
trap cleanup EXIT

publish_dir="${working_dir}/publish"
"${dotnet}" publish \
    "${repository_dir}/src/GhostShell.Desktop/GhostShell.Desktop.csproj" \
    --configuration "${configuration}" \
    --runtime osx-arm64 \
    --self-contained true \
    --output "${publish_dir}" \
    -p:Version="${version}" \
    -p:AssemblyVersion="${version}" \
    -p:FileVersion="${version}" \
    -p:InformationalVersion="${version}" \
    -p:DebugType=None \
    -p:DebugSymbols=false

required_publish=(
    "${publish_dir}/GhostShell"
    "${publish_dir}/GhostShell.deps.json"
    "${publish_dir}/GhostShell.runtimeconfig.json"
    "${publish_dir}/libghostshell-ghostty.dylib"
    "${publish_dir}/libghostty.dylib"
    "${publish_dir}/GHOSTTY-LICENSE"
    "${publish_dir}/THIRD-PARTY-NOTICES.md"
    "${publish_dir}/DOTNET-LICENSE.txt"
    "${publish_dir}/DOTNET-THIRD-PARTY-NOTICES.txt"
    "${publish_dir}/native-macos-components.json"
    "${publish_dir}/native-macos-build-receipt.json"
    "${publish_dir}/macos-arm64-build-evidence.json"
    "${publish_dir}/macos-arm64-resource-evidence.json"
    "${publish_dir}/ghostty"
    "${publish_dir}/terminfo"
)
for required in "${required_publish[@]}"; do
    if [[ ! -e "${required}" ]]; then
        echo "The self-contained publish is incomplete; missing $(basename "${required}")." >&2
        exit 1
    fi
done

if find "${publish_dir}" -maxdepth 1 -type f -name 'GhostShell*.pdb' -print -quit \
        | grep -q .; then
    echo "The self-contained publish unexpectedly contains first-party debug symbols." >&2
    exit 1
fi

first_party_assemblies=(
    "GhostShell.dll"
    "GhostShell.Agent.dll"
    "GhostShell.Agent.Providers.dll"
    "GhostShell.Agent.Runtime.dll"
    "GhostShell.App.dll"
    "GhostShell.Application.dll"
    "GhostShell.Browser.dll"
    "GhostShell.Core.dll"
    "GhostShell.Files.dll"
    "GhostShell.Infrastructure.dll"
    "GhostShell.Mcp.dll"
    "GhostShell.Monitoring.dll"
    "GhostShell.Protocol.dll"
    "GhostShell.SessionHost.dll"
    "GhostShell.Terminal.dll"
)
for assembly in "${first_party_assemblies[@]}"; do
    if [[ ! -f "${publish_dir}/${assembly}" ]]; then
        echo "The self-contained publish is incomplete; missing ${assembly}." >&2
        exit 1
    fi

    if LC_ALL=C grep -aFq "${repository_dir}" "${publish_dir}/${assembly}"; then
        echo "${assembly} embeds the build host repository path." >&2
        exit 1
    fi
done

if find "${publish_dir}" ! -type f ! -type d -print -quit | grep -q .; then
    echo "The self-contained publish contains a symbolic link or special entry." >&2
    exit 1
fi

if ! /usr/bin/file "${publish_dir}/GhostShell" \
        | grep -Eq 'Mach-O 64-bit executable arm64'; then
    echo "The published GhostShell executable is not a macOS arm64 Mach-O executable." >&2
    exit 1
fi

for library in libghostshell-ghostty.dylib libghostty.dylib; do
    if ! /usr/bin/file "${publish_dir}/${library}" \
            | grep -Eq 'Mach-O 64-bit dynamically linked shared library arm64'; then
        echo "${library} is not a macOS arm64 dynamic library." >&2
        exit 1
    fi
done

if ! /usr/bin/otool -L "${publish_dir}/libghostshell-ghostty.dylib" \
        | grep -Fq '@rpath/libghostty.dylib'; then
    echo "The GhostSHELL shim is not linked to the colocated libghostty runtime." >&2
    exit 1
fi
if ! /usr/bin/otool -l "${publish_dir}/libghostshell-ghostty.dylib" \
        | grep -A2 'LC_RPATH' \
        | grep -Fq '@loader_path'; then
    echo "The GhostSHELL shim does not resolve libghostty beside itself." >&2
    exit 1
fi

"${dotnet}" run \
    --project "${repository_dir}/tools/GhostShell.Packaging/GhostShell.Packaging.csproj" \
    --configuration Release \
    -- \
    macos \
    --publish "${publish_dir}" \
    --output "${candidate}" \
    --version "${version}" \
    --build-version "${build_version}" \
    --component-catalog "${component_catalog}" \
    --native-component-catalog "${native_component_catalog}" \
    --native-build-receipt "${native_build_receipt}" \
    --nuget-packages "${nuget_packages}"

/usr/bin/plutil -lint "${candidate}/Contents/Info.plist"

if [[ ! -x "${candidate}/Contents/MacOS/GhostShell" ]]; then
    echo "The packaged GhostShell executable is not executable." >&2
    exit 1
fi

"${dotnet}" run \
    --project \
    "${repository_dir}/tools/GhostShell.AccessibilityAcceptance/GhostShell.AccessibilityAcceptance.csproj" \
    --configuration Release \
    -- \
    publish-macos-package \
    --build-label "macos-${version}-${build_version}" \
    --package "${candidate}" \
    --output "${output}"

echo "Created unsigned macOS release candidate at ${output}."
