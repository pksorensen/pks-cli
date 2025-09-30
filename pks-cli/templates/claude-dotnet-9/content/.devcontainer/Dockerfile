FROM mcr.microsoft.com/devcontainers/dotnet:9.0-bookworm AS base

# Install Node.js and npm
# Option 1: Using NodeSource repository (recommended for latest versions)
RUN curl -fsSL https://deb.nodesource.com/setup_lts.x | bash - && \
    apt-get install -y nodejs
    
# Install Claude
RUN npm install -g @anthropic-ai/claude-code


