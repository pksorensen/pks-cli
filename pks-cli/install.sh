#!/bin/bash

# PKS CLI Installation Script
# Cross-platform installation script for PKS CLI
# Supports Linux, macOS, and Windows (via WSL/Git Bash)

set -e  # Exit on any error

# Colors for output (with fallback for non-color terminals)
if [ -t 1 ] && command -v tput > /dev/null 2>&1 && tput colors > /dev/null 2>&1; then
    RED=$(tput setaf 1)
    GREEN=$(tput setaf 2)
    YELLOW=$(tput setaf 3)
    CYAN=$(tput setaf 6)
    RESET=$(tput sgr0)
else
    RED=""
    GREEN=""
    YELLOW=""
    CYAN=""
    RESET=""
fi

# Configuration
CONFIGURATION="${CONFIGURATION:-Release}"
FORCE_INSTALL="${FORCE_INSTALL:-false}"

echo "${CYAN}🚀 Installing PKS CLI - The Next Agentic CLI for .NET Developers${RESET}"
echo ""

# Function to print error and exit
error_exit() {
    echo "${RED}❌ Error: $1${RESET}" >&2
    exit 1
}

# Function to print warning
warning() {
    echo "${YELLOW}⚠️  Warning: $1${RESET}" >&2
}

# Function to print success
success() {
    echo "${GREEN}✅ $1${RESET}"
}

# Function to print info
info() {
    echo "${CYAN}ℹ️  $1${RESET}"
}

# Check if .NET is installed
check_dotnet() {
    if ! command -v dotnet > /dev/null 2>&1; then
        error_exit ".NET is not installed. Please install .NET 8.0 or higher first.\nVisit: https://dotnet.microsoft.com/download"
    fi

    # Get .NET version and validate
    DOTNET_VERSION=$(dotnet --version 2>/dev/null || echo "unknown")
    if [ "$DOTNET_VERSION" = "unknown" ]; then
        error_exit "Unable to determine .NET version"
    fi

    # Extract major version (e.g., "8.0.100" -> "8")
    MAJOR_VERSION=$(echo "$DOTNET_VERSION" | cut -d. -f1)
    if [ "$MAJOR_VERSION" -lt 8 ]; then
        error_exit ".NET 8.0 or higher is required. Found version: $DOTNET_VERSION"
    fi

    success "Found .NET version: $DOTNET_VERSION"
}

# Navigate to script directory
setup_directories() {
    SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    cd "$SCRIPT_DIR" || error_exit "Failed to navigate to script directory: $SCRIPT_DIR"
    
    # Verify we're in the right place
    if [ ! -f "PKS.CLI.sln" ]; then
        error_exit "PKS.CLI.sln not found. Please run this script from the PKS CLI root directory."
    fi
    
    info "Working directory: $SCRIPT_DIR"
}

# Clean previous builds if requested
clean_build() {
    if [ "$FORCE_INSTALL" = "true" ]; then
        info "Cleaning previous build artifacts..."
        dotnet clean PKS.CLI.sln --configuration "$CONFIGURATION" > /dev/null 2>&1 || true
        rm -rf src/bin src/obj templates/*/bin templates/*/obj 2>/dev/null || true
    fi
}

# Build the solution
build_solution() {
    info "Building PKS CLI and templates..."
    
    # Restore packages first
    if ! dotnet restore PKS.CLI.sln --verbosity quiet; then
        error_exit "Failed to restore NuGet packages"
    fi
    
    # Build solution
    if ! dotnet build PKS.CLI.sln --configuration "$CONFIGURATION" --no-restore --verbosity minimal; then
        error_exit "Build failed. Please check the error messages above."
    fi
    
    success "Build completed successfully"
}

# Create packages
create_packages() {
    info "Creating packages (CLI + Templates)..."
    
    if ! dotnet pack PKS.CLI.sln --configuration "$CONFIGURATION" --no-build --verbosity minimal; then
        error_exit "Package creation failed. Please check the error messages above."
    fi
    
    success "Packages created successfully"
}

