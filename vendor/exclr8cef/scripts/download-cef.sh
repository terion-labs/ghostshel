#!/usr/bin/env bash
# Download the pinned CEF binary distribution from cef-builds.spotifycdn.com
# into third_party/cef/<platform>/.
#
# The pinned version is read from cef.json at the repo root. Bump via
# scripts/bump-cef.sh <new-version>.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CEF_JSON="${REPO_ROOT}/cef.json"
DEST_ROOT="${REPO_ROOT}/third_party/cef"

if [ ! -f "${CEF_JSON}" ]; then
  echo "cef.json not found at ${CEF_JSON}" >&2
  exit 1
fi

# Read pinned version from cef.json. We use python3 (preinstalled on
# macOS / Linux / Windows GitHub runners) instead of jq for portability.
CEF_VERSION="${CEF_VERSION:-$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "${CEF_JSON}")}"
DIST_TYPE="${DIST_TYPE:-minimal}"   # 'minimal' or 'standard' (with cefsimple/cefclient)

detect_platform() {
  local os arch
  case "$(uname -s)" in
    Darwin) os="macos" ;;
    Linux)  os="linux" ;;
    MINGW*|MSYS*|CYGWIN*) os="windows" ;;
    *) echo "Unsupported OS: $(uname -s)" >&2; exit 1 ;;
  esac
  case "$(uname -m)" in
    arm64|aarch64) arch="arm64" ;;
    x86_64|amd64)  arch="64" ;;
    *) echo "Unsupported arch: $(uname -m)" >&2; exit 1 ;;
  esac
  echo "${os}${arch}"
}

PLATFORM="${PLATFORM:-$(detect_platform)}"
EXT="tar.bz2"   # CEF 147+ ships .tar.bz2 on every platform incl. Windows

URL_VERSION="${CEF_VERSION//+/%2B}"
ARCHIVE_BASENAME="cef_binary_${URL_VERSION}_${PLATFORM}"
[ "${DIST_TYPE}" = "minimal" ] && ARCHIVE_BASENAME="${ARCHIVE_BASENAME}_minimal"
URL="https://cef-builds.spotifycdn.com/${ARCHIVE_BASENAME}.${EXT}"

DEST="${DEST_ROOT}/${PLATFORM}"

cat <<EOF
CEF version : ${CEF_VERSION}
Platform    : ${PLATFORM}
Distribution: ${DIST_TYPE}
URL         : ${URL}
Destination : ${DEST}
EOF
echo

if [ -e "${DEST}/include/cef_version.h" ]; then
  echo "CEF already extracted at ${DEST}. Delete it to redownload."
  exit 0
fi

mkdir -p "${DEST_ROOT}"
TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

echo "Downloading..."
curl -fL --progress-bar -o "${TMP}/cef.${EXT}" "${URL}"

echo "Extracting..."
tar -xjf "${TMP}/cef.${EXT}" -C "${TMP}"

EXTRACTED="$(find "${TMP}" -maxdepth 1 -mindepth 1 -type d -name 'cef_binary_*' | head -1)"
if [ -z "${EXTRACTED}" ]; then
  echo "Could not find extracted CEF directory in ${TMP}" >&2
  exit 1
fi

mkdir -p "${DEST}"
if command -v rsync >/dev/null 2>&1; then
  rsync -a "${EXTRACTED}/" "${DEST}/"
else
  cp -a "${EXTRACTED}/." "${DEST}/"
fi

echo
echo "CEF extracted to ${DEST}"
echo "Headers : ${DEST}/include"
echo "Binaries: ${DEST}/Release"
echo
echo "Next:  cmake -S native -B native/build && cmake --build native/build"
