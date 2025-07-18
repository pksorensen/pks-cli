#!/bin/bash

# PKS CLI Installation Script
echo "🚀 Installing PKS CLI - The Next Agentic CLI for .NET Developers"
echo ""

# Check if .NET is installed
if ! command -v dotnet &> /dev/null; then
    echo "❌ .NET is not installed. Please install .NET 8.0 or higher first."
    echo "Visit: https://dotnet.microsoft.com/download"
    exit 1
fi

# Get .NET version
DOTNET_VERSION=$(dotnet --version)
echo "✅ Found .NET version: $DOTNET_VERSION"

# Navigate to src directory
cd "$(dirname "$0")/src" || exit 1

# Build and pack the application
echo "🔨 Building PKS CLI..."
dotnet build --configuration Release

echo "📦 Creating package..."
dotnet pack --configuration Release

# Install as global tool
echo "🌍 Installing as global tool..."
dotnet tool install -g --add-source ./bin/Release pks-cli --force

# Verify installation
echo "🧪 Verifying installation..."
if command -v pks &> /dev/null; then
    echo ""
    echo "🎉 PKS CLI installed successfully!"
    echo ""
    echo "Try these commands:"
    echo "  pks --help              # Show help"
    echo "  pks ascii 'Hello'       # Generate ASCII art"
    echo "  pks init my-project     # Initialize a new project"
    echo "  pks agent create        # Create an AI agent"
    echo ""
    echo "🤖 Welcome to the future of .NET development!"
else
    echo "❌ Installation failed. Please check the error messages above."
    exit 1
fi