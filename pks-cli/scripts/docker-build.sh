#!/bin/bash

# Docker build script for PKS CLI
# Builds the Docker image locally for testing

set -e

# Parse arguments
MINIMAL=false
IMAGE_TAG="latest"

while [[ $# -gt 0 ]]; do
    case $1 in
        --minimal)
            MINIMAL=true
            shift
            ;;
        *)
            IMAGE_TAG="$1"
            shift
            ;;
    esac
done

# Configuration
IMAGE_NAME="pks-cli"
REGISTRY="registry.kjeldager.io/si14agents/cli"
DOCKERFILE="Dockerfile"
IMAGE_SUFFIX=""

if [ "$MINIMAL" = true ]; then
    DOCKERFILE="Dockerfile.minimal"
    IMAGE_SUFFIX="-minimal"
fi

FULL_IMAGE_NAME="${REGISTRY}:${IMAGE_TAG}${IMAGE_SUFFIX}"

echo "🐳 Building PKS CLI Docker image..."
echo "Image: ${FULL_IMAGE_NAME}"
echo "Context: $(pwd)"

# Check if Docker is available
if ! command -v docker &> /dev/null; then
    echo "❌ Docker is not installed or not in PATH"
    exit 1
fi

# Build the Docker image
echo "🔨 Building Docker image..."
docker build \
    --tag "${IMAGE_NAME}:${IMAGE_TAG}" \
    --tag "${IMAGE_NAME}:latest" \
    --tag "${FULL_IMAGE_NAME}" \
    --file Dockerfile \
    .

echo "✅ Docker image built successfully!"
echo "Local tags created:"
echo "  - ${IMAGE_NAME}:${IMAGE_TAG}"
echo "  - ${IMAGE_NAME}:latest" 
echo "  - ${FULL_IMAGE_NAME}"

# Test the image
echo "🧪 Testing the built image..."
docker run --rm "${IMAGE_NAME}:latest" --version || true

echo "✅ Build completed successfully!"
echo ""
echo "Next steps:"
echo "  - Test locally: docker run --rm -it ${IMAGE_NAME}:latest"
echo "  - Publish: ./docker-publish.sh ${IMAGE_TAG}"