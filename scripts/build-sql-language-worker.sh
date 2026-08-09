#!/usr/bin/env bash
set -euo pipefail

readonly SCRIPT_DIRECTORY="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "$SCRIPT_DIRECTORY/.." && pwd)"
readonly WORKER_DIRECTORY="$REPOSITORY_ROOT/native/sql-language-worker"
readonly LEGAL_CLOSURE_GENERATOR="$WORKER_DIRECTORY/generate-third-party-notices.sh"
readonly ARTIFACTS_DIRECTORY="$REPOSITORY_ROOT/native/artifacts"
readonly MAVEN_IMAGE="maven:3.9.11-eclipse-temurin-21"
readonly NATIVE_IMAGE="container-registry.oracle.com/graalvm/native-image:25.0.4"
readonly EXPECTED_NATIVE_IMAGE_VERSION="25.0.4"
readonly MAXIMUM_RUNTIME_HEAP="256m"
readonly MINIMUM_MACOS_VERSION="13.0"
readonly MAXIMUM_GLIBC_VERSION="2.34"
readonly MAVEN_CACHE_VOLUME="ghostshell-sql-language-m2"

rid=""
mode="auto"
skip_tests=false
native_image_command=""

if [[ -n "${GRAALVM_HOME:-}" && -z "${JAVA_HOME:-}" ]]; then
    export JAVA_HOME="$GRAALVM_HOME"
fi

resolve_native_image() {
    if [[ -n "${GRAALVM_HOME:-}" && -x "$GRAALVM_HOME/bin/native-image" ]]; then
        echo "$GRAALVM_HOME/bin/native-image"
    elif [[ -n "${JAVA_HOME:-}" && -x "$JAVA_HOME/bin/native-image" ]]; then
        echo "$JAVA_HOME/bin/native-image"
    elif command -v native-image >/dev/null; then
        command -v native-image
    fi
    return 0
}

usage() {
    echo "Usage: $0 [--rid RID] [--docker|--local] [--skip-tests]"
    echo ""
    echo "RIDs: linux-x64, linux-arm64, osx-x64, osx-arm64, win-x64"
}

while (($# > 0)); do
    case "$1" in
        --rid)
            [[ $# -ge 2 ]] || { usage >&2; exit 64; }
            rid="$2"
            shift 2
            ;;
        --docker)
            mode="docker"
            shift
            ;;
        --local)
            mode="local"
            shift
            ;;
        --skip-tests)
            skip_tests=true
            shift
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            usage >&2
            exit 64
            ;;
    esac
done

host_rid() {
    local operating_system architecture
    operating_system="$(uname -s)"
    architecture="$(uname -m)"
    case "$operating_system:$architecture" in
        Linux:x86_64) echo "linux-x64" ;;
        Linux:aarch64|Linux:arm64) echo "linux-arm64" ;;
        Darwin:x86_64) echo "osx-x64" ;;
        Darwin:arm64) echo "osx-arm64" ;;
        MINGW*:x86_64|MSYS*:x86_64|CYGWIN*:x86_64) echo "win-x64" ;;
        *)
            echo "Cannot infer a supported RID from $operating_system/$architecture." >&2
            exit 65
            ;;
    esac
}

if [[ -z "$rid" ]]; then
    rid="$(host_rid)"
fi

binary_name="ghostshell-sql-language"
docker_platform=""
abi=""
case "$rid" in
    linux-x64)
        docker_platform="linux/amd64"
        abi="linux-gnu-x64"
        ;;
    linux-arm64)
        docker_platform="linux/arm64"
        abi="linux-gnu-arm64"
        ;;
    osx-x64) abi="darwin-x64" ;;
    osx-arm64) abi="darwin-arm64" ;;
    win-x64)
        binary_name="ghostshell-sql-language.exe"
        abi="windows-x64"
        ;;
    win-arm64)
        echo "Windows ARM64 is not supported by GraalVM Native Image 25.0.4." >&2
        exit 65
        ;;
    *)
        echo "Unsupported RID: $rid" >&2
        exit 65
        ;;
