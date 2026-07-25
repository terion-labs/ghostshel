#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
repository_dir="$(cd "${script_dir}/.." && pwd -P)"
cd -- "${repository_dir}"
dependencies_dir="${repository_dir}/.deps"
ghostty_dir="${dependencies_dir}/ghostty"
artifact_parent_dir="${repository_dir}/native/artifacts"
artifact_destination_dir="${artifact_parent_dir}/osx-arm64"
component_catalog="${repository_dir}/licenses/native-macos-components.json"
dotnet="${repository_dir}/.dotnet/dotnet"
zig_version="0.15.2"
ghostty_tag="v1.3.1"
ghostty_commit="332b2aefc6e72d363aa93ab6ecfc86eeeeb5ed28"
ghostty_archive_sha="105f63ec2df9b53cd5dd1f685434d1924163fd4bcf23ecb1b07df343e79d2077"
metallib_sha="6893dea958b8d89b58c0ccefb1bfdb589ba4bb0c6fd1a0d73fe38a1715650918"

native_build_run_dir="$(mktemp -d "${TMPDIR:-/tmp}/ghostshell-native-build.XXXXXX")"
artifact_staging_parent=""
ghostty_install_dir="${native_build_run_dir}/install"
zig_local_cache_dir="${native_build_run_dir}/zig-local-cache"
zig_build_trace="${native_build_run_dir}/zig-build-trace.log"
zig_global_cache_dir="${dependencies_dir}/zig-global-cache"

cleanup_native_build_run() {
    rm -rf -- "${native_build_run_dir}"
    if [[ -n "${artifact_staging_parent}" ]]; then
        rm -rf -- "${artifact_staging_parent}"
    fi
}
trap cleanup_native_build_run EXIT

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "The full Ghostty renderer is currently built only on macOS." >&2
    exit 1
fi

if [[ "$(uname -m)" != "arm64" ]]; then
    echo "This bootstrap currently packages the macOS arm64 Ghostty runtime only." >&2
    exit 1
fi

if [[ ! -x "${dotnet}" ]]; then
    echo "Run ./scripts/bootstrap.sh to install the workspace .NET SDK." >&2
    exit 1
fi

if [[ ! -f "${component_catalog}" ]]; then
    echo "The reviewed native macOS component catalog is missing." >&2
    exit 1
fi

if [[ -L "${repository_dir}/native" ||
      -L "${artifact_parent_dir}" ||
      ! -d "${artifact_parent_dir}" ]]; then
    echo "The native artifact parent must be an existing real directory." >&2
    exit 1
fi

artifact_staging_parent="$(
    mktemp -d "${artifact_parent_dir}/.ghostshell-native-artifacts.XXXXXX"
)"
artifact_dir="${artifact_staging_parent}/osx-arm64"
receipt="${artifact_dir}/native-macos-build-receipt.json"

mkdir -p "${dependencies_dir}/toolchains"
mkdir -p "${ghostty_install_dir}" "${zig_local_cache_dir}" "${zig_global_cache_dir}"
mkdir "${artifact_dir}"

zig_archive="${dependencies_dir}/toolchains/zig-aarch64-macos-${zig_version}.tar.xz"
zig_dir="${dependencies_dir}/toolchains/zig-aarch64-macos-${zig_version}"
zig="${zig_dir}/zig"
if [[ ! -x "${zig}" ]]; then
    curl -fL "https://ziglang.org/download/${zig_version}/zig-aarch64-macos-${zig_version}.tar.xz" -o "${zig_archive}"
    echo "3cc2bab367e185cdfb27501c4b30b1b0653c28d9f73df8dc91488e66ece5fa6b  ${zig_archive}" | shasum -a 256 -c -
    tar -xJf "${zig_archive}" -C "${dependencies_dir}/toolchains"
fi
echo "c65cd34917923f575448cc0603dd7c2326da0af0e5c323043d090662dcdf351c  ${zig}" | shasum -a 256 -c -

if [[ ! -d "${ghostty_dir}/.git" ]]; then
    git clone --depth 1 --branch "${ghostty_tag}" https://github.com/ghostty-org/ghostty.git "${ghostty_dir}"
fi

actual_ghostty_commit="$(git -C "${ghostty_dir}" rev-parse HEAD)"
if [[ "${actual_ghostty_commit}" != "${ghostty_commit}" ]]; then
    echo "Expected Ghostty ${ghostty_commit}, found ${actual_ghostty_commit}." >&2
    exit 1
fi

ghostty_patch="${repository_dir}/native/ghostty/patches/0001-macos-dynamic-embedding.patch"
echo "84bd30325e39c742b26e45327670202f44928f4c10c3f59cf87218c8c66d2fe4  ${ghostty_patch}" | shasum -a 256 -c -
if git -C "${ghostty_dir}" apply --check "${ghostty_patch}" 2>/dev/null; then
    git -C "${ghostty_dir}" apply "${ghostty_patch}"
