#!/bin/bash

# Script to get the current package version
# Usage: ./get-package-version.sh [scope]
# Without scope, reads from root version.txt (CLI version)
# With scope, reads from package-specific version.txt

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

SCOPE=${1:-}

declare -A VERSION_FILES=(
    ["cli"]="src/version.txt"
    ["devcontainer"]="templates/devcontainer/version.txt"
    ["claude-dotnet-9"]="templates/claude-dotnet-9/version.txt"
    ["claude-dotnet-10-full"]="templates/claude-dotnet-10-full/version.txt"
    ["pks-fullstack"]="templates/pks-fullstack/version.txt"
)

if [ -z "$SCOPE" ]; then
    VERSION_FILE="${REPO_ROOT}/src/version.txt"
elif [ -n "${VERSION_FILES[$SCOPE]+x}" ]; then
    VERSION_FILE="${REPO_ROOT}/${VERSION_FILES[$SCOPE]}"
else
    echo "Error: Invalid scope: ${SCOPE}" >&2
    echo "Scopes: cli, devcontainer, claude-dotnet-9, claude-dotnet-10-full, pks-fullstack" >&2
    exit 1
fi

if [ ! -f "$VERSION_FILE" ]; then
    echo "Error: Version file not found: ${VERSION_FILE}" >&2
    exit 1
fi

cat "$VERSION_FILE"