esac

if [[ "$mode" == "auto" ]]; then
    native_image_command="$(resolve_native_image)"
    if [[ "$rid" == "$(host_rid)" ]] && command -v mvn >/dev/null && [[ -n "$native_image_command" ]]; then
        mode="local"
    elif [[ -n "$docker_platform" ]] && command -v docker >/dev/null; then
        mode="docker"
    else
        echo "No local GraalVM toolchain is available, and Docker can only build Linux RIDs." >&2
        exit 69
    fi
fi

if [[ "$mode" == "local" ]]; then
    native_image_command="$(resolve_native_image)"
    [[ "$rid" == "$(host_rid)" ]] || {
        echo "Local Native Image cannot cross-compile $rid from $(host_rid)." >&2
        exit 69
    }
    command -v mvn >/dev/null || { echo "mvn is required for --local." >&2; exit 69; }
    [[ -n "$native_image_command" ]] || {
        echo "native-image is required for --local (PATH, GRAALVM_HOME, or JAVA_HOME)." >&2
        exit 69
    }
fi

if [[ "$mode" == "docker" ]]; then
    [[ -n "$docker_platform" ]] || {
        echo "Docker builds are supported only for Linux RIDs." >&2
        exit 69
    }
    command -v docker >/dev/null || { echo "docker is required for --docker." >&2; exit 69; }
fi

verify_native_image_version() {
    local version_line="$1"
    if [[ ! "$version_line" =~ ^native-image[[:space:]]+$EXPECTED_NATIVE_IMAGE_VERSION([[:space:]]|$) ]]; then
        echo "Native Image $EXPECTED_NATIVE_IMAGE_VERSION is required; found: $version_line" >&2
        exit 69
    fi
}

sha256_file() {
    if command -v sha256sum >/dev/null; then
        sha256sum "$1" | awk '{print $1}'
    else
        shasum -a 256 "$1" | awk '{print $1}'
    fi
}

legal_property() {
    local key="$1"
    local properties_file="$WORKER_DIRECTORY/target/legal-closure.properties"
    awk -F= -v key="$key" '
        $1 == key { value = $2; matches++ }
        END { if (matches != 1 || value == "") exit 2; print value }
    ' "$properties_file"
}

version_is_at_most() {
    local actual="$1"
    local maximum="$2"
    local actual_major actual_minor maximum_major maximum_minor
    IFS=. read -r actual_major actual_minor <<< "$actual"
    IFS=. read -r maximum_major maximum_minor <<< "$maximum"
    ((actual_major < maximum_major
        || (actual_major == maximum_major && actual_minor <= maximum_minor)))
}

read_macos_minimum_version() {
    otool -l "$1" | awk '
        $1 == "cmd" && $2 == "LC_BUILD_VERSION" { in_build_version = 1; next }
        in_build_version && $1 == "minos" { print $2; exit }
        $1 == "cmd" && $2 == "LC_VERSION_MIN_MACOSX" { in_version_min = 1; next }
        in_version_min && $1 == "version" { print $2; exit }
    '
}

if [[ "$mode" == "local" ]]; then
    native_image_version="$("$native_image_command" --version 2>&1 | sed -n '1p')"
else
    native_image_version="$(docker run --rm --platform "$docker_platform" \
        "$NATIVE_IMAGE" --version 2>&1 | sed -n '1p')"
fi
verify_native_image_version "$native_image_version"

artifact_directory="$ARTIFACTS_DIRECTORY/$rid"
artifact_path="$artifact_directory/$binary_name"
mkdir -p "$ARTIFACTS_DIRECTORY"
staging_directory="$(mktemp -d "$ARTIFACTS_DIRECTORY/.sql-language-$rid.XXXXXX")"
staged_artifact_path="$staging_directory/$binary_name"
cleanup_staging() {
    rm -rf -- "$staging_directory"
}
trap cleanup_staging EXIT

maven_goal="verify"
if [[ "$skip_tests" == true ]]; then
    maven_goal="package"
