#!/bin/bash

# Script to update version in csproj files
# Usage: ./update-version.sh <version> [scope]
# Scope can be: cli, devcontainer, claude-dotnet-9, claude-dotnet-10-full, pks-fullstack, all (default)
# Example: ./update-version.sh 1.2.0 cli

set -euo pipefail

# Color codes for output
readonly RED='\033[0;31m'
readonly GREEN='\033[0;32m'
readonly YELLOW='\033[1;33m'
readonly CYAN='\033[0;36m'
readonly NC='\033[0m' # No Color

VERSION=${1:-}
SCOPE=${2:-all}

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
    echo -e "${CYAN}Usage: $0 <version> [scope]${NC}" >&2
    echo "" >&2
    echo "Arguments:" >&2
    echo "  version    Version to set (e.g., 1.2.0, 1.2.0-rc.1)" >&2
    echo "  scope      Optional. Package scope to update (default: all)" >&2
    echo "" >&2
    echo "Available scopes:" >&2
    echo "  - all (default, updates all packages)" >&2
    for package in "${!PACKAGE_FILES[@]}"; do
        echo "  - ${package}" >&2
    done
    echo "" >&2
    echo "Examples:" >&2
    echo "  $0 1.2.0              # Update all packages to 1.2.0" >&2
    echo "  $0 1.2.0 cli          # Update only CLI to 1.2.0" >&2
    echo "  $0 1.0.5 devcontainer # Update only devcontainer template to 1.0.5" >&2
    exit 1
}

# Function to update version in a single file
update_file_version() {
    local file=$1
    local version=$2

    if [ ! -f "$file" ]; then
        echo -e "${RED}  ⚠️  File not found: $file${NC}"
        return 1
    fi

    echo -e "${CYAN}  📝 Updating: $file${NC}"

    # Update Version property
    if grep -q "<Version>" "$file"; then
        sed -i "s|<Version>.*</Version>|<Version>$version</Version>|g" "$file"
    else
        # Add Version if it doesn't exist
        sed -i "/<PropertyGroup>/a\    <Version>$version</Version>" "$file"
    fi

    # Update PackageVersion property
    if grep -q "<PackageVersion>" "$file"; then
        sed -i "s|<PackageVersion>.*</PackageVersion>|<PackageVersion>$version</PackageVersion>|g" "$file"
    fi

    # Update AssemblyVersion property
    if grep -q "<AssemblyVersion>" "$file"; then
        sed -i "s|<AssemblyVersion>.*</AssemblyVersion>|<AssemblyVersion>$version</AssemblyVersion>|g" "$file"
    fi

    # Update FileVersion property
    if grep -q "<FileVersion>" "$file"; then
        sed -i "s|<FileVersion>.*</FileVersion>|<FileVersion>$version</FileVersion>|g" "$file"
    fi

    echo -e "${GREEN}  ✅ Updated successfully${NC}"
}

# Main execution
main() {
    # Validate version parameter
    if [ -z "$VERSION" ]; then
        echo -e "${RED}Error: Version not provided${NC}" >&2
        echo "" >&2
        usage
    fi

    # Validate scope parameter
    if [ "$SCOPE" != "all" ] && [ -z "${PACKAGE_FILES[$SCOPE]+x}" ]; then
        echo -e "${RED}Error: Invalid scope: ${SCOPE}${NC}" >&2
        echo "" >&2
        usage
    fi

    echo -e "${CYAN}📝 Updating project versions to: ${VERSION}${NC}"
    echo -e "${CYAN}📦 Scope: ${SCOPE}${NC}"
    echo ""

    local updated_count=0

    # Get the repository root (script is in scripts/ directory)
    local script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    local repo_root="$(cd "${script_dir}/.." && pwd)"

    if [ "$SCOPE" = "all" ]; then
        # Update all packages
        for package in "${!PACKAGE_FILES[@]}"; do
            local file="${PACKAGE_FILES[$package]}"
            local full_path="${repo_root}/${file}"

            if update_file_version "$full_path" "$VERSION"; then
                ((updated_count++))
            fi
        done
    else
        # Update specific package
        local file="${PACKAGE_FILES[$SCOPE]}"
        local full_path="${repo_root}/${file}"

        if update_file_version "$full_path" "$VERSION"; then
            ((updated_count++))
        fi
    fi

    echo ""
    echo -e "${GREEN}✅ Version update complete!${NC}"
    echo -e "${GREEN}   Updated ${updated_count} file(s)${NC}"

    # Display updated files
    if [ $updated_count -gt 0 ]; then
        echo ""
        echo -e "${CYAN}📋 Files with new version (${VERSION}):${NC}"

        if [ "$SCOPE" = "all" ]; then
            for package in "${!PACKAGE_FILES[@]}"; do
                local file="${PACKAGE_FILES[$package]}"
                local full_path="${repo_root}/${file}"
                if grep -q "<.*Version>$VERSION</.*Version>" "$full_path" 2>/dev/null; then
                    echo -e "${GREEN}  ✓ ${file}${NC}"
                fi
            done
        else
            local file="${PACKAGE_FILES[$SCOPE]}"
            local full_path="${repo_root}/${file}"
            if grep -q "<.*Version>$VERSION</.*Version>" "$full_path" 2>/dev/null; then
                echo -e "${GREEN}  ✓ ${file}${NC}"
            fi
        fi
    fi
}

# Run main function
main "$@"