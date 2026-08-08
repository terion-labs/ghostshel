#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_dir="$(cd "${script_dir}/.." && pwd)"
dotnet="${repository_dir}/.dotnet/dotnet"
project="${repository_dir}/tests/GhostShell.Databases.IntegrationTests/GhostShell.Databases.IntegrationTests.csproj"

if [[ ! -x "${dotnet}" ]]; then
    echo "Run GHOSTSHELL_SKIP_NATIVE=1 ./scripts/bootstrap.sh first." >&2
    exit 1
fi

if [[ "${1-}" == "--help" || "${1-}" == "-h" ]]; then
    cat <<'USAGE'
Usage: ./scripts/test-database-viewer-integration.sh [provider,...|all] [dotnet test arguments]

Examples:
  ./scripts/test-database-viewer-integration.sh
  ./scripts/test-database-viewer-integration.sh sqlite,duckdb
  ./scripts/test-database-viewer-integration.sh postgres --logger "console;verbosity=detailed"

If no provider argument is supplied, the script uses
GHOSTSHELL_DATABASE_INTEGRATION_PROVIDERS, then falls back to all.
USAGE
    exit 0
fi

providers="${GHOSTSHELL_DATABASE_INTEGRATION_PROVIDERS:-all}"
if [[ $# -gt 0 && "${1}" != -* ]]; then
    providers="${1}"
    shift
fi

# Provider IDs are case-insensitive at this command boundary. Remove whitespace
# so the value passed to the test process has the canonical comma-list shape.
providers="$(printf '%s' "${providers}" | tr '[:upper:]' '[:lower:]' | tr -d '[:space:]')"
if [[ -z "${providers}" ]]; then
    echo "The provider list cannot be empty." >&2
    exit 2
fi

docker_required=0
if [[ "${providers}" == "all" ]]; then
    docker_required=1
else
    IFS=',' read -r -a selected_providers <<< "${providers}"
    for provider in "${selected_providers[@]}"; do
        case "${provider}" in
            sqlite|duckdb)
                ;;
            postgres|cockroach|redshift|mysql|mariadb|sqlserver|oracle|firebird|clickhouse)
                docker_required=1
                ;;
            *)
                echo "Unknown database integration provider: ${provider}" >&2
                echo "Known providers: sqlite, duckdb, postgres, cockroach, redshift, mysql, mariadb, sqlserver, oracle, firebird, clickhouse" >&2
                exit 2
                ;;
        esac
    done
fi

if [[ "${docker_required}" == "1" ]]; then
    if ! command -v docker >/dev/null 2>&1; then
        echo "Docker is required for the selected database provider(s)." >&2
        exit 1
    fi

    if ! docker info >/dev/null 2>&1; then
        echo "Docker is installed, but its daemon is not available." >&2
        exit 1
    fi
fi

export GHOSTSHELL_RUN_DATABASE_INTEGRATION=1
export GHOSTSHELL_DATABASE_INTEGRATION_PROVIDERS="${providers}"

exec "${dotnet}" test "${project}" --configuration Release "$@"
