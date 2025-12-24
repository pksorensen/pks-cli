FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet

FROM node:20

ARG TZ
ENV TZ="$TZ"

ARG CLAUDE_CODE_VERSION=latest
ARG WORKSPACE_NAME=workspace

# Install Microsoft package signing key and repository
RUN wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O packages-microsoft-prod.deb && \
  dpkg -i packages-microsoft-prod.deb && \
  rm packages-microsoft-prod.deb

# Install basic development tools and iptables/ipset
RUN apt update && apt install -y less \
  git \
  procps \
  sudo \
  fzf \
  zsh \
  man-db \
  unzip \
  gnupg2 \
  gh \
  iptables \
  ipset \
  iproute2 \
  dnsutils \
  aggregate \
  jq \
  dotnet-sdk-8.0

  # Install FFmpeg and other dependencies
RUN apt update && apt install -y \
    ffmpeg \
    python3 \
    make \
    g++ \
    git
    
# Install playwright dependencies
RUN apt update && apt install -y \
    libnss3 \
    libnspr4 \
    libatk1.0-0 \
    libatk-bridge2.0-0 \
    libcups2 \
    libdrm2 \
    libxkbcommon0 \
    libxcomposite1 \
    libxdamage1 \
    libxfixes3 \
    libxrandr2 \
    libgbm1 \
    libasound2 \
    libgtk-3-0 \
    libgtk-4-1 \
    libgdk-pixbuf2.0-0 \
    libpango-1.0-0 \
    libcairo2 \
    libgraphene-1.0-0 \
    libwoff1 \
    libavif15 \
    libharfbuzz-icu0 \
    libenchant-2-2 \
    libhyphen0 \
    libmanette-0.2-0 \
    libgles2 \
    libgstreamer1.0-0 \
    gstreamer1.0-plugins-base \
    gstreamer1.0-plugins-good \
    gstreamer1.0-plugins-bad \
    gstreamer1.0-gl

# Install DevTunnel CLI dependencies
RUN apt update && apt install -y libsecret-1-0

