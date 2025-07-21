#!/bin/bash

# Script to update version in all csproj files
# Usage: ./update-version.sh <version>

set -e

VERSION=$1

if [ -z "$VERSION" ]; then
    echo "Error: Version not provided"
    echo "Usage: $0 <version>"
    exit 1
fi

echo "📝 Updating project versions to: $VERSION"

# Find all csproj files and update version properties
find . -name "*.csproj" -type f | while read -r file; do
    echo "  Updating: $file"
    
    # Update Version property
    if grep -q "<Version>" "$file"; then
        sed -i "s|<Version>.*</Version>|<Version>$VERSION</Version>|g" "$file"
    else
        # Add Version if it doesn't exist
        sed -i "/<PropertyGroup>/a\    <Version>$VERSION</Version>" "$file"
    fi
    
    # Update PackageVersion property
    if grep -q "<PackageVersion>" "$file"; then
        sed -i "s|<PackageVersion>.*</PackageVersion>|<PackageVersion>$VERSION</PackageVersion>|g" "$file"
    fi
    
    # Update AssemblyVersion property
    if grep -q "<AssemblyVersion>" "$file"; then
        sed -i "s|<AssemblyVersion>.*</AssemblyVersion>|<AssemblyVersion>$VERSION</AssemblyVersion>|g" "$file"
    fi
    
    # Update FileVersion property
    if grep -q "<FileVersion>" "$file"; then
        sed -i "s|<FileVersion>.*</FileVersion>|<FileVersion>$VERSION</FileVersion>|g" "$file"
    fi
done

echo "✅ Version update complete!"

# Display updated files
echo ""
echo "📋 Updated files:"
find . -name "*.csproj" -type f -exec grep -l "<Version>$VERSION</Version>" {} \;