elif git -C "${ghostty_dir}" apply --reverse --check "${ghostty_patch}" 2>/dev/null; then
    :
else
    echo "The pinned Ghostty source has unexpected local changes." >&2
    exit 1
fi

expected_changes=$'build.zig\ninclude/ghostty.h\nsrc/apprt/embedded.zig\nsrc/build/MetallibStep.zig\nsrc/build/SharedDeps.zig'
actual_changes="$(git -C "${ghostty_dir}" diff --name-only HEAD | LC_ALL=C sort)"
if [[ "${actual_changes}" != "${expected_changes}" ]]; then
    echo "The pinned Ghostty checkout contains changes outside the reviewed patch." >&2
    exit 1
fi
untracked_sources="$(git -C "${ghostty_dir}" ls-files --others --exclude-standard | LC_ALL=C sort)"
if [[ -n "${untracked_sources}" ]]; then
    echo "The pinned Ghostty checkout contains unexpected untracked sources." >&2
    exit 1
fi

echo "171274764f6fd6adca965510f773a8b3a3d201ce983fe3c418472b4237d5bbc0  ${ghostty_dir}/build.zig" | shasum -a 256 -c -
echo "1dc70450ddf505d828cc2e874a73e1bcd2c66677530e9e13b93a3c4c7fe1b645  ${ghostty_dir}/include/ghostty.h" | shasum -a 256 -c -
echo "b1468cd772ccab4967be57da015298e9744a87d84706df227a1cfafd24fe42d1  ${ghostty_dir}/src/apprt/embedded.zig" | shasum -a 256 -c -
echo "e44fbfe05fe97a012941b24ae98595dadf6c548f2934f28a058dabead4d8d7c6  ${ghostty_dir}/src/build/MetallibStep.zig" | shasum -a 256 -c -
echo "43aa5f61985b53e40e98ec6c20d49543ea4c9d585ee2943855fab71952a99a92  ${ghostty_dir}/src/build/SharedDeps.zig" | shasum -a 256 -c -
echo "991e650ab7d334b9521d53816f528cb269bc92a3514edacb4ce0d73150ad6bbe  ${ghostty_dir}/build.zig.zon" | shasum -a 256 -c -

release_archive="${dependencies_dir}/ghostty-macos-universal-${ghostty_tag#v}.zip"
if [[ ! -f "${release_archive}" ]] ||
   ! echo "${ghostty_archive_sha}  ${release_archive}" | shasum -a 256 -c - >/dev/null 2>&1; then
    curl -fL "https://release.files.ghostty.org/${ghostty_tag#v}/ghostty-macos-universal.zip" -o "${release_archive}"
fi
echo "${ghostty_archive_sha}  ${release_archive}" | shasum -a 256 -c -

release_dir="${dependencies_dir}/ghostty-release-${ghostty_tag#v}"
release_binary="${release_dir}/Ghostty.app/Contents/MacOS/ghostty"
release_arm64_binary="${release_dir}/ghostty-arm64"
metallib="${dependencies_dir}/Ghostty-${ghostty_tag#v}.metallib"
if [[ ! -f "${metallib}" ]] ||
   ! echo "${metallib_sha}  ${metallib}" | shasum -a 256 -c - >/dev/null 2>&1; then
    mkdir -p "${release_dir}"
    unzip -qo "${release_archive}" "Ghostty.app/Contents/MacOS/ghostty" -d "${release_dir}"
    lipo "${release_binary}" -thin arm64 -output "${release_arm64_binary}"
    dd if="${release_arm64_binary}" of="${metallib}" bs=1 skip=13283328 count=52333 status=none
fi
echo "${metallib_sha}  ${metallib}" | shasum -a 256 -c -
echo "5256fb2bee5744109c54c76e595c500137919baa650db55a215da870a2c7d3a5  ${release_arm64_binary}" | shasum -a 256 -c -

macos_sdk="$(/usr/bin/xcrun --sdk macosx --show-sdk-path)"
if ! head -n 5 "${macos_sdk}/usr/lib/libSystem.tbd" | grep -q "arm64-macos"; then
    fallback_sdk="/Library/Developer/CommandLineTools/SDKs/MacOSX15.4.sdk"
    if [[ ! -f "${fallback_sdk}/usr/lib/libSystem.tbd" ]]; then
        echo "No macOS SDK compatible with Zig ${zig_version} was found." >&2
        exit 1
    fi
    macos_sdk="${fallback_sdk}"
fi

