#!/bin/bash

# Script to detect changes in packages since their last release
# Outputs JSON object with change status for each package
# Usage: ./detect-changes.sh

set -euo pipefail

# Color codes for output
readonly RED='\033[0;31m'
readonly GREEN='\033[0;32m'
readonly YELLOW='\033[1;33m'
readonly CYAN='\033[0;36m'
readonly NC='\033[0m' # No Color

# Package definitions
declare -A PACKAGES=(
    ["cli"]="v*"
    ["devcontainer"]="devcontainer-v*"
    ["claude-dotnet-9"]="claude-dotnet-9-v*"
    ["claude-dotnet-10-full"]="claude-dotnet-10-full-v*"
    ["pks-fullstack"]="pks-fullstack-v*"
)

declare -A PACKAGE_PATHS=(
    ["cli"]="src/** scripts/** .github/workflows/** *.sln *.md install.sh"
    ["devcontainer"]="templates/devcontainer/**"
    ["claude-dotnet-9"]="templates/claude-dotnet-9/**"
    ["claude-dotnet-10-full"]="templates/claude-dotnet-10-full/**"
    ["pks-fullstack"]="templates/pks-fullstack/**"
)

# Function to get the latest tag for a package
get_latest_tag() {
    local package=$1
    local tag_pattern="${PACKAGES[$package]}"

    # Get the latest tag matching the pattern, sorted by version
    local latest_tag=$(git tag -l "${tag_pattern}" 2>/dev/null | sort -V | tail -n 1)

    echo "${latest_tag}"
}

# Function to check if a package has changes since its last tag
has_changes() {
    local package=$1
    local paths="${PACKAGE_PATHS[$package]}"
    local latest_tag=$(get_latest_tag "$package")

    # If no tag exists, package has changes (initial release)
    if [ -z "$latest_tag" ]; then
        echo "true"
        return 0
    fi

    # Check if there are changes in the package paths since the last tag
    local changes=0

    # Convert space-separated paths to array
    IFS=' ' read -ra path_array <<< "$paths"

    for path in "${path_array[@]}"; do
        # Use git diff to check for changes, handle glob patterns
        if git diff --quiet "${latest_tag}..HEAD" -- "$path" 2>/dev/null; then
            continue
        else
            changes=1
            break
        fi
    done

    if [ $changes -eq 1 ]; then
        echo "true"
    else
        echo "false"
    fi
}

# Function to get current branch
get_current_branch() {
    git rev-parse --abbrev-ref HEAD
}

# Main execution
main() {
    local current_branch=$(get_current_branch)

    echo -e "${CYAN}🔍 Detecting changes for PKS CLI packages${NC}" >&2
    echo -e "${CYAN}📍 Branch: ${current_branch}${NC}" >&2
    echo "" >&2

    # Build JSON object
    local json="{"
    local first=true

    for package in "${!PACKAGES[@]}"; do
        local latest_tag=$(get_latest_tag "$package")
        local changed=$(has_changes "$package")

        # Display information
        if [ -z "$latest_tag" ]; then
            echo -e "${YELLOW}  ⚠️  ${package}: No tags found (initial release)${NC}" >&2
        else
            if [ "$changed" = "true" ]; then
                echo -e "${GREEN}  ✅ ${package}: Changes detected since ${latest_tag}${NC}" >&2
            else
                echo -e "  ⏭️  ${package}: No changes since ${latest_tag}" >&2
            fi
        fi

        # Add to JSON
        if [ "$first" = true ]; then
            first=false
        else
            json="${json},"
        fi

        json="${json}\"${package}\":${changed}"
    done

    json="${json}}"

    echo "" >&2
    echo -e "${CYAN}📊 Change detection result:${NC}" >&2
    echo -e "${CYAN}${json}${NC}" >&2
    echo "" >&2

    # Output JSON to stdout (for consumption by CI/CD)
    echo "$json"
}

# Run main function
main "$@"
