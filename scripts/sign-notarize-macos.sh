#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_dir="$(cd -- "${script_dir}/.." && pwd -P)"
entitlements="${repository_dir}/tools/GhostShell.Packaging/MacOS/Chromium.entitlements"
app=""
identity=""
notary_profile=""
evidence=""

usage() {
    cat >&2 <<'EOF'
Usage:
  ./scripts/sign-notarize-macos.sh \
    --app <path/to/GhostShell.app> \
    --identity <Developer ID Application identity> \
    [--notary-profile <notarytool keychain profile>] \
    [--evidence <outside-app/notarization.json>]

Signs the already-assembled managed-runtime native code, CEF framework, five
helper apps, and outer GhostShell bundle in nested-code order. If a notary
profile is provided, submits a temporary ZIP, staples the ticket, and validates
it. Use identity '-' only for a locally trusted ad-hoc development build;
ad-hoc builds cannot be notarized or distributed through a browser.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --app)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            app="$2"
            shift 2
            ;;
        --identity)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            identity="$2"
            shift 2
            ;;
        --notary-profile)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            notary_profile="$2"
            shift 2
            ;;
        --evidence)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            evidence="$2"
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

if [[ -z "${app}" || -z "${identity}" ]]; then
    usage
    exit 64
fi
if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "macOS signing requires a macOS host." >&2
    exit 1
fi
if [[ ! -d "${app}" || "$(basename "${app}")" != "GhostShell.app" ]]; then
    echo "--app must name an assembled GhostShell.app directory." >&2
    exit 1
fi
if [[ ! -f "${entitlements}" ]]; then
    echo "Chromium hardened-runtime entitlements are unavailable." >&2
    exit 1
fi
if [[ "${identity}" == "-" && -n "${notary_profile}" ]]; then
    echo "Ad-hoc signatures cannot be notarized." >&2
    exit 64
fi
if [[ -n "${notary_profile}" && -z "${evidence}" ]]; then
    echo "Notarized distribution requires a closed --evidence record." >&2
    exit 64
fi
if [[ -z "${notary_profile}" && -n "${evidence}" ]]; then
    echo "Signing evidence is valid only for a notarized distribution." >&2
    exit 64
