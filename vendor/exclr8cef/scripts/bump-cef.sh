#!/usr/bin/env bash
# Update the pinned CEF version in cef.json. Used by the upstream-check
# CI workflow which then opens a PR with the bumped pin.
#
#   $ scripts/bump-cef.sh "147.0.11+abc1234+chromium-147.0.7727.119"

set -euo pipefail

if [ $# -lt 1 ]; then
  echo "usage: $0 <new-cef-version>" >&2
  exit 1
fi

NEW_VERSION="$1"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CEF_JSON="${REPO_ROOT}/cef.json"

if [ ! -f "${CEF_JSON}" ]; then
  echo "cef.json not found at ${CEF_JSON}" >&2
  exit 1
fi

OLD_VERSION="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "${CEF_JSON}")"

if [ "${OLD_VERSION}" = "${NEW_VERSION}" ]; then
  echo "Already at ${NEW_VERSION}; nothing to do."
  exit 0
fi

# Rewrite cef.json with the new version, preserving the rest of the structure.
python3 - "${CEF_JSON}" "${NEW_VERSION}" <<'PY'
import json, sys
path, new_ver = sys.argv[1], sys.argv[2]
with open(path) as f:
    data = json.load(f)
data["version"] = new_ver
with open(path, "w") as f:
    json.dump(data, f, indent=2)
    f.write("\n")
PY

echo "bumped: ${OLD_VERSION}"
echo "    →  ${NEW_VERSION}"
