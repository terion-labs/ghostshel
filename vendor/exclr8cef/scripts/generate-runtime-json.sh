#!/usr/bin/env bash
# Generate src/Exclr8Cef/runtime.json so that NuGet auto-resolves the
# correct runtime.<rid>.Exclr8Cef package per consumer RID. Run as part
# of the release workflow after the version is known.
#
#   $ scripts/generate-runtime-json.sh <version>

set -euo pipefail

if [ $# -lt 1 ]; then
  echo "usage: $0 <version>" >&2
  exit 1
fi

VERSION="$1"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${REPO_ROOT}/src/Exclr8Cef/runtime.json"

cat > "${OUT}" <<JSON
{
  "runtimes": {
    "osx-arm64": {
      "Exclr8Cef": { "runtime.osx-arm64.Exclr8Cef": "${VERSION}" }
    },
    "osx-x64": {
      "Exclr8Cef": { "runtime.osx-x64.Exclr8Cef": "${VERSION}" }
    },
    "win-x64": {
      "Exclr8Cef": { "runtime.win-x64.Exclr8Cef": "${VERSION}" }
    },
    "linux-x64": {
      "Exclr8Cef": { "runtime.linux-x64.Exclr8Cef": "${VERSION}" }
    },
    "linux-arm64": {
      "Exclr8Cef": { "runtime.linux-arm64.Exclr8Cef": "${VERSION}" }
    }
  }
}
JSON

echo "Wrote ${OUT}"