fi
native_image_common_arguments=(
    --no-fallback
    -O2
    "-R:MaxHeapSize=$MAXIMUM_RUNTIME_HEAP"
    -H:+ReportExceptionStackTraces
)

if [[ "$mode" == "local" ]]; then
    (
        cd "$WORKER_DIRECTORY"
        if [[ "$skip_tests" == true ]]; then
            mvn -B clean package -DskipTests
        else
            mvn -B clean verify
        fi
        mvn -B dependency:list \
            -DincludeScope=runtime \
            -DoutputFile=target/runtime-dependencies.raw.txt \
            -DappendOutput=false \
            dependency:copy-dependencies \
            -DincludeScope=runtime \
            -DoutputDirectory=target/runtime-jars \
            -DoverWriteReleases=true \
            -DoverWriteSnapshots=true \
            -DoverWriteIfNewer=true
        "$LEGAL_CLOSURE_GENERATOR" \
            --dependency-list target/runtime-dependencies.raw.txt \
            --jar-directory target/runtime-jars \
            --manifest target/runtime-dependencies.txt \
            --output target/THIRD-PARTY-NOTICES.md \
            --metadata target/legal-closure.properties
        mvn -B \
            -Dtest=LegalClosurePolicyTest \
            "-Dlegal.runtimeDependencies=$WORKER_DIRECTORY/target/runtime-dependencies.txt" \
            "-Dlegal.thirdPartyNotices=$WORKER_DIRECTORY/target/THIRD-PARTY-NOTICES.md" \
            "-Dlegal.metadata=$WORKER_DIRECTORY/target/legal-closure.properties" \
            test
    )
    native_image_arguments=(
        "${native_image_common_arguments[@]}"
        -jar "$WORKER_DIRECTORY/target/ghostshell-sql-language-worker.jar"
        -o "$staged_artifact_path"
    )
    if [[ "$rid" == osx-* ]]; then
        native_image_arguments=(
            "${native_image_common_arguments[@]}"
            "--native-compiler-options=-mmacosx-version-min=$MINIMUM_MACOS_VERSION"
            "-H:NativeLinkerOption=-mmacosx-version-min=$MINIMUM_MACOS_VERSION"
            -jar "$WORKER_DIRECTORY/target/ghostshell-sql-language-worker.jar"
            -o "$staged_artifact_path"
        )
        MACOSX_DEPLOYMENT_TARGET="$MINIMUM_MACOS_VERSION" \
            "$native_image_command" "${native_image_arguments[@]}"
    else
        "$native_image_command" "${native_image_arguments[@]}"
    fi
else
    docker volume create "$MAVEN_CACHE_VOLUME" >/dev/null
    maven_arguments=(mvn -B clean "$maven_goal")
    if [[ "$skip_tests" == true ]]; then
        maven_arguments+=(-DskipTests)
    fi
    docker run --rm \
        --platform "$docker_platform" \
        -v "$MAVEN_CACHE_VOLUME:/root/.m2" \
        -v "$WORKER_DIRECTORY:/workspace" \
        -w /workspace \
        "$MAVEN_IMAGE" \
        "${maven_arguments[@]}"
    docker run --rm \
        --platform "$docker_platform" \
        -v "$MAVEN_CACHE_VOLUME:/root/.m2" \
        -v "$WORKER_DIRECTORY:/workspace" \
        -w /workspace \
        "$MAVEN_IMAGE" \
        mvn -B dependency:list \
            -DincludeScope=runtime \
            -DoutputFile=target/runtime-dependencies.raw.txt \
            -DappendOutput=false \
            dependency:copy-dependencies \
            -DincludeScope=runtime \
            -DoutputDirectory=target/runtime-jars \
            -DoverWriteReleases=true \
            -DoverWriteSnapshots=true \
            -DoverWriteIfNewer=true
    docker run --rm \
        --platform "$docker_platform" \
        -v "$WORKER_DIRECTORY:/workspace" \
        -w /workspace \
        "$MAVEN_IMAGE" \
        bash ./generate-third-party-notices.sh \
            --dependency-list target/runtime-dependencies.raw.txt \
            --jar-directory target/runtime-jars \
            --manifest target/runtime-dependencies.txt \
            --output target/THIRD-PARTY-NOTICES.md \
            --metadata target/legal-closure.properties
    docker run --rm \
        --platform "$docker_platform" \
        -v "$MAVEN_CACHE_VOLUME:/root/.m2" \
        -v "$WORKER_DIRECTORY:/workspace" \
        -w /workspace \
        "$MAVEN_IMAGE" \
        mvn -B \
            -Dtest=LegalClosurePolicyTest \
            -Dlegal.runtimeDependencies=/workspace/target/runtime-dependencies.txt \
            -Dlegal.thirdPartyNotices=/workspace/target/THIRD-PARTY-NOTICES.md \
            -Dlegal.metadata=/workspace/target/legal-closure.properties \
            test
    docker run --rm \
        --platform "$docker_platform" \
        --entrypoint native-image \
        -v "$WORKER_DIRECTORY:/workspace" \
        -v "$staging_directory:/out" \
        -w /workspace \
        "$NATIVE_IMAGE" \
        "${native_image_common_arguments[@]}" \
        -jar target/ghostshell-sql-language-worker.jar \
        -o "/out/$binary_name"
