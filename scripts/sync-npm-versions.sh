#!/bin/bash

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Logging functions
log_info() {
    echo -e "${BLUE}ℹ️  $1${NC}"
}

log_success() {
    echo -e "${GREEN}✅ $1${NC}"
}

log_error() {
    echo -e "${RED}❌ $1${NC}"
}

log_warn() {
    echo -e "${YELLOW}⚠️  $1${NC}"
}

log_step() {
    echo -e "\n${BLUE}🔨 $1${NC}"
}

# Get the root directory (parent of scripts directory)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
NPM_DIR="$ROOT_DIR/npm"
SRC_DIR="$ROOT_DIR/src"

# Get version from command line or .csproj
VERSION="${1:-}"

if [ -z "$VERSION" ]; then
    log_info "No version provided, reading from .csproj..."

    CSPROJ_PATH="$SRC_DIR/pks-cli.csproj"

    if [ ! -f "$CSPROJ_PATH" ]; then
        log_error ".csproj file not found at: $CSPROJ_PATH"
        exit 1
    fi

    # Extract version from .csproj
    VERSION=$(grep -oP '<Version>\K[^<]+' "$CSPROJ_PATH" || echo "")

    if [ -z "$VERSION" ]; then
        log_error "Could not extract version from .csproj"
        exit 1
    fi
fi

log_info "Syncing version: $VERSION"

# Update main package.json
update_package_json() {
    local package_json="$1"
    local version="$2"

    if [ ! -f "$package_json" ]; then
        log_warn "File not found: $package_json"
        return 1
    fi

    log_info "Updating: $package_json"

    # Use Node.js to update the JSON file properly
    node -e "
        const fs = require('fs');
        const path = '$package_json';
        const version = '$version';

        try {
            const pkg = JSON.parse(fs.readFileSync(path, 'utf8'));
            pkg.version = version;

            // Update optionalDependencies versions if present
            if (pkg.optionalDependencies) {
                Object.keys(pkg.optionalDependencies).forEach(dep => {
                    pkg.optionalDependencies[dep] = version;
                });
            }

            fs.writeFileSync(path, JSON.stringify(pkg, null, 2) + '\n');
            console.log('✓');
        } catch (error) {
            console.error('Error:', error.message);
            process.exit(1);
        }
    "

    if [ $? -eq 0 ]; then
        log_success "Updated: $package_json"
        return 0
    else
        log_error "Failed to update: $package_json"
        return 1
    fi
}

# Main execution
main() {
    log_step "Syncing npm package versions to $VERSION"

    local success_count=0
    local failure_count=0

    # Update main package
    if update_package_json "$NPM_DIR/pks-cli/package.json" "$VERSION"; then
        ((success_count++))
    else
        ((failure_count++))
    fi

    # Update platform packages
    for platform_dir in "$NPM_DIR"/pks-cli-*; do
        if [ -d "$platform_dir" ]; then
            if update_package_json "$platform_dir/package.json" "$VERSION"; then
                ((success_count++))
            else
                ((failure_count++))
            fi
        fi
    done

    # Summary
    log_step "Summary"
    log_info "Total packages: $((success_count + failure_count))"
    log_success "Successfully updated: $success_count"

    if [ $failure_count -gt 0 ]; then
        log_error "Failed: $failure_count"
        exit 1
    fi

    log_success "\n🎉 All package versions synced to $VERSION!"
}

# Show usage
show_usage() {
    cat << EOF
Usage: $0 [VERSION]

Syncs all npm package.json files to the specified version.

Arguments:
  VERSION   Version to set (e.g., 1.2.3). If not provided, reads from .csproj

Examples:
  $0 1.2.3          # Set all packages to version 1.2.3
  $0                # Read version from .csproj and sync

EOF
}

# Check for help flag
if [ "$1" = "-h" ] || [ "$1" = "--help" ]; then
    show_usage
    exit 0
fi

# Run main function
main
