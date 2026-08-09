#!/usr/bin/env bash
# Publish Exclr8Cef.WebView.Demo (Avalonia) and assemble a macOS .app
# bundle that embeds the CEF framework + helper subprocesses + the
# Exclr8CEF shim. The result is a double-clickable .app showing an
# Avalonia window with an embedded Chromium browser.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ="${REPO_ROOT}/samples/Exclr8Cef.WebView.Demo"
NATIVE_DEMO="${REPO_ROOT}/native/build/demo/Release/exclr8cef_demo.app"
SHIM_DYLIB="${REPO_ROOT}/native/build/shim/libexclr8cef.dylib"

if [ ! -d "${NATIVE_DEMO}" ]; then
  echo "Native demo bundle not found at ${NATIVE_DEMO}" >&2
  echo "Run: cmake --build native/build" >&2
  exit 1
fi

# Always re-link the native shim before bundling. Skipping this was a
# repeated trap when adding a new C ABI symbol: the dotnet build picks up
# the regenerated bindings (which reference the new entry point), the
# bundle assembly copies the stale dylib, and the app crashes at startup
# with EntryPointNotFoundException. ninja is incremental so the cost is
# trivial when nothing changed.
echo "==> Rebuilding native shim (incremental)..."
cmake --build "${REPO_ROOT}/native/build" --target exclr8cef --parallel >/dev/null

case "$(uname -s)/$(uname -m)" in
  Darwin/arm64)  RID="osx-arm64" ;;
  Darwin/x86_64) RID="osx-x64" ;;
  *) echo "This bundling script targets macOS only" >&2; exit 1 ;;
esac

echo "==> Publishing Avalonia app for ${RID}..."
dotnet publish "${PROJ}" \
    -c Release \
    -r "${RID}" \
    --self-contained true \
    -p:PublishSingleFile=false \
    /nologo

PUBLISH_DIR="${PROJ}/bin/Release/net10.0/${RID}/publish"
[ -d "${PUBLISH_DIR}" ] || { echo "publish dir missing: ${PUBLISH_DIR}" >&2; exit 1; }

APP="${PROJ}/bin/Release/Exclr8Cef.WebView.Demo.app"
echo "==> Assembling .app bundle at ${APP}"
rm -rf "${APP}"
mkdir -p "${APP}/Contents/MacOS" "${APP}/Contents/Frameworks"

cp -a "${PUBLISH_DIR}/." "${APP}/Contents/MacOS/"

cat > "${APP}/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>      <string>en</string>
  <key>CFBundleDisplayName</key>            <string>Exclr8CEF Avalonia Demo</string>
  <key>CFBundleExecutable</key>             <string>Exclr8Cef.WebView.Demo</string>
  <key>CFBundleIdentifier</key>             <string>com.exclr8.cef.avaloniademo</string>
  <key>CFBundleInfoDictionaryVersion</key>  <string>6.0</string>
  <key>CFBundleName</key>                   <string>Exclr8CEF Avalonia Demo</string>
  <key>CFBundlePackageType</key>            <string>APPL</string>
  <key>CFBundleShortVersionString</key>     <string>0.4.0</string>
  <key>CFBundleSignature</key>              <string>????</string>
  <key>CFBundleVersion</key>                <string>0.4.0</string>
  <key>LSEnvironment</key>
  <dict>
    <key>MallocNanoZone</key>               <string>0</string>
  </dict>
  <key>LSMinimumSystemVersion</key>         <string>12.0</string>
  <key>NSPrincipalClass</key>               <string>Exclr8CefApplication</string>
  <key>NSHighResolutionCapable</key>        <true/>
  <key>NSSupportsAutomaticGraphicsSwitching</key>  <true/>
  <key>NSCameraUsageDescription</key>       <string>Demonstrates CefPermissionHandler camera prompts.</string>
  <key>NSMicrophoneUsageDescription</key>   <string>Demonstrates CefPermissionHandler microphone prompts.</string>
  <key>NSLocationUsageDescription</key>     <string>Demonstrates CefPermissionHandler geolocation prompts.</string>
</dict>
</plist>
PLIST

echo "==> Copying CEF framework + helpers + shim..."
cp -R "${NATIVE_DEMO}/Contents/Frameworks/Chromium Embedded Framework.framework" "${APP}/Contents/Frameworks/"
for HELPER in "${NATIVE_DEMO}/Contents/Frameworks/"*"Helper"*.app; do
  cp -R "${HELPER}" "${APP}/Contents/Frameworks/"
done
cp "${SHIM_DYLIB}" "${APP}/Contents/Frameworks/"
cp "${SHIM_DYLIB}" "${APP}/Contents/MacOS/" || true

echo
echo "Built: ${APP}"
echo
echo "Run:   open \"${APP}\""
