#!/usr/bin/env bash
set -euo pipefail

original_class="ExtensionDropdownHandler"
namespaced_class="AvnFileTypeDropdownClass"
expected_occurrences=32

usage() {
    echo "Usage: ./scripts/namespace-avalonia-native-macos.sh <libAvaloniaNative.dylib>" >&2
}

if [[ $# -ne 1 ]]; then
    usage
    exit 64
fi
if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "The Avalonia Native Objective-C namespace fix requires macOS." >&2
    exit 1
fi

dylib="$1"
if [[ ! -f "${dylib}" || -L "${dylib}" ]]; then
    echo "The Avalonia Native library is missing or linked: ${dylib}" >&2
    exit 1
fi

# Avalonia Native and Chromium both ship Chromium's file-dialog helper under
# the process-global Objective-C name ExtensionDropdownHandler. Rewrite only
# the copied application payload. The equal-length name keeps every Mach-O
# offset intact; the exact occurrence count makes an Avalonia binary change a
# fail-closed review event instead of silently patching an unknown library.
/usr/bin/python3 - "${dylib}" "${original_class}" "${namespaced_class}" \
    "${expected_occurrences}" <<'PY'
import os
from pathlib import Path
import stat
import sys
import tempfile

path = Path(sys.argv[1])
original = sys.argv[2].encode("ascii")
namespaced = sys.argv[3].encode("ascii")
expected = int(sys.argv[4])

if len(original) != len(namespaced):
    raise SystemExit("Objective-C class names must have identical byte lengths.")

source = path.read_bytes()
original_count = source.count(original)
namespaced_count = source.count(namespaced)
if original_count == 0 and namespaced_count == expected:
    raise SystemExit(0)
if original_count != expected or namespaced_count != 0:
    raise SystemExit(
        "Avalonia Native Objective-C metadata differs from the reviewed binary "
        f"(original={original_count}, namespaced={namespaced_count}, expected={expected})."
    )

mode = stat.S_IMODE(path.stat().st_mode)
temporary_name = None
try:
    with tempfile.NamedTemporaryFile(
        mode="wb",
        prefix=f".{path.name}.namespace.",
        dir=path.parent,
        delete=False,
    ) as temporary:
        temporary_name = temporary.name
        temporary.write(source.replace(original, namespaced))
        temporary.flush()
        os.fsync(temporary.fileno())
    os.chmod(temporary_name, mode)
    os.replace(temporary_name, path)
    temporary_name = None
finally:
    if temporary_name is not None:
        Path(temporary_name).unlink(missing_ok=True)
PY

# The byte rewrite invalidates Avalonia's embedded ad-hoc signature. Preserve
# its identity while producing a valid development signature; release signing
# later replaces this signature with the configured distribution identity.
if ! /usr/bin/codesign \
        --force \
        --sign - \
        --preserve-metadata=identifier,requirements,flags \
        "${dylib}" >/dev/null 2>&1; then
    echo "The namespaced Avalonia Native library could not be re-signed." >&2
    exit 1
fi
/usr/bin/codesign --verify --strict "${dylib}"

/usr/bin/python3 - "${dylib}" "${original_class}" "${namespaced_class}" \
    "${expected_occurrences}" <<'PY'
from pathlib import Path
import sys

payload = Path(sys.argv[1]).read_bytes()
original = sys.argv[2].encode("ascii")
namespaced = sys.argv[3].encode("ascii")
expected = int(sys.argv[4])
if payload.count(original) != 0 or payload.count(namespaced) != expected:
    raise SystemExit("The Avalonia Native Objective-C namespace fix did not verify.")
PY
