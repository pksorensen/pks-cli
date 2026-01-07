#!/bin/bash
# Publishes self-contained builds for all platforms

set -euo pipefail

# Configuration
VERSION=${1:-1.0.0}
OUTPUT_DIR=${2:-./npm-dist}
CONFIGURATION=${CONFIGURATION:-Release}

# Platform definitions
PLATFORMS=(
  "linux-x64"
  "linux-arm64"
  "osx-x64"
  "osx-arm64"
  "win-x64"
  "win-arm64"
)

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Find solution root
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOLUTION_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT_PATH="$SOLUTION_ROOT/src/pks-cli.csproj"

echo -e "${GREEN}PKS CLI Self-Contained Build${NC}"
echo "================================"
echo "Version: $VERSION"
echo "Output: $OUTPUT_DIR"
echo "Configuration: $CONFIGURATION"
echo ""

# Validate project file exists
if [ ! -f "$PROJECT_PATH" ]; then
  echo -e "${RED}Error: Project file not found at $PROJECT_PATH${NC}"
  exit 1
fi

# Create output directory
mkdir -p "$OUTPUT_DIR"

# Track success/failure
SUCCESS_COUNT=0
FAILURE_COUNT=0
FAILED_PLATFORMS=()

# Build each platform
for platform in "${PLATFORMS[@]}"; do
  echo -e "${YELLOW}Building for $platform...${NC}"

  PLATFORM_OUTPUT="$OUTPUT_DIR/$platform"

  # Run dotnet publish
  if dotnet publish "$PROJECT_PATH" \
    --configuration "$CONFIGURATION" \
    --runtime "$platform" \
    --self-contained true \
    --output "$PLATFORM_OUTPUT" \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=false \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:PublishReadyToRun=true \
    -p:Version="$VERSION" \
    -p:PublishSelfContained=true \
    -p:EmbedTemplates=true \
    --nologo \
    --verbosity quiet; then

    echo -e "${GREEN}✓ Successfully built $platform${NC}"
    ((SUCCESS_COUNT++))

    # Display binary size
    if [ "$platform" == "win-x64" ] || [ "$platform" == "win-arm64" ]; then
      BINARY="$PLATFORM_OUTPUT/pks.exe"
    else
      BINARY="$PLATFORM_OUTPUT/pks"
    fi

    if [ -f "$BINARY" ]; then
      SIZE=$(du -h "$BINARY" | cut -f1)
      echo "  Size: $SIZE"
    fi
  else
    echo -e "${RED}✗ Failed to build $platform${NC}"
    ((FAILURE_COUNT++))
    FAILED_PLATFORMS+=("$platform")
  fi

  echo ""
done

# Summary
echo "================================"
echo -e "${GREEN}Build Summary${NC}"
echo "Success: $SUCCESS_COUNT/${#PLATFORMS[@]}"
echo "Failed: $FAILURE_COUNT/${#PLATFORMS[@]}"

if [ $FAILURE_COUNT -gt 0 ]; then
  echo ""
  echo -e "${RED}Failed platforms:${NC}"
  for platform in "${FAILED_PLATFORMS[@]}"; do
    echo "  - $platform"
  done
  exit 1
fi

echo ""
echo -e "${GREEN}All platforms built successfully!${NC}"
echo "Output directory: $OUTPUT_DIR"

# List all binaries
echo ""
echo "Binaries:"
find "$OUTPUT_DIR" -type f \( -name "pks" -o -name "pks.exe" \) -exec ls -lh {} \;

exit 0
