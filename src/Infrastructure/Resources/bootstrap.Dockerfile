# PKS CLI Bootstrap Container
# Executes devcontainer operations in Linux environment for cross-platform bash support

FROM mcr.microsoft.com/devcontainers/base:0-alpine-3.20

LABEL maintainer="PKS CLI"
LABEL description="Bootstrap container for PKS devcontainer operations"
LABEL version="1.0.0"

# Install essential development tools
RUN apk add --no-cache \
    bash \
    git \
    git-lfs \
    nodejs \
    npm \
    docker-cli \
    docker-cli-buildx \
    docker-cli-compose \
    curl \
    wget \
    ca-certificates \
    tar \
    gzip

# Install devcontainer CLI globally
RUN npm install -g @devcontainers/cli@0.80.3

# Configure npm to use system CA certificates
RUN npm config set cafile /etc/ssl/certs/ca-certificates.crt

# Set working directory
WORKDIR /workspaces

# Create helper script using RUN command (backward compatible)
RUN echo '#!/bin/sh' > /usr/local/bin/pks-devcontainer-helper && \
    echo 'set -e' >> /usr/local/bin/pks-devcontainer-helper && \
    echo '' >> /usr/local/bin/pks-devcontainer-helper && \
    echo '# Helper script for PKS CLI devcontainer operations' >> /usr/local/bin/pks-devcontainer-helper && \
    echo '' >> /usr/local/bin/pks-devcontainer-helper && \
    echo 'case "$1" in' >> /usr/local/bin/pks-devcontainer-helper && \
    echo '    up)' >> /usr/local/bin/pks-devcontainer-helper && \
    echo '        devcontainer up --workspace-folder "$2" 2>&1' >> /usr/local/bin/pks-devcontainer-helper && \
    echo '        ;;' >> /usr/local/bin/pks-devcontainer-helper && \
    echo '    exec)' >> /usr/local/bin/pks-devcontainer-helper && \
    echo '        devcontainer exec --workspace-folder "$2" "${@:3}" 2>&1' >> /usr/local/bin/pks-devcontainer-helper && \
    echo '        ;;' >> /usr/local/bin/pks-devcontainer-helper && \
    echo '    *)' >> /usr/local/bin/pks-devcontainer-helper && \
    echo '        echo "Unknown command: $1"' >> /usr/local/bin/pks-devcontainer-helper && \
    echo '        exit 1' >> /usr/local/bin/pks-devcontainer-helper && \
    echo '        ;;' >> /usr/local/bin/pks-devcontainer-helper && \
    echo 'esac' >> /usr/local/bin/pks-devcontainer-helper && \
    chmod +x /usr/local/bin/pks-devcontainer-helper

# Healthcheck to ensure Docker socket is accessible
HEALTHCHECK --interval=10s --timeout=3s --start-period=5s \
    CMD docker version || exit 1

CMD ["sleep", "infinity"]
