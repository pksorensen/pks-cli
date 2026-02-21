#!/bin/bash

# Script to extract the current version from a package's .csproj file
# Usage: ./get-package-version.sh <package-scope>
# Example: ./get-package-version.sh cli

set -euo pipefail

# Color codes for output
readonly RED='\033[0;31m'
readonly GREEN='\033[0;32m'
readonly CYAN='\033[0;36m'
readonly NC='\033[0m' # No Color

# Package to .csproj file mappings
declare -A PACKAGE_FILES=(
    ["cli"]="src/pks-cli.csproj"
    ["devcontainer"]="templates/devcontainer/PKS.Templates.DevContainer.csproj"
    ["claude-dotnet-9"]="templates/claude-dotnet-9/PKS.Templates.ClaudeDotNet9.csproj"
    ["claude-dotnet-10-full"]="templates/claude-dotnet-10-full/PKS.Templates.ClaudeDotNet10.Full.csproj"
    ["pks-fullstack"]="templates/pks-fullstack/PKS.Templates.PksFullstack.csproj"
)

# Function to display usage
usage() {
    echo -e "${CYAN}Usage: $0 <package-scope>${NC}" >&2
    echo "" >&2
    echo "Available package scopes:" >&2
    for package in "${!PACKAGE_FILES[@]}"; do
        echo "  - ${package}" >&2
    done
    echo "" >&2
    echo "Example:" >&2
    echo "  $0 cli" >&2
    exit 1
}

# Function to extract version from .csproj file
get_version() {
    local csproj_file=$1

    # Check if file exists
    if [ ! -f "$csproj_file" ]; then
        echo -e "${RED}Error: File not found: ${csproj_file}${NC}" >&2
        exit 1
    fi

    # Extract version from PackageVersion or Version tag
    local version=$(grep -oP '<PackageVersion>\K[^<]+' "$csproj_file" 2>/dev/null || \
                    grep -oP '<Version>\K[^<]+' "$csproj_file" 2>/dev/null || \
                    echo "")

    if [ -z "$version" ]; then
        echo -e "${RED}Error: Could not find version in ${csproj_file}${NC}" >&2
        exit 1
    fi

    echo "$version"
}

# Main execution
main() {
    # Check if package scope is provided
    if [ $# -eq 0 ]; then
        echo -e "${RED}Error: Package scope not provided${NC}" >&2
        echo "" >&2
        usage
    fi

    local package_scope=$1

    # Validate package scope
    if [ -z "${PACKAGE_FILES[$package_scope]+x}" ]; then
        echo -e "${RED}Error: Invalid package scope: ${package_scope}${NC}" >&2
        echo "" >&2
        usage
    fi

    # Get the .csproj file path
    local csproj_file="${PACKAGE_FILES[$package_scope]}"

    # Get the repository root (script is in scripts/ directory)
    local script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    local repo_root="$(cd "${script_dir}/.." && pwd)"
    local full_path="${repo_root}/${csproj_file}"

    # Extract and output version
    local version=$(get_version "$full_path")
    echo "$version"
}

# Run main function
main "$@"