# Install as global tool
install_global_tool() {
    info "Installing as global tool..."
    
    local cli_package_dir="./src/bin/$CONFIGURATION"
    
    if [ ! -d "$cli_package_dir" ]; then
        error_exit "CLI package directory not found: $cli_package_dir"
    fi
    
    # Prepare install arguments
    local install_args="tool install -g --add-source $cli_package_dir pks-cli"
    
    # Add force flag if specified or if tool already exists
    if [ "$FORCE_INSTALL" = "true" ] || dotnet tool list -g | grep -q "pks-cli"; then
        install_args="$install_args --force"
    fi
    
    if ! dotnet $install_args; then
        error_exit "Installation failed. You may need to run with FORCE_INSTALL=true"
    fi
    
    success "Global tool installed successfully"
}

# Show created packages
show_packages() {
    info "Template packages created:"
    
    # Find template packages with better error handling
    local template_packages
    template_packages=$(find . -name "*.Templates.*.nupkg" 2>/dev/null | head -5)
    
    if [ -n "$template_packages" ]; then
        echo "$template_packages" | while read -r package; do
            if [ -n "$package" ]; then
                echo "  - $(basename "$package")"
            fi
        done
    else
        warning "No template packages found"
    fi
}

# Verify installation
verify_installation() {
    info "Verifying installation..."
    
    # Wait a moment for the PATH to update
    sleep 1
    
    # Check if pks command is available
    if command -v pks > /dev/null 2>&1; then
        # Try to get version to ensure it works
        local pks_version
        pks_version=$(pks --version 2>/dev/null || echo "unknown")
        
        if [ "$pks_version" != "unknown" ]; then
            echo ""
            success "PKS CLI installed successfully! Version: $pks_version"
        else
            success "PKS CLI installed successfully!"
        fi
        
        echo ""
        echo "${CYAN}Try these commands:${RESET}"
        echo "  pks --help              # Show help"
        echo "  pks ascii 'Hello'       # Generate ASCII art"
        echo "  pks init my-project     # Initialize a new project"
        echo "  pks agent create        # Create an AI agent"
        echo ""
        echo "${CYAN}🤖 Welcome to the future of .NET development!${RESET}"
        
        return 0
    else
        warning "PKS command not found in PATH. You may need to:"
        echo "  - Restart your shell/terminal"
        echo "  - Add ~/.dotnet/tools to your PATH"
        echo "  - Run: export PATH=\"\$PATH:\$HOME/.dotnet/tools\""
        
        return 1
    fi
}

# Show usage information
show_usage() {
    echo ""
    echo "${CYAN}💡 Usage Tips:${RESET}"
    echo "  - Set FORCE_INSTALL=true to reinstall over existing installation"
    echo "  - Set CONFIGURATION=Debug for debug builds"
    echo "  - Use 'pks --help' to see all available commands"
    echo ""
    echo "${CYAN}Environment Variables:${RESET}"
    echo "  CONFIGURATION  - Build configuration (default: Release)"
    echo "  FORCE_INSTALL  - Force reinstall (default: false)"
    echo ""
    echo "${CYAN}Example:${RESET}"
    echo "  FORCE_INSTALL=true ./install.sh"
}

# Main installation process
main() {
    check_dotnet
    setup_directories
    clean_build
    build_solution
    create_packages
    install_global_tool
    show_packages
    
    if verify_installation; then
        show_usage
    else
        echo ""
        error_exit "Installation verification failed. Please check the warnings above."
    fi
}

# Handle script arguments
case "${1:-}" in
    --help|-h|help)
        echo "PKS CLI Installation Script"
        echo "=========================="
        echo ""
        echo "Usage: $0 [--help]"
        echo ""
        show_usage
        exit 0
        ;;
    *)
        main "$@"
        ;;
esac