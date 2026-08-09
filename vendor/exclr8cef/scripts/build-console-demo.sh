#!/usr/bin/env bash
# Publish Exclr8Cef.ConsoleDemo and assemble a macOS .app bundle that
# embeds the CEF framework + helper subprocesses (reused from the native
# Stage 2 demo build). End result: a double-clickable .app that, when run,
# opens chrome://version via Exclr8CEF, driven entirely from .NET.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ="${REPO_ROOT}/samples/Exclr8Cef.ConsoleDemo"
NATIVE_DEMO="${REPO_ROOT}/native/build/demo/Release/exclr8cef_demo.app"
SHIM_DYLIB="${REPO_ROOT}/native/build/shim/libexclr8cef.dylib"

if [ ! -d "${NATIVE_DEMO}" ]; then
  echo "Native demo bundle not found at ${NATIVE_DEMO}" >&2
  echo "Run: cmake --build native/build" >&2
  exit 1
fi

if [ ! -f "${SHIM_DYLIB}" ]; then
  echo "Shim dylib not found at ${SHIM_DYLIB}" >&2
  exit 1
fi

# Detect host RID.
case "$(uname -s)/$(uname -m)" in
  Darwin/arm64)  RID="osx-arm64" ;;
  Darwin/x86_64) RID="osx-x64" ;;
  *) echo "This bundling script targets macOS only" >&2; exit 1 ;;
esac

echo "==> Publishing .NET app for ${RID}..."
dotnet publish "${PROJ}" \
    -c Release \
    -r "${RID}" \
    --self-contained true \
    -p:PublishSingleFile=false \
    /nologo

PUBLISH_DIR="${PROJ}/bin/Release/net10.0/${RID}/publish"
if [ ! -d "${PUBLISH_DIR}" ]; then
  echo "Expected publish directory not found: ${PUBLISH_DIR}" >&2
  exit 1
fi

APP="${PROJ}/bin/Release/Exclr8Cef.ConsoleDemo.app"
echo "==> Assembling .app bundle at ${APP}"
rm -rf "${APP}"
mkdir -p "${APP}/Contents/MacOS" "${APP}/Contents/Frameworks"

# .NET self-contained publish output -> Contents/MacOS
cp -a "${PUBLISH_DIR}/." "${APP}/Contents/MacOS/"

# Generate Info.plist. NSPrincipalClass is the CEF NSApplication subclass
# defined in the shim (matches the C demo's plist).
cat > "${APP}/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>      <string>en</string>
  <key>CFBundleDisplayName</key>            <string>Exclr8Cef Console Demo</string>
  <key>CFBundleExecutable</key>             <string>Exclr8Cef.ConsoleDemo</string>
  <key>CFBundleIdentifier</key>             <string>com.exclr8.cef.consoledemo</string>
  <key>CFBundleInfoDictionaryVersion</key>  <string>6.0</string>
  <key>CFBundleName</key>                   <string>Exclr8Cef Console Demo</string>
  <key>CFBundlePackageType</key>            <string>APPL</string>
  <key>CFBundleShortVersionString</key>     <string>0.3.0</string>
  <key>CFBundleSignature</key>              <string>????</string>
  <key>CFBundleVersion</key>                <string>0.3.0</string>
  <key>LSEnvironment</key>
  <dict>
    <key>MallocNanoZone</key>               <string>0</string>
  </dict>
  <key>LSMinimumSystemVersion</key>         <string>12.0</string>
  <key>NSPrincipalClass</key>               <string>Exclr8CefApplication</string>
  <key>NSHighResolutionCapable</key>        <true/>
  <key>NSSupportsAutomaticGraphicsSwitching</key>  <true/>
</dict>
</plist>
PLIST

# Copy CEF framework + all 5 helper bundles + shim dylib into Frameworks/.
echo "==> Copying CEF framework + helpers + shim..."
cp -R "${NATIVE_DEMO}/Contents/Frameworks/Chromium Embedded Framework.framework" "${APP}/Contents/Frameworks/"
for HELPER in "${NATIVE_DEMO}/Contents/Frameworks/"*"Helper"*.app; do
  cp -R "${HELPER}" "${APP}/Contents/Frameworks/"
done
cp "${SHIM_DYLIB}" "${APP}/Contents/Frameworks/"

# Move the shim dylib next to the .NET binaries too — DllImport("exclr8cef")
# resolves to <exe>/libexclr8cef.dylib. The publish step puts a copy there
# already (via the Exclr8Cef.SmokeTest pattern), but be safe.
cp "${SHIM_DYLIB}" "${APP}/Contents/MacOS/" || true

echo
echo "Built: ${APP}"
echo
echo "Run:   open \"${APP}\""
echo "Or:    \"${APP}/Contents/MacOS/Exclr8Cef.ConsoleDemo\""