clang="$(/usr/bin/xcrun -f clang)"
clang_version="$("${clang}" --version | head -n 1)"
sdk_identity="$(basename "${macos_sdk}")"
ghostty_options=(
    -Dapp-runtime=none
    -Demit-docs=false
    -Demit-themes=true
    -Demit-xcframework=false
    -Demit-macos-app=false
    -Doptimize=ReleaseFast
    -Dsentry=false
)
PATH="${repository_dir}/scripts/toolchain-shims:${PATH}" \
GHOSTSHELL_MACOS_SDK="${macos_sdk}" \
GHOSTTY_PRECOMPILED_METALLIB="${metallib}" \
    "${zig}" build \
        --build-file "${ghostty_dir}/build.zig" \
        "${ghostty_options[@]}" \
        --prefix "${ghostty_install_dir}" \
        --cache-dir "${zig_local_cache_dir}" \
        --global-cache-dir "${zig_global_cache_dir}" \
        --seed 0 \
        --verbose \
        --summary all \
        -j1 2>&1 | tee "${zig_build_trace}"

cp "${ghostty_install_dir}/lib/libghostty.dylib" "${artifact_dir}/libghostty.dylib"
cp "${ghostty_dir}/LICENSE" "${artifact_dir}/GHOSTTY-LICENSE"
mkdir -p "${artifact_dir}/ghostty"
rsync -a --delete "${ghostty_install_dir}/share/ghostty/" "${artifact_dir}/ghostty/"
mkdir -p "${artifact_dir}/terminfo"
rsync -a --delete "${ghostty_install_dir}/share/terminfo/" "${artifact_dir}/terminfo/"

shim_compile_options=(
    -fobjc-arc
    -fblocks
    -Wall
    -Wextra
    -Werror
    -mmacosx-version-min=13.0
    -dynamiclib
)
shim_link_options=(
    -lghostty
    -framework AppKit
    -framework Foundation
    -Wl,-rpath,@loader_path
    -Wl,-install_name,@rpath/libghostshell-ghostty.dylib
)
"${clang}" \
    "${shim_compile_options[@]}" \
    -isysroot "${macos_sdk}" \
    "${repository_dir}/native/macos/GhostShellGhostty.m" \
    -I "${repository_dir}/native/macos" \
    -I "${ghostty_install_dir}/include" \
    -L "${ghostty_install_dir}/lib" \
    "${shim_link_options[@]}" \
    -o "${artifact_dir}/libghostshell-ghostty.dylib"

"${clang}" \
    -fobjc-arc \
    -fblocks \
    -Wall \
    -Wextra \
    -Werror \
    -mmacosx-version-min=13.0 \
    -isysroot "${macos_sdk}" \
    "${repository_dir}/native/macos/GhostShellGhosttySmoke.m" \
    -I "${repository_dir}/native/macos" \
    -L "${artifact_dir}" \
    -lghostshell-ghostty \
    -framework AppKit \
    -framework Foundation \
    -Wl,-rpath,@executable_path \
    -o "${artifact_dir}/ghostshell-ghostty-smoke"

receipt_arguments=(
    native-macos-receipt
    --catalog "${component_catalog}"
    --artifact-directory "${artifact_dir}"
    --output "${receipt}"
    --repository-root "${repository_dir}"
    --ghostty-source "${ghostty_dir}"
    --zig-archive "${zig_archive}"
    --zig-executable "${zig}"
    --zig-library-directory "${zig_dir}/lib"
    --zig-local-cache "${zig_local_cache_dir}"
    --zig-global-cache "${zig_global_cache_dir}"
    --zig-build-trace "${zig_build_trace}"
    --ghostty-install "${ghostty_install_dir}"
    --clang-executable "${clang}"
    --sdk-directory "${macos_sdk}"
    --sdk-settings "${macos_sdk}/SDKSettings.json"
    --release-archive "${release_archive}"
    --release-arm64-binary "${release_arm64_binary}"
    --metallib "${metallib}"
    --artifact-libghostty "${artifact_dir}/libghostty.dylib"
    --zig-version "${zig_version}"
    --ghostty-commit "${ghostty_commit}"
    --ghostty-tag "${ghostty_tag}"
    --clang-version "${clang_version}"
    --sdk-version "${sdk_identity}"
)
for option in "${ghostty_options[@]}"; do
    receipt_arguments+=(--ghostty-option "${option}")
done
shim_receipt_options=(
    "${shim_compile_options[@]}"
    "-isysroot=${sdk_identity}"
    "source=GhostShellGhostty.m"
    "-I=ghostshell-native-macos"
    "-I=ghostty-install-include"
    "-L=ghostty-install-lib"
    "${shim_link_options[@]}"
    "smoke-isysroot=${sdk_identity}"
)
for option in "${shim_receipt_options[@]}"; do
    receipt_arguments+=(--shim-compiler-option "${option}")
done
"${dotnet}" run \
    --project "${repository_dir}/tools/GhostShell.Packaging/GhostShell.Packaging.csproj" \
    --configuration Release \
    -- \
    "${receipt_arguments[@]}"

"${dotnet}" run \
    --project "${repository_dir}/tools/GhostShell.Packaging/GhostShell.Packaging.csproj" \
    --configuration Release \
    -- \
    native-macos-publish-artifacts \
    --staged-directory "${artifact_dir}" \
    --destination "${artifact_destination_dir}"

echo "Built the pinned libghostty runtime in ${artifact_destination_dir}."