fi

if [[ "$rid" != win-* ]]; then
    chmod +x "$staged_artifact_path"
fi

minimum_os_version=""
minimum_glibc_version=""
if [[ "$rid" == osx-* ]]; then
    command -v otool >/dev/null || { echo "otool is required for macOS ABI validation." >&2; exit 69; }
    minimum_os_version="$(read_macos_minimum_version "$staged_artifact_path")"
    [[ "$minimum_os_version" == "$MINIMUM_MACOS_VERSION" ]] || {
        echo "macOS artifact minimum version is $minimum_os_version; expected $MINIMUM_MACOS_VERSION." >&2
        exit 70
    }
elif [[ "$rid" == linux-* ]]; then
    minimum_glibc_version="$(docker run --rm \
        --platform "$docker_platform" \
        --entrypoint /bin/bash \
        -v "$staging_directory:/out:ro" \
        "$NATIVE_IMAGE" \
        -lc "readelf --version-info '/out/$binary_name' \
            | grep -o 'GLIBC_[0-9.]*' \
            | sed 's/GLIBC_//' \
            | sort -Vu \
            | tail -1")"
    [[ -n "$minimum_glibc_version" ]] || {
        echo "Could not determine the Linux artifact's glibc floor." >&2
        exit 70
    }
    version_is_at_most "$minimum_glibc_version" "$MAXIMUM_GLIBC_VERSION" || {
        echo "Linux artifact requires glibc $minimum_glibc_version; maximum supported is $MAXIMUM_GLIBC_VERSION." >&2
        exit 70
    }
fi

if [[ "$mode" == "local" ]]; then
    (
        cd "$WORKER_DIRECTORY"
        mvn -B \
            -Dtest=NativeExecutableSmokeTest \
            "-Dnative.executable=$staged_artifact_path" \
            test
    )
else
    docker run --rm \
        --platform "$docker_platform" \
        -v "$MAVEN_CACHE_VOLUME:/root/.m2" \
        -v "$WORKER_DIRECTORY:/workspace" \
        -v "$staging_directory:/out:ro" \
        -w /workspace \
        "$MAVEN_IMAGE" \
        mvn -B \
            -Dtest=NativeExecutableSmokeTest \
            "-Dnative.executable=/out/$binary_name" \
            test
fi

cp "$WORKER_DIRECTORY/target/THIRD-PARTY-NOTICES.md" "$staging_directory/THIRD-PARTY-NOTICES.md"
cp "$WORKER_DIRECTORY/target/runtime-dependencies.txt" "$staging_directory/runtime-dependencies.txt"

