#!/usr/bin/env bash
# Compare the version pinned in cef.json against the latest stable on
# Spotify's CEF build CDN. Prints both versions; exits 0 if they match,
# 1 if a newer stable is available (intended for use as a CI gate that
# decides whether to open a bump PR).
#
#   $ scripts/check-cef-upstream.sh
#   pinned     : 150.0.9+g81b0088+chromium-150.0.7871.46
#   upstream   : 150.0.9+g81b0088+chromium-150.0.7871.46
#   ✓ up to date
#
# Set GITHUB_OUTPUT (or call with --github-output) to emit the version
# to GitHub Actions outputs.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CEF_JSON="${REPO_ROOT}/cef.json"
INDEX_URL="https://cef-builds.spotifycdn.com/index.json"
# Upstream version is determined from this platform's stable channel —
# all platforms publish the same version simultaneously, so any will do.
PROBE_PLATFORM="${PROBE_PLATFORM:-macosarm64}"

PINNED="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "${CEF_JSON}")"

UPSTREAM="$(curl -fsSL --max-time 30 "${INDEX_URL}" \
  | python3 -c '
import json, sys
data = json.load(sys.stdin)
plat = sys.argv[1]
versions = data.get(plat, {}).get("versions", [])
stables = [v for v in versions if v.get("channel") == "stable"]
if not stables:
    sys.exit(2)
# The index is date-ordered, not version-ordered, and Spotify keeps
# publishing point releases for older extended branches (e.g. 144.x
# after 148.x shipped) — so "first stable in the list" can be an old
# branch. Pick the highest version number instead. cef_version looks
# like "148.0.10+g7ee53f5+chromium-148.0.7778.218"; compare the
# numeric CEF triple, tie-break on the chromium build number.
def key(v):
    cv = v["cef_version"]
    cef = tuple(int(x) for x in cv.split("+")[0].split("."))
    chromium = tuple(int(x) for x in cv.rsplit("chromium-", 1)[1].split("."))
    return (cef, chromium)
print(max(stables, key=key)["cef_version"])
' "${PROBE_PLATFORM}")"

echo "pinned    : ${PINNED}"
echo "upstream  : ${UPSTREAM}"

# Version-aware comparison: only flag a bump when upstream is strictly
# NEWER than the pin. A plain string-inequality would also fire when the
# pin is ahead of the newest stable (e.g. a beta pin), making CI open a
# downgrade PR.
NEEDS_BUMP="$(python3 -c '
import sys
def key(cv):
    cef = tuple(int(x) for x in cv.split("+")[0].split("."))
    chromium = tuple(int(x) for x in cv.rsplit("chromium-", 1)[1].split("."))
    return (cef, chromium)
print("true" if key(sys.argv[2]) > key(sys.argv[1]) else "false")
' "${PINNED}" "${UPSTREAM}")"

# Emit GitHub Actions outputs if running in CI.
if [ -n "${GITHUB_OUTPUT:-}" ]; then
  {
    echo "pinned=${PINNED}"
    echo "upstream=${UPSTREAM}"
    echo "needs_bump=${NEEDS_BUMP}"
  } >> "${GITHUB_OUTPUT}"
fi

if [ "${NEEDS_BUMP}" = "false" ]; then
  echo "✓ up to date"
  exit 0
fi
echo "↑ upstream is newer"
exit 1
