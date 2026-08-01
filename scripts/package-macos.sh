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
native_component_catalog="${repository_dir}/licenses/native-terminal-components.json"
native_build_receipt="${repository_dir}/native/artifacts/osx-arm64/native-terminal-build-receipt.json"
font_assets_catalog="${repository_dir}/licenses/terminal-font-assets.json"
font_assets_directory="${repository_dir}/native/artifacts/common/fonts/JetBrainsMono"
font_assets_build_receipt="${repository_dir}/native/artifacts/common/terminal-font-assets-build-receipt.json"
declare_macos_sdk="${repository_dir}/scripts/declare-macos-sdk26.sh"
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

if [[ ! -x "${declare_macos_sdk}" ]]; then
    echo "The macOS SDK declaration helper is unavailable." >&2
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
    "${repository_dir}/native/artifacts/osx-arm64/libghostty-vt.dylib"
    "${repository_dir}/native/artifacts/osx-arm64/GHOSTTY-LICENSE"
    "${repository_dir}/native/artifacts/osx-arm64/ghostty-vt-required-exports.txt"
    "${native_component_catalog}"
    "${native_build_receipt}"
    "${font_assets_catalog}"
    "${font_assets_build_receipt}"
    "${font_assets_directory}/JetBrainsMono-Regular.ttf"
    "${font_assets_directory}/JetBrainsMono-Bold.ttf"
    "${font_assets_directory}/JetBrainsMono-Italic.ttf"
    "${font_assets_directory}/JetBrainsMono-BoldItalic.ttf"
    "${font_assets_directory}/OFL.txt"
    "${font_assets_directory}/MANIFEST.sha256"
)
for required in "${required_native[@]}"; do
    if [[ ! -e "${required}" ]]; then
        echo "The pinned libghostty-vt payload is incomplete; missing $(basename "${required}")." >&2
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

# Keep the runtime assets as a deterministic, independently receipted closure.
# Avalonia also embeds these faces for font discovery, but package provenance
# must remain inspectable without parsing a managed resource container.
publish_font_directory="${publish_dir}/fonts/JetBrainsMono"
mkdir -p "${publish_font_directory}"
for asset in \
    JetBrainsMono-Regular.ttf \
    JetBrainsMono-Bold.ttf \
    JetBrainsMono-Italic.ttf \
    JetBrainsMono-BoldItalic.ttf \
    OFL.txt \
    MANIFEST.sha256; do
    cp "${font_assets_directory}/${asset}" \
        "${publish_font_directory}/${asset}"
done
cp "${font_assets_catalog}" \
    "${publish_dir}/terminal-font-assets.json"
cp "${font_assets_build_receipt}" \
    "${publish_dir}/terminal-font-assets-build-receipt.json"
cp "${font_assets_directory}/OFL.txt" \
    "${publish_dir}/JETBRAINS-MONO-OFL.txt"

required_publish=(
    "${publish_dir}/GhostShell"
    "${publish_dir}/GhostShell.deps.json"
    "${publish_dir}/GhostShell.runtimeconfig.json"
    "${publish_dir}/libghostty-vt.dylib"
    "${publish_dir}/GHOSTTY-LICENSE"
    "${publish_dir}/ghostty-vt-required-exports.txt"
    "${publish_dir}/THIRD-PARTY-NOTICES.md"
    "${publish_dir}/DOTNET-LICENSE.txt"
    "${publish_dir}/DOTNET-THIRD-PARTY-NOTICES.txt"
    "${publish_dir}/native-terminal-components.json"
    "${publish_dir}/native-terminal-build-receipt.json"
    "${publish_dir}/terminal-font-assets.json"
    "${publish_dir}/terminal-font-assets-build-receipt.json"
    "${publish_dir}/JETBRAINS-MONO-OFL.txt"
    "${publish_font_directory}/JetBrainsMono-Regular.ttf"
    "${publish_font_directory}/JetBrainsMono-Bold.ttf"
    "${publish_font_directory}/JetBrainsMono-Italic.ttf"
    "${publish_font_directory}/JetBrainsMono-BoldItalic.ttf"
    "${publish_font_directory}/OFL.txt"
    "${publish_font_directory}/MANIFEST.sha256"
    "${publish_dir}/ghostty/shell-integration/MANIFEST.sha256"
    "${publish_dir}/ghostty/shell-integration/bash/ghostty.bash"
    "${publish_dir}/ghostty/shell-integration/bash/bash-preexec.sh"
    "${publish_dir}/ghostty/shell-integration/fish/vendor_conf.d/ghostty-shell-integration.fish"
    "${publish_dir}/ghostty/shell-integration/zsh/.zshenv"
    "${publish_dir}/ghostty/shell-integration/zsh/ghostty-integration"
    "${publish_dir}/ghostty/shell-integration/SHELL-INTEGRATION-NOTICE.md"
)
for required in "${required_publish[@]}"; do
    if [[ ! -e "${required}" ]]; then
        echo "The self-contained publish is incomplete; missing $(basename "${required}")." >&2
        exit 1
    fi
done

# macOS uses the apphost's SDK marker for compatibility styling. Rewrite the
# temporary apphost before any package fingerprint is produced; release signing
# replaces the helper's ad-hoc signature later.
"${declare_macos_sdk}" "${publish_dir}/GhostShell"

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

if ! /usr/bin/file "${publish_dir}/libghostty-vt.dylib" \
        | grep -Eq 'Mach-O 64-bit dynamically linked shared library arm64'; then
    echo "libghostty-vt.dylib is not a macOS arm64 dynamic library." >&2
    exit 1
fi
if ! /usr/bin/otool -D "${publish_dir}/libghostty-vt.dylib" \
        | grep -Fxq '@rpath/libghostty-vt.dylib'; then
    echo "libghostty-vt.dylib has an unexpected install name." >&2
    exit 1
fi
unexpected_dependencies="$(
    /usr/bin/otool -L "${publish_dir}/libghostty-vt.dylib" \
        | tail -n +2 \
        | awk '{print $1}' \
        | grep -Fvx '@rpath/libghostty-vt.dylib' \
        | grep -Fvx '/usr/lib/libSystem.B.dylib' \
        || true
)"
if [[ -n "${unexpected_dependencies}" ]]; then
    echo "libghostty-vt.dylib has an unexpected dynamic dependency." >&2
    exit 1
fi
unexpected_exports="$(
    /usr/bin/nm -gU "${publish_dir}/libghostty-vt.dylib" \
        | awk '$2 != "U" {print $3}' \
        | grep -Ev '^_ghostty_' \
        || true
)"
if [[ -n "${unexpected_exports}" ]]; then
    echo "libghostty-vt.dylib exports symbols outside the Ghostty C ABI." >&2
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
    --font-assets-catalog "${font_assets_catalog}" \
    --font-assets-build-receipt "${font_assets_build_receipt}" \
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