artifact_sha256="$(sha256_file "$staged_artifact_path")"
legal_closure_format_version="$(legal_property formatVersion)"
legal_document_count="$(legal_property legalDocumentCount)"
legal_review_required_count="$(legal_property legalReviewRequiredCount)"
runtime_dependency_count="$(legal_property runtimeDependencyCount)"
runtime_dependencies_sha256="$(legal_property runtimeDependenciesSha256)"
third_party_notices_sha256="$(legal_property thirdPartyNoticesSha256)"
[[ "$legal_closure_format_version" =~ ^[0-9]+$ \
    && "$legal_document_count" =~ ^[0-9]+$ \
    && "$legal_review_required_count" =~ ^[0-9]+$ \
    && "$runtime_dependency_count" =~ ^[0-9]+$ ]] || {
    echo "Legal closure metadata contains a non-numeric count or format version." >&2
    exit 65
}
[[ "$(sha256_file "$staging_directory/runtime-dependencies.txt")" == "$runtime_dependencies_sha256" ]] || {
    echo "Published runtime dependency manifest differs from the validated legal closure." >&2
    exit 65
}
[[ "$(sha256_file "$staging_directory/THIRD-PARTY-NOTICES.md")" == "$third_party_notices_sha256" ]] || {
    echo "Published third-party notices differ from the validated legal closure." >&2
    exit 65
}

escaped_builder="${native_image_version//\\/\\\\}"
escaped_builder="${escaped_builder//\"/\\\"}"
builder_source="$NATIVE_IMAGE"
if [[ "$mode" == "local" ]]; then
    builder_source="local"
fi
escaped_builder_source="${builder_source//\\/\\\\}"
escaped_builder_source="${escaped_builder_source//\"/\\\"}"
built_at_utc="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
minimum_version_field=""
if [[ -n "$minimum_os_version" ]]; then
    minimum_version_field="  \"minimumOsVersion\": \"$minimum_os_version\","
elif [[ -n "$minimum_glibc_version" ]]; then
    minimum_version_field="  \"minimumGlibcVersion\": \"$minimum_glibc_version\","
fi
cat > "$staging_directory/build-receipt.json" <<EOF
{
  "protocolVersion": 1,
  "rid": "$rid",
  "artifact": "$binary_name",
  "abi": "$abi",
$minimum_version_field
  "sha256": "$artifact_sha256",
  "calciteVersion": "1.42.0",
  "javaRelease": 21,
  "legalClosureFormatVersion": $legal_closure_format_version,
  "legalDocumentCount": $legal_document_count,
  "legalReviewRequiredCount": $legal_review_required_count,
  "runtimeDependencyCount": $runtime_dependency_count,
  "runtimeDependenciesSha256": "$runtime_dependencies_sha256",
  "thirdPartyNoticesSha256": "$third_party_notices_sha256",
  "maximumRuntimeHeap": "$MAXIMUM_RUNTIME_HEAP",
  "buildMode": "$mode",
  "builder": "$escaped_builder",
  "builderSource": "$escaped_builder_source",
  "builtAtUtc": "$built_at_utc"
}
EOF

mkdir -p "$artifact_directory"
previous_receipt="$artifact_directory/.build-receipt.json.previous.$$"
if [[ -f "$artifact_directory/build-receipt.json" ]]; then
    mv "$artifact_directory/build-receipt.json" "$previous_receipt"
fi
mv -f "$staged_artifact_path" "$artifact_path"
mv -f \
    "$staging_directory/THIRD-PARTY-NOTICES.md" \
    "$artifact_directory/THIRD-PARTY-NOTICES.md"
mv -f \
    "$staging_directory/runtime-dependencies.txt" \
    "$artifact_directory/runtime-dependencies.txt"
# The receipt is the commit marker: packaging fails closed while it is absent,
# and can only observe it after the binary and metadata have been published.
mv -f "$staging_directory/build-receipt.json" "$artifact_directory/build-receipt.json"
if [[ -f "$previous_receipt" ]]; then
    rm -f -- "$previous_receipt"
fi

echo "Built $artifact_path"
echo "SHA-256 $artifact_sha256"
