#!/usr/bin/env bash
# Stage native artifacts for the runtime NuGet package of a given RID.
#
#   $ scripts/stage-native-files.sh <rid> <staging-dir>
#
# After CI builds native/ for a platform, this script copies the right
# files into <staging-dir>/runtimes/<rid>/native/ for `dotnet pack` of
# src/runtime/runtime.csproj.
#
# RID -> CEF platform mapping:
#   osx-arm64    macosarm64
#   osx-x64      macosx64
#   win-x64      windows64
#   linux-x64    linux64
#   linux-arm64  linuxarm64

set -euo pipefail

if [ $# -lt 2 ]; then
  echo "usage: $0 <rid> <staging-dir>" >&2
  exit 1
fi

RID="$1"
STAGING="$2"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

case "${RID}" in
  osx-arm64)   CEF_PLATFORM="macosarm64";  SHIM_LIB="libexclr8cef.dylib" ;;
  osx-x64)     CEF_PLATFORM="macosx64";    SHIM_LIB="libexclr8cef.dylib" ;;
  win-x64)     CEF_PLATFORM="windows64";   SHIM_LIB="exclr8cef.dll" ;;
  win-arm64)   CEF_PLATFORM="windowsarm64";SHIM_LIB="exclr8cef.dll" ;;
  linux-x64)   CEF_PLATFORM="linux64";     SHIM_LIB="libexclr8cef.so" ;;
  linux-arm64) CEF_PLATFORM="linuxarm64";  SHIM_LIB="libexclr8cef.so" ;;
  *) echo "unknown RID: ${RID}" >&2; exit 1 ;;
esac

CEF_ROOT="${REPO_ROOT}/third_party/cef/${CEF_PLATFORM}"
CEF_RELEASE="${CEF_ROOT}/Release"
CEF_RESOURCES="${CEF_ROOT}/Resources"
NATIVE_BUILD="${REPO_ROOT}/native/build/shim"
DEST="${STAGING}/runtimes/${RID}/native"

mkdir -p "${DEST}"
echo "Staging ${RID} → ${DEST}"

# --- Shim shared library --------------------------------------------------
if [ -f "${NATIVE_BUILD}/${SHIM_LIB}" ]; then
  cp "${NATIVE_BUILD}/${SHIM_LIB}" "${DEST}/"
elif [ -f "${NATIVE_BUILD}/Release/${SHIM_LIB}" ]; then
  # Multi-config generators (MSVC) put output in Release/.
  cp "${NATIVE_BUILD}/Release/${SHIM_LIB}" "${DEST}/"
else
  echo "shim library not found: ${NATIVE_BUILD}/${SHIM_LIB}" >&2
  exit 1
fi

# --- Per-platform CEF binaries --------------------------------------------
case "${RID}" in
  osx-*)
    # macOS: copy the framework bundle + helper apps.
    cp -R "${CEF_RELEASE}/Chromium Embedded Framework.framework" "${DEST}/"
    ;;
  win-*)
    # Windows: libcef.dll + chrome_elf.dll + ICU + locale paks.
    cp "${CEF_RELEASE}"/*.dll "${DEST}/"
    cp "${CEF_RELEASE}"/*.bin "${DEST}/" 2>/dev/null || true
    cp "${CEF_RELEASE}"/*.dat "${DEST}/" 2>/dev/null || true
    if [ -d "${CEF_RESOURCES}" ]; then
      cp -R "${CEF_RESOURCES}"/* "${DEST}/"
    fi
    ;;
  linux-*)
    # Linux: libcef.so + chrome_sandbox + ICU + locale paks.
    cp "${CEF_RELEASE}"/*.so "${DEST}/" 2>/dev/null || true
    cp "${CEF_RELEASE}"/chrome-sandbox "${DEST}/" 2>/dev/null || true
    cp "${CEF_RELEASE}"/*.bin "${DEST}/" 2>/dev/null || true
    cp "${CEF_RELEASE}"/*.dat "${DEST}/" 2>/dev/null || true
    if [ -d "${CEF_RESOURCES}" ]; then
      cp -R "${CEF_RESOURCES}"/* "${DEST}/"
    fi
    # Aggressive trim — the full Linux CEF dist + libv8 etc. pushes the nupkg
    # past nuget.org's 250 MB hard limit. Drop non-essential pieces:
    #   - non-en-US locales (UI strings only)
    #   - libvk_swiftshader.so + libvulkan.so (software-Vulkan fallback,
    #     rarely used in OSR mode)
    #   - chrome_200_percent.pak (HiDPI 2x UI assets)
    # Hosts that need any of these can drop them in alongside the runtime
    # NuGet contents at deploy time.
    if [ -d "${DEST}/locales" ]; then
      find "${DEST}/locales" -type f -name '*.pak' ! -name 'en-US.pak' -delete
    fi
    rm -f "${DEST}/libvk_swiftshader.so" \
          "${DEST}/libvulkan.so" \
          "${DEST}/chrome_200_percent.pak"
    # CEF's Linux distribution ships libcef.so / libGLESv2.so / libEGL.so with
    # debug symbols; stripping cuts ~70–80% off libcef.so (1.3 GB → ~200 MB).
    # Required to fit nuget.org's 250 MB per-package cap.
    if command -v strip >/dev/null 2>&1; then
      for so in "${DEST}"/*.so; do
        [ -f "$so" ] || continue
        strip --strip-unneeded "$so" 2>/dev/null || true
      done
    fi
    ;;
esac

echo "Staged files (top 10 by size):"
# Per-file sizes help diagnose nuget.org 250 MB cap issues fast.
# `sort -h` exits early; `|| true` swallows the resulting SIGPIPE under pipefail.
du -h "${DEST}"/* "${DEST}"/locales/* 2>/dev/null | sort -hr | head -10 || true
echo
echo "Total staged size: $(du -sh "${DEST}" | cut -f1)"
