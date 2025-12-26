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

# Discover template packages dynamically
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEMPLATES_DISCOVERY_OUTPUT=$("$SCRIPT_DIR/discover-templates.sh" 2>/dev/null)

# Initialize package definitions with CLI (always present)
declare -A PACKAGES=(
    ["cli"]="v*"
)

declare -A PACKAGE_PATHS=(
    ["cli"]="src/** scripts/** .github/workflows/** *.sln *.md install.sh"
)

# Dynamically add discovered templates to package definitions
if [ -n "$TEMPLATES_DISCOVERY_OUTPUT" ]; then
    while IFS= read -r template_info; do
        template_name=$(echo "$template_info" | jq -r '.template')
        template_path=$(echo "$template_info" | jq -r '.template_path')

        if [ -n "$template_name" ] && [ "$template_name" != "null" ]; then
            PACKAGES["$template_name"]="${template_name}-v*"
            PACKAGE_PATHS["$template_name"]="${template_path}/**"
        fi
    done < <(echo "$TEMPLATES_DISCOVERY_OUTPUT" | jq -c '.include[]')
fi

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
