#!/bin/bash

# Docker run script for PKS CLI
# Convenient wrapper for running PKS CLI in Docker

set -e

# Configuration
IMAGE_NAME="pks-cli"
IMAGE_TAG="${PKS_CLI_VERSION:-latest}"
REGISTRY="registry.kjeldager.io/si14agents/cli"

# Determine which image to use
if [ "${USE_REGISTRY:-false}" = "true" ]; then
    DOCKER_IMAGE="${REGISTRY}:${IMAGE_TAG}"
else
    DOCKER_IMAGE="${IMAGE_NAME}:${IMAGE_TAG}"
fi

# Check if image exists locally, pull if using registry
if [ "${USE_REGISTRY:-false}" = "true" ]; then
    echo "🐳 Using registry image: ${DOCKER_IMAGE}"
    if ! docker image inspect "${DOCKER_IMAGE}" &> /dev/null; then
        echo "📥 Pulling image from registry..."
        docker pull "${DOCKER_IMAGE}"
    fi
else
    echo "🐳 Using local image: ${DOCKER_IMAGE}"
    if ! docker image inspect "${DOCKER_IMAGE}" &> /dev/null; then
        echo "❌ Local image not found. Build it first with: ./docker-build.sh"
        exit 1
    fi
fi

# Set up volume mounts for project directories
DOCKER_ARGS=""

# Mount current directory if it looks like a project directory
if [ -f "./pks-cli.sln" ] || [ -f "./*.csproj" ] || [ -f "./package.json" ]; then
    DOCKER_ARGS="${DOCKER_ARGS} -v $(pwd):/workspace"
    DOCKER_ARGS="${DOCKER_ARGS} -w /workspace"
fi

# Mount home directory for git config and other user settings
DOCKER_ARGS="${DOCKER_ARGS} -v ${HOME}/.gitconfig:/home/pksuser/.gitconfig:ro"

# Interactive mode
DOCKER_ARGS="${DOCKER_ARGS} -it"

# Run the container
echo "🚀 Running PKS CLI in Docker..."
echo "Command: pks $*"

docker run --rm ${DOCKER_ARGS} "${DOCKER_IMAGE}" "$@"