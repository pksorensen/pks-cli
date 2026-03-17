# PKS Fullstack DevContainer
# Uses pre-built base image for fast container setup
# Base image contains: Node.js 20, .NET 8+10, Playwright, DevTunnel, GCM, Aspire CLI, Claude Code

# Build argument for base image tag - can be overridden in devcontainer.json or at build time
# Available tags: latest, 1.0.0, 1.1.0, etc.
# See: https://github.com/pksorensen/pks-cli/pkgs/container/pks-fullstack-base
ARG BASE_IMAGE_TAG=latest

FROM ghcr.io/pksorensen/pks-fullstack-base:${BASE_IMAGE_TAG}

# ============================================================================
# Firewall Configuration
# ============================================================================
# Copy and set up firewall script
# This is the customizable part - edit init-firewall.sh to add/remove allowed domains
USER root
COPY init-firewall.sh /usr/local/bin/
RUN chmod +x /usr/local/bin/init-firewall.sh && \
  echo "node ALL=(root) NOPASSWD: /usr/local/bin/init-firewall.sh" > /etc/sudoers.d/node-firewall && \
  chmod 0440 /etc/sudoers.d/node-firewall

# Install tmux for Claude Code agent team split-pane mode
# Install libnss3-tools for dotnet dev-certs trust (certutil needed for browser/Playwright cert trust)
RUN apt-get update && apt-get install -y --no-install-recommends tmux libnss3-tools && \
  # Install ttyd for web-based terminal access (useful with tmux for split-pane viewing)
  curl -fsSL -o /tmp/ttyd https://github.com/tsl0922/ttyd/releases/download/1.7.7/ttyd.x86_64 && \
  install -m 755 /tmp/ttyd /usr/local/bin/ttyd && rm /tmp/ttyd && \
  rm -rf /var/lib/apt/lists/*

# Configure passwordless sudo for Docker-in-Docker
# The docker-init.sh script needs to run dockerd and related commands as root
RUN echo "node ALL=(root) NOPASSWD: /usr/local/share/docker-init.sh, /usr/bin/dockerd, /usr/bin/containerd, /usr/bin/pkill" > /etc/sudoers.d/node-docker && \
  chmod 0440 /etc/sudoers.d/node-docker

# Configure tmux mouse scroll support
COPY .tmux.conf /home/node/.tmux.conf
RUN chown node:node /home/node/.tmux.conf

USER node
