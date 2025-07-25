#!/bin/bash

# Docker publish script for PKS CLI
# Publishes the Docker image to registry.kjeldager.io/si14agents/cli

set -e

# Configuration
IMAGE_NAME="pks-cli"
IMAGE_TAG="${1:-latest}"
REGISTRY="registry.kjeldager.io/si14agents/cli"
FULL_IMAGE_NAME="${REGISTRY}:${IMAGE_TAG}"

echo "🚀 Publishing PKS CLI Docker image..."
echo "Target: ${FULL_IMAGE_NAME}"

# Check if Docker is available
if ! command -v docker &> /dev/null; then
    echo "❌ Docker is not installed or not in PATH"
    exit 1
fi

# Check if image exists locally
if ! docker image inspect "${IMAGE_NAME}:${IMAGE_TAG}" &> /dev/null; then
    echo "❌ Local image ${IMAGE_NAME}:${IMAGE_TAG} not found"
    echo "Run ./docker-build.sh ${IMAGE_TAG} first"
    exit 1
fi

# Login to registry (optional - you might need to customize this)
echo "🔐 Logging into registry..."
echo "Note: Make sure you're logged into registry.kjeldager.io"
echo "If not logged in, run: docker login registry.kjeldager.io"

# Check if logged in by attempting to query the registry
if ! docker manifest inspect "${REGISTRY}:latest" &> /dev/null && [ "${IMAGE_TAG}" != "latest" ]; then
    echo "⚠️  Cannot access registry. Please ensure you're logged in:"
    echo "   docker login registry.kjeldager.io"
    read -p "Continue anyway? (y/N): " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        exit 1
    fi
fi

# Tag for registry if not already tagged
if ! docker image inspect "${FULL_IMAGE_NAME}" &> /dev/null; then
    echo "🏷️  Tagging image for registry..."
    docker tag "${IMAGE_NAME}:${IMAGE_TAG}" "${FULL_IMAGE_NAME}"
fi

# Push to registry
echo "📤 Pushing to registry..."
docker push "${FULL_IMAGE_NAME}"

# Also push as latest if this is a versioned tag
if [ "${IMAGE_TAG}" != "latest" ]; then
    echo "📤 Also pushing as latest..."
    docker tag "${IMAGE_NAME}:${IMAGE_TAG}" "${REGISTRY}:latest"
    docker push "${REGISTRY}:latest"
fi

echo "✅ Successfully published to registry!"
echo ""
echo "Published images:"
echo "  - ${FULL_IMAGE_NAME}"
if [ "${IMAGE_TAG}" != "latest" ]; then
    echo "  - ${REGISTRY}:latest"
fi
echo ""
echo "Usage:"
echo "  docker pull ${FULL_IMAGE_NAME}"
echo "  docker run --rm -it ${FULL_IMAGE_NAME}"