# PKS Fullstack DevContainer
# Uses pre-built base image for fast container setup
# Base image contains: Node.js 20, .NET 8+10, Playwright, DevTunnel, GCM, Aspire CLI, Claude Code
FROM ghcr.io/pksorensen/pks-fullstack-base:BASE_IMAGE_TAG

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

# Configure passwordless sudo for Docker-in-Docker
# The docker-init.sh script needs to run dockerd and related commands as root
RUN echo "node ALL=(root) NOPASSWD: /usr/local/share/docker-init.sh, /usr/bin/dockerd, /usr/bin/containerd, /usr/bin/pkill" > /etc/sudoers.d/node-docker && \
  chmod 0440 /etc/sudoers.d/node-docker

USER node