fi
if [[ -n "${evidence}" ]]; then
    evidence="$(cd -- "$(dirname -- "${evidence}")" && pwd -P)/$(basename -- "${evidence}")"
    app_canonical="$(cd -- "$(dirname -- "${app}")" && pwd -P)/$(basename -- "${app}")"
    if [[ "${evidence}" == "${app_canonical}"/* || -e "${evidence}" ]]; then
        echo "Signing evidence must be a new file outside GhostShell.app." >&2
        exit 64
    fi
fi

frameworks="${app}/Contents/Frameworks"
cef_framework="${frameworks}/Chromium Embedded Framework.framework"
required_nested=(
    "${cef_framework}"
    "${frameworks}/libexclr8cef.dylib"
    "${app}/Contents/MacOS/libexclr8cef.dylib"
    "${app}/Contents/MacOS/libghostty-vt.dylib"
    "${frameworks}/GhostSHELL Helper.app"
    "${frameworks}/GhostSHELL Helper (Alerts).app"
    "${frameworks}/GhostSHELL Helper (GPU).app"
    "${frameworks}/GhostSHELL Helper (Plugin).app"
    "${frameworks}/GhostSHELL Helper (Renderer).app"
)
for nested in "${required_nested[@]}"; do
    if [[ ! -e "${nested}" ]]; then
        echo "The assembled bundle is missing nested code: $(basename "${nested}")." >&2
        exit 1
    fi
done

sign_plain() {
    local arguments=(--force)
    if [[ "${identity}" != "-" ]]; then
        arguments+=(--options runtime --timestamp)
    fi
    arguments+=(--sign "${identity}" "$1")
    /usr/bin/codesign "${arguments[@]}"
}

sign_chromium_bundle() {
    local arguments=(
        --force
        --entitlements "${entitlements}"
    )
    if [[ "${identity}" != "-" ]]; then
        arguments+=(--options runtime --timestamp)
    fi
    arguments+=(--sign "${identity}" "$1")
    /usr/bin/codesign "${arguments[@]}"
}

# Sign leaf Mach-O libraries before the framework and app bundles that contain
# them. `--deep` is intentionally avoided: it can silently apply the wrong
# entitlements to nested Chromium helpers.
while IFS= read -r -d '' library; do
    sign_plain "${library}"
done < <(find "${cef_framework}/Libraries" -type f \
    \( -name '*.dylib' -o -name '*.so' \) -print0)
sign_plain "${cef_framework}"

sign_plain "${frameworks}/libexclr8cef.dylib"
while IFS= read -r -d '' runtime_file; do
    if [[ "${runtime_file}" == "${app}/Contents/MacOS/GhostShell" ]]; then
        continue
    fi

    runtime_description="$(/usr/bin/file -b "${runtime_file}")"
    if [[ "${runtime_description}" == Mach-O* ]]; then
        sign_plain "${runtime_file}"
    fi
done < <(find "${app}/Contents/MacOS" -type f -print0)

for helper in \
    "${frameworks}/GhostSHELL Helper.app" \
    "${frameworks}/GhostSHELL Helper (Alerts).app" \
    "${frameworks}/GhostSHELL Helper (GPU).app" \
    "${frameworks}/GhostSHELL Helper (Plugin).app" \
    "${frameworks}/GhostSHELL Helper (Renderer).app"; do
    sign_chromium_bundle "${helper}"
done

sign_chromium_bundle "${app}"
/usr/bin/codesign --verify --deep --strict --verbose=2 "${app}"

if [[ -z "${notary_profile}" ]]; then
    exit 0
fi

notary_directory="$(mktemp -d "${TMPDIR:-/tmp}/ghostshell-notary.XXXXXX")"
notary_zip="${notary_directory}/GhostShell.zip"
evidence_staging=""
cleanup() {
    rm -rf -- "${notary_directory}"
    if [[ -n "${evidence_staging}" ]]; then
        rm -f -- "${evidence_staging}"
    fi
}
trap cleanup EXIT

/usr/bin/ditto -c -k --keepParent "${app}" "${notary_zip}"
notary_result="${notary_directory}/notary-result.json"
/usr/bin/xcrun notarytool submit \
    "${notary_zip}" \
    --keychain-profile "${notary_profile}" \
    --wait \
    --output-format json > "${notary_result}"
notarization_id="$(/usr/bin/plutil -extract id raw "${notary_result}")"
notarization_status="$(/usr/bin/plutil -extract status raw "${notary_result}")"
if [[ -z "${notarization_id}" || "${notarization_status}" != "Accepted" ]]; then
    echo "Apple notarization did not return an accepted closed result." >&2
    exit 1
fi
/usr/bin/xcrun stapler staple "${app}"
/usr/bin/xcrun stapler validate "${app}"
/usr/sbin/spctl --assess --type execute --verbose=2 "${app}"

codesign_details="${notary_directory}/codesign-details.txt"
/usr/bin/codesign --display --verbose=4 "${app}" 2> "${codesign_details}"
team_identifier="$(/usr/bin/sed -n 's/^TeamIdentifier=//p' "${codesign_details}")"
if [[ ! "${team_identifier}" =~ ^[A-Za-z0-9]{1,32}$ ]]; then
    echo "The signed application has no bounded TeamIdentifier." >&2
    exit 1
fi
certificate_prefix="${notary_directory}/certificate"
/usr/bin/codesign --display "--extract-certificates=${certificate_prefix}" "${app}"
if [[ ! -f "${certificate_prefix}0" ]]; then
    echo "The signing certificate could not be extracted." >&2
    exit 1
fi
certificate_sha256="$(/usr/bin/shasum -a 256 "${certificate_prefix}0" | /usr/bin/awk '{print $1}')"

evidence_staging="${evidence}.staging"
/usr/bin/plutil -create json "${evidence_staging}"
/usr/bin/plutil -insert schemaVersion -integer 1 "${evidence_staging}"
/usr/bin/plutil -insert format -string ghostshell-macos-signing-evidence-v1 "${evidence_staging}"
/usr/bin/plutil -insert notarizationId -string "${notarization_id}" "${evidence_staging}"
/usr/bin/plutil -insert notarizationStatus -string "${notarization_status}" "${evidence_staging}"
/usr/bin/plutil -insert teamIdentifier -string "${team_identifier}" "${evidence_staging}"
/usr/bin/plutil -insert certificateSha256 -string "${certificate_sha256}" "${evidence_staging}"
/usr/bin/plutil -insert codeSignatureValid -bool true "${evidence_staging}"
/usr/bin/plutil -insert stapleValid -bool true "${evidence_staging}"
/usr/bin/plutil -insert gatekeeperAccepted -bool true "${evidence_staging}"
/bin/mv "${evidence_staging}" "${evidence}"
evidence_staging=""
