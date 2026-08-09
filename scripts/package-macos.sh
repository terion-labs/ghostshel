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
sql_language_artifact_directory="${repository_dir}/native/artifacts/osx-arm64"
sql_language_worker="${sql_language_artifact_directory}/ghostshell-sql-language"
sql_language_receipt="${sql_language_artifact_directory}/build-receipt.json"
maximum_sql_language_macos_version="13.0"

normalize_macos_version() {
    /usr/bin/awk -v version="$1" '
        BEGIN {
            if (version !~ /^[0-9]+([.][0-9]+)?([.][0-9]+)?$/) {
                exit 1
            }
            count = split(version, components, ".")
            major = components[1] + 0
            minor = count >= 2 ? components[2] + 0 : 0
            patch = count >= 3 ? components[3] + 0 : 0
            printf "%d.%d.%d\n", major, minor, patch
        }
    '
}

macos_version_is_at_most() {
    /usr/bin/awk -v candidate="$1" -v maximum="$2" '
        BEGIN {
            split(candidate, candidate_components, ".")
            split(maximum, maximum_components, ".")
            for (position = 1; position <= 3; position++) {
                candidate_component = candidate_components[position] + 0
                maximum_component = maximum_components[position] + 0
                if (candidate_component < maximum_component) {
                    exit 0
                }
                if (candidate_component > maximum_component) {
                    exit 1
                }
            }
            exit 0
        }
    '
}

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
    "${sql_language_worker}"
    "${sql_language_artifact_directory}/THIRD-PARTY-NOTICES.md"
    "${sql_language_artifact_directory}/runtime-dependencies.txt"
    "${sql_language_receipt}"
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
        echo "The pinned native payload is incomplete; missing $(basename "${required}")." >&2
        exit 1
    fi
done

sql_language_receipt_rid="$(/usr/bin/plutil -extract rid raw -o - "${sql_language_receipt}")"
sql_language_receipt_protocol="$(/usr/bin/plutil -extract protocolVersion raw -o - "${sql_language_receipt}")"
sql_language_receipt_artifact="$(/usr/bin/plutil -extract artifact raw -o - "${sql_language_receipt}")"
sql_language_receipt_abi="$(/usr/bin/plutil -extract abi raw -o - "${sql_language_receipt}")"
sql_language_receipt_minos="$(/usr/bin/plutil -extract minimumOsVersion raw -o - "${sql_language_receipt}")"
sql_language_expected_sha="$(/usr/bin/plutil -extract sha256 raw -o - "${sql_language_receipt}")"
sql_language_legal_closure_format="$(/usr/bin/plutil -extract legalClosureFormatVersion raw -expect integer -o - "${sql_language_receipt}")"
sql_language_legal_document_count="$(/usr/bin/plutil -extract legalDocumentCount raw -expect integer -o - "${sql_language_receipt}")"
sql_language_legal_review_required_count="$(/usr/bin/plutil -extract legalReviewRequiredCount raw -expect integer -o - "${sql_language_receipt}")"
sql_language_runtime_dependency_count="$(/usr/bin/plutil -extract runtimeDependencyCount raw -expect integer -o - "${sql_language_receipt}")"
sql_language_expected_dependencies_sha="$(/usr/bin/plutil -extract runtimeDependenciesSha256 raw -expect string -o - "${sql_language_receipt}")"
sql_language_expected_notices_sha="$(/usr/bin/plutil -extract thirdPartyNoticesSha256 raw -expect string -o - "${sql_language_receipt}")"
sql_language_actual_sha="$(/usr/bin/shasum -a 256 "${sql_language_worker}" | /usr/bin/awk '{print $1}')"
sql_language_actual_dependencies_sha="$(/usr/bin/shasum -a 256 "${sql_language_artifact_directory}/runtime-dependencies.txt" | /usr/bin/awk '{print $1}')"
sql_language_actual_notices_sha="$(/usr/bin/shasum -a 256 "${sql_language_artifact_directory}/THIRD-PARTY-NOTICES.md" | /usr/bin/awk '{print $1}')"
sql_language_file_type="$(/usr/bin/file -b "${sql_language_worker}")"
if [[ "${sql_language_receipt_rid}" != "osx-arm64" \
    || "${sql_language_receipt_protocol}" != "1" \
    || "${sql_language_receipt_artifact}" != "ghostshell-sql-language" \
    || "${sql_language_receipt_abi}" != "darwin-arm64" \
    || "${sql_language_expected_sha}" != "${sql_language_actual_sha}" \
    || "${sql_language_file_type}" != *"Mach-O 64-bit executable arm64"* ]]; then
    echo "The SQL language worker does not match its osx-arm64 build receipt." >&2
    exit 1
fi
if [[ ! "${sql_language_legal_closure_format}" =~ ^[0-9]+$ \
    || ! "${sql_language_legal_document_count}" =~ ^[0-9]+$ \
    || ! "${sql_language_legal_review_required_count}" =~ ^[0-9]+$ \
    || ! "${sql_language_runtime_dependency_count}" =~ ^[0-9]+$ \
    || "${sql_language_legal_closure_format}" -lt 1 \
    || "${sql_language_legal_document_count}" -lt 1 \
    || "${sql_language_runtime_dependency_count}" -lt 1 ]]; then
    echo "The SQL language worker receipt has invalid legal closure counts." >&2
    exit 1
fi
if [[ "${sql_language_expected_dependencies_sha}" != "${sql_language_actual_dependencies_sha}" \
    || "${sql_language_expected_notices_sha}" != "${sql_language_actual_notices_sha}" ]]; then
    echo "The SQL language worker legal files do not match its build receipt." >&2
    exit 1
fi

sql_language_build_version_count="$(
    /usr/bin/otool -l "${sql_language_worker}" \
        | /usr/bin/awk '$1 == "cmd" && $2 == "LC_BUILD_VERSION" { count++ } END { print count + 0 }'
)"
sql_language_minos="$(
    /usr/bin/otool -l "${sql_language_worker}" \
        | /usr/bin/awk '
            $1 == "cmd" && $2 == "LC_BUILD_VERSION" { in_build_version = 1; next }
            in_build_version && $1 == "minos" { print $2; in_build_version = 0 }
        '
)"
sql_language_platform="$(
    /usr/bin/otool -l "${sql_language_worker}" \
        | /usr/bin/awk '
            $1 == "cmd" && $2 == "LC_BUILD_VERSION" { in_build_version = 1; next }
            in_build_version && $1 == "platform" { print $2; in_build_version = 0 }
        '
)"
if [[ "${sql_language_build_version_count}" != "1" \
    || -z "${sql_language_minos}" \
    || ( "${sql_language_platform}" != "1" && "${sql_language_platform}" != "MACOS" ) ]]; then
    echo "The SQL language worker must contain exactly one LC_BUILD_VERSION command." >&2
    exit 1
fi

if ! sql_language_receipt_minos_normalized="$(normalize_macos_version "${sql_language_receipt_minos}")" \
    || ! sql_language_minos_normalized="$(normalize_macos_version "${sql_language_minos}")" \
    || ! maximum_sql_language_macos_version_normalized="$(normalize_macos_version "${maximum_sql_language_macos_version}")"; then
    echo "The SQL language worker has a malformed macOS compatibility version." >&2
    exit 1
fi
if [[ "${sql_language_receipt_minos_normalized}" != "${sql_language_minos_normalized}" ]]; then
    echo "The SQL language worker LC_BUILD_VERSION does not match its build receipt." >&2
    exit 1
fi
if ! macos_version_is_at_most \
    "${sql_language_minos_normalized}" \
    "${maximum_sql_language_macos_version_normalized}"; then
    echo "The SQL language worker requires macOS ${sql_language_minos}, newer than GhostShell's macOS ${maximum_sql_language_macos_version} minimum." >&2
    exit 1
fi

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
    -p:DebugSymbols=false \
    -p:GhostShellSqlLanguageRequired=true

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
    "${publish_dir}/runtimes/osx-arm64/native/ghostshell-sql-language"
    "${publish_dir}/runtimes/osx-arm64/native/THIRD-PARTY-NOTICES.md"
    "${publish_dir}/runtimes/osx-arm64/native/runtime-dependencies.txt"
    "${publish_dir}/runtimes/osx-arm64/native/build-receipt.json"
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

published_sql_language_worker="${publish_dir}/runtimes/osx-arm64/native/ghostshell-sql-language"
published_sql_language_receipt="${publish_dir}/runtimes/osx-arm64/native/build-receipt.json"
published_sql_language_dependencies="${publish_dir}/runtimes/osx-arm64/native/runtime-dependencies.txt"
published_sql_language_notices="${publish_dir}/runtimes/osx-arm64/native/THIRD-PARTY-NOTICES.md"
published_sql_language_sha="$(/usr/bin/shasum -a 256 "${published_sql_language_worker}" | /usr/bin/awk '{print $1}')"
if [[ "${published_sql_language_sha}" != "${sql_language_expected_sha}" ]]; then
    echo "The published SQL language worker does not match its build receipt." >&2
    exit 1
fi
if ! /usr/bin/cmp -s "${sql_language_receipt}" "${published_sql_language_receipt}"; then
    echo "The published SQL language worker receipt differs from the verified receipt." >&2
    exit 1
fi
published_sql_language_dependencies_sha="$(/usr/bin/shasum -a 256 "${published_sql_language_dependencies}" | /usr/bin/awk '{print $1}')"
published_sql_language_notices_sha="$(/usr/bin/shasum -a 256 "${published_sql_language_notices}" | /usr/bin/awk '{print $1}')"
if [[ "${published_sql_language_dependencies_sha}" != "${sql_language_expected_dependencies_sha}" \
    || "${published_sql_language_notices_sha}" != "${sql_language_expected_notices_sha}" ]]; then
    echo "The published SQL language worker legal files do not match its build receipt." >&2
    exit 1
fi

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
if [[ ! -x "${candidate}/Contents/MacOS/runtimes/osx-arm64/native/ghostshell-sql-language" ]]; then
    echo "The packaged SQL language worker is missing or not executable." >&2
    exit 1
fi
candidate_sql_language_directory="${candidate}/Contents/MacOS/runtimes/osx-arm64/native"
for required in \
    THIRD-PARTY-NOTICES.md \
    runtime-dependencies.txt \
    build-receipt.json; do
    if [[ ! -f "${candidate_sql_language_directory}/${required}" ]]; then
        echo "The packaged SQL language worker metadata is incomplete; missing ${required}." >&2
        exit 1
    fi
done
candidate_sql_language_sha="$(/usr/bin/shasum -a 256 "${candidate_sql_language_directory}/ghostshell-sql-language" | /usr/bin/awk '{print $1}')"
if [[ "${candidate_sql_language_sha}" != "${sql_language_expected_sha}" ]]; then
    echo "The packaged SQL language worker does not match its build receipt." >&2
    exit 1
fi
if ! /usr/bin/cmp -s \
    "${sql_language_receipt}" \
    "${candidate_sql_language_directory}/build-receipt.json"; then
    echo "The packaged SQL language worker receipt differs from the verified receipt." >&2
    exit 1
fi
candidate_sql_language_dependencies_sha="$(/usr/bin/shasum -a 256 "${candidate_sql_language_directory}/runtime-dependencies.txt" | /usr/bin/awk '{print $1}')"
candidate_sql_language_notices_sha="$(/usr/bin/shasum -a 256 "${candidate_sql_language_directory}/THIRD-PARTY-NOTICES.md" | /usr/bin/awk '{print $1}')"
if [[ "${candidate_sql_language_dependencies_sha}" != "${sql_language_expected_dependencies_sha}" \
    || "${candidate_sql_language_notices_sha}" != "${sql_language_expected_notices_sha}" ]]; then
    echo "The packaged SQL language worker legal files do not match its build receipt." >&2
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
