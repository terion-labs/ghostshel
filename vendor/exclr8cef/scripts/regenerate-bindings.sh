#!/usr/bin/env bash
# Regenerate C# P/Invoke bindings from the shim's C ABI header
# (native/shim/exclr8cef.h) into src/Exclr8Cef/Generated/.
#
# Run this after changing the C ABI surface or when bumping CEF.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ_DIR="${REPO_ROOT}/src/Exclr8Cef"

cd "${PROJ_DIR}"
mkdir -p Generated

# ClangSharpPInvokeGenerator depends on libclang.dylib + libClangSharp.dylib,
# which are shipped in the runtime NuGet package
# (clangsharppinvokegenerator.<rid>). The tool's [DllImport("libclang")] uses
# bare dlopen, so we have to point DYLD_FALLBACK_LIBRARY_PATH at the package
# directory. SIP doesn't filter this for user processes.
case "$(uname -s)/$(uname -m)" in
  Darwin/arm64)  RID="osx-arm64" ;;
  Darwin/x86_64) RID="osx-x64" ;;
  Linux/x86_64)  RID="linux-x64" ;;
  Linux/aarch64) RID="linux-arm64" ;;
  *) RID="" ;;
esac

if [ -n "${RID}" ]; then
  LIBCLANG_DIR=$(find "${HOME}/.nuget/packages/clangsharppinvokegenerator.${RID}" \
                      -name "libclang.dylib" -o -name "libclang.so.*" \
                      2>/dev/null | head -1 | xargs -I{} dirname {})
  if [ -n "${LIBCLANG_DIR}" ]; then
    export DYLD_FALLBACK_LIBRARY_PATH="${LIBCLANG_DIR}:${DYLD_FALLBACK_LIBRARY_PATH:-/usr/local/lib:/usr/lib}"
    export LD_LIBRARY_PATH="${LIBCLANG_DIR}:${LD_LIBRARY_PATH:-}"
  fi
fi

# The NuGet-shipped libclang has no builtin-header path baked in, so
# freestanding includes like <stdint.h> fail unless we point it at the
# system clang's resource directory.
CLANG_BUILTIN_INC=""
if command -v clang >/dev/null 2>&1; then
  CLANG_BUILTIN_INC="$(clang -print-resource-dir)/include"
fi

# ClangSharpPInvokeGenerator is installed as a local tool via .config/dotnet-tools.json.
# Absolute paths are appended here so generate-bindings.rsp stays portable.
cd "${REPO_ROOT}"
dotnet ClangSharpPInvokeGenerator @"${PROJ_DIR}/generate-bindings.rsp" \
    --file "${REPO_ROOT}/native/shim/exclr8cef.h" \
    --include-directory "${REPO_ROOT}/native/shim" \
    ${CLANG_BUILTIN_INC:+--include-directory "${CLANG_BUILTIN_INC}"} \
    --output "${PROJ_DIR}/Generated"

echo
echo "Bindings regenerated at: src/Exclr8Cef/Generated/"
ls -l "${PROJ_DIR}/Generated/"