RUN apt-get clean && rm -rf /var/lib/apt/lists/*

# Ensure default node user has access to /usr/local/share
RUN mkdir -p /usr/local/share/npm-global && \
  chown -R node:node /usr/local/share

# Copy .NET from the official image
# Copy .NET 10 SDK to the same dotnet directory
COPY --from=dotnet /usr/share/dotnet/sdk /usr/share/dotnet/sdk/
COPY --from=dotnet /usr/share/dotnet/shared /usr/share/dotnet/shared/
COPY --from=dotnet /usr/share/dotnet/packs /usr/share/dotnet/packs/
COPY --from=dotnet /usr/share/dotnet/templates /usr/share/dotnet/templates/

# Set DOTNET environment variables
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH=$PATH:/usr/share/dotnet


ARG USERNAME=node

# Persist bash history.
RUN SNIPPET="export PROMPT_COMMAND='history -a' && export HISTFILE=/commandhistory/.bash_history" \
  && mkdir /commandhistory \
  && touch /commandhistory/.bash_history \
  && chown -R $USERNAME /commandhistory

# Set `DEVCONTAINER` environment variable to help with orientation
ENV DEVCONTAINER=true

# Create workspace and config directories and set permissions
RUN mkdir -p /workspaces/${WORKSPACE_NAME} /home/node/.claude && \
  chown -R node:node /workspaces/${WORKSPACE_NAME} /home/node/.claude

WORKDIR /workspaces/${WORKSPACE_NAME}

ARG GIT_DELTA_VERSION=0.18.2
RUN ARCH=$(dpkg --print-architecture) && \
  wget "https://github.com/dandavison/delta/releases/download/${GIT_DELTA_VERSION}/git-delta_${GIT_DELTA_VERSION}_${ARCH}.deb" && \
  sudo dpkg -i "git-delta_${GIT_DELTA_VERSION}_${ARCH}.deb" && \
  rm "git-delta_${GIT_DELTA_VERSION}_${ARCH}.deb"

# Install Playwright browsers
RUN npx -y playwright@1.56.1 install --with-deps chromium


# Set up non-root user
USER node

# ============================================================================
# DevTunnel CLI Installation
# ============================================================================
# Dependencies: libsecret-1-0 (installed above as root)
# Install location: ~/bin/devtunnel
# PATH: Requires ~/bin to be in PATH (set below)
# Must run as node user (after USER node) to install to /home/node/bin
RUN set -e && \
    mkdir -p ~/bin && \
    ARCH="$(uname -m)" && \
    echo "Detected architecture: ${ARCH}" && \
    if [ "$(expr substr ${ARCH} 1 6)" = "x86_64" ]; then \
        echo "Downloading DevTunnel CLI..." && \
        curl -fsSL -o ~/bin/devtunnel https://tunnelsassetsprod.blob.core.windows.net/cli/linux-x64-devtunnel && \
        chmod +x ~/bin/devtunnel && \
        echo "DevTunnel CLI downloaded" && \
        ls -lh ~/bin/devtunnel && \
        ~/bin/devtunnel --version && \
        echo "✓ DevTunnel CLI installed successfully"; \
    else \
        echo "ERROR: Unsupported architecture for DevTunnel: ${ARCH}" && \
        exit 1; \
    fi
# ============================================================================

# ============================================================================
# Git Credential Manager (GCM) Installation
# ============================================================================
# Install GCM for Azure DevOps authentication with device code flow
# Install location: ~/.local/bin/git-credential-manager
# PATH: Requires ~/.local/bin to be in PATH (set below)
ARG GCM_VERSION=2.6.0
RUN set -e && \
    mkdir -p ~/.local/bin && \
    echo "Downloading Git Credential Manager..." && \
    curl -fsSL -o /tmp/gcm.tar.gz https://github.com/git-ecosystem/git-credential-manager/releases/download/v${GCM_VERSION}/gcm-linux_amd64.${GCM_VERSION}.tar.gz && \
    tar -xzf /tmp/gcm.tar.gz -C /tmp && \
    mv /tmp/git-credential-manager ~/.local/bin/ && \
    chmod +x ~/.local/bin/git-credential-manager && \
    rm -rf /tmp/gcm.tar.gz /tmp/git-credential-manager-* && \
    echo "✓ Git Credential Manager installed successfully"

# Configure GCM for Azure DevOps with device code flow at system level
# This ensures container-specific config doesn't pollute host's .gitconfig
USER root
RUN git config --system credential.helper "!/home/node/.local/bin/git-credential-manager" && \
    git config --system credential.credentialStore plaintext && \
    git config --system credential.msauthFlow devicecode && \
    git config --system credential.azreposCredentialType oauth && \
    git config --system credential.useHttpPath true && \
    echo "✓ Git Credential Manager configured for Azure DevOps (system-level)"
USER node
# ============================================================================

# ============================================================================
# .NET Aspire CLI Installation
# ============================================================================
# Install location: ~/.dotnet/tools/dotnet-aspire
# PATH: Requires ~/.dotnet/tools to be in PATH (set below)
ENV DOTNET_CLI_HOME=/home/node
RUN dotnet tool install -g Aspire.Cli --prerelease
# ============================================================================

# Install global packages and configure PATH
ENV NPM_CONFIG_PREFIX=/usr/local/share/npm-global
# PATH includes:
#   - /usr/local/share/npm-global/bin (npm global packages)
#   - /home/node/.dotnet/tools (Aspire CLI)
#   - /home/node/.local/bin (other user-installed tools)
#   - /home/node/bin (DevTunnel CLI)
ENV PATH=$PATH:/usr/local/share/npm-global/bin:/home/node/.dotnet/tools:/home/node/.local/bin:/home/node/bin

# Set the default shell to zsh rather than sh
ENV SHELL=/bin/zsh

# Default powerline10k theme
ARG ZSH_IN_DOCKER_VERSION=1.2.0
RUN sh -c "$(wget -O- https://github.com/deluan/zsh-in-docker/releases/download/v${ZSH_IN_DOCKER_VERSION}/zsh-in-docker.sh)" -- \
  -p git \
  -p fzf \
  -a "source /usr/share/doc/fzf/examples/key-bindings.zsh" \
  -a "source /usr/share/doc/fzf/examples/completion.zsh" \
  -a "export PROMPT_COMMAND='history -a' && export HISTFILE=/commandhistory/.bash_history" \
  -a "export PATH=\$PATH:/home/node/.dotnet/tools:/home/node/.local/bin:/home/node/bin" \
  -x

# Install Claude
RUN npm install -g @anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}

# Copy and set up firewall script
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