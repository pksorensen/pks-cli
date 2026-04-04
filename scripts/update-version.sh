#!/bin/bash

# Script to update version in csproj files
# Usage: ./update-version.sh <version> [scope]
# Scope: cli, devcontainer, claude-dotnet-9, claude-dotnet-10-full, pks-fullstack, all (default)

set -euo pipefail

readonly GREEN='\033[0;32m'
readonly RED='\033[0;31m'
readonly CYAN='\033[0;36m'
readonly NC='\033[0m'

VERSION=${1:-}
SCOPE=${2:-all}

# Package to file mappings
declare -A PACKAGE_FILES=(
    ["cli"]="src/pks-cli.csproj"
    ["devcontainer"]="templates/devcontainer/PKS.Templates.DevContainer.csproj"
    ["claude-dotnet-9"]="templates/claude-dotnet-9/PKS.Templates.ClaudeDotNet9.csproj"
    ["claude-dotnet-10-full"]="templates/claude-dotnet-10-full/PKS.Templates.ClaudeDotNet10.Full.csproj"
    ["pks-fullstack"]="templates/pks-fullstack/PKS.Templates.PksFullstack.csproj"
)

# Package to version.txt mappings
declare -A VERSION_FILES=(
    ["cli"]="version.txt"
    ["devcontainer"]="templates/devcontainer/version.txt"
    ["claude-dotnet-9"]="templates/claude-dotnet-9/version.txt"
    ["claude-dotnet-10-full"]="templates/claude-dotnet-10-full/version.txt"
    ["pks-fullstack"]="templates/pks-fullstack/version.txt"
)

if [ -z "$VERSION" ]; then
    echo -e "${RED}Error: Version not provided${NC}" >&2
    echo "Usage: $0 <version> [scope]" >&2
    echo "Scopes: cli, devcontainer, claude-dotnet-9, claude-dotnet-10-full, pks-fullstack, all" >&2
    exit 1
fi

if [ "$SCOPE" != "all" ] && [ -z "${PACKAGE_FILES[$SCOPE]+x}" ]; then
    echo -e "${RED}Error: Invalid scope: ${SCOPE}${NC}" >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

update_package() {
    local package=$1
    local file="${REPO_ROOT}/${PACKAGE_FILES[$package]}"
    local version_file="${REPO_ROOT}/${VERSION_FILES[$package]}"

    if [ ! -f "$file" ]; then
        echo -e "${RED}  File not found: ${PACKAGE_FILES[$package]}${NC}"
        return 1
    fi

    echo -e "${CYAN}  Updating: ${PACKAGE_FILES[$package]}${NC}"
    for tag in Version PackageVersion AssemblyVersion FileVersion; do
        if grep -q "<${tag}>" "$file"; then
            sed -i "s|<${tag}>.*</${tag}>|<${tag}>${VERSION}</${tag}>|g" "$file"
        fi
    done

    echo "$VERSION" > "$version_file"
    echo -e "${GREEN}  Updated${NC}"
}

echo -e "${CYAN}Updating version to: ${VERSION} (scope: ${SCOPE})${NC}"
echo ""

updated_count=0

if [ "$SCOPE" = "all" ]; then
    for package in "${!PACKAGE_FILES[@]}"; do
        if update_package "$package"; then
            ((updated_count++))
        fi
    done
else
    if update_package "$SCOPE"; then
        ((updated_count++))
    fi
fi

echo ""
echo -e "${GREEN}Updated ${updated_count} package(s) to ${VERSION}${NC}"
