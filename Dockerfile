# Use the official .NET 8 SDK image for building
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

# Set working directory
WORKDIR /app

# Copy solution file and project files
COPY pks-cli.sln ./
COPY src/pks-cli.csproj src/
COPY templates/devcontainer/PKS.Templates.DevContainer.csproj templates/devcontainer/
COPY tests/PKS.CLI.Tests.csproj tests/

# Restore dependencies for Linux runtime
RUN dotnet restore -r linux-musl-x64

# Copy source code and templates
COPY src/ src/
COPY templates/ templates/
COPY tests/ tests/

# Build and publish the application with optimizations
# Note: PublishReadyToRun=false because R2R is not supported for linux-musl-x64 (Alpine)
WORKDIR /app/src
RUN dotnet publish -c Release -o /app/publish \
    -r linux-musl-x64 \
    --self-contained false \
    --no-restore \
    /p:PublishReadyToRun=false \
    /p:PublishSingleFile=false \
    /p:PublishTrimmed=false

# Use the ASP.NET Core 8 runtime Alpine image (required for ModelContextProtocol.AspNetCore)
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime

# Install git and other essential tools using Alpine package manager
RUN apk add --no-cache \
    git \
    curl \
    wget \
    unzip \
    bash


# Set working directory
WORKDIR /app

# Copy published application
COPY --from=build /app/publish .

# Create a non-root user for security (Alpine Linux syntax)
RUN addgroup -g 1000 pksuser && \
    adduser -D -s /bin/bash -u 1000 -G pksuser pksuser && \
    chown -R pksuser:pksuser /app


USER pksuser

# Set environment variables
ENV DOTNET_ENABLE_DIAGNOSTICS=0
ENV PATH="/app:${PATH}"

# Create a symlink to make pks command available globally
RUN mkdir -p /home/pksuser/.local/bin && \
    ln -s /app/pks-cli /home/pksuser/.local/bin/pks
ENV PATH="/home/pksuser/.local/bin:${PATH}"


# Expose any ports if needed (adjust as necessary)
# EXPOSE 8080

# Set the entry point
ENTRYPOINT ["dotnet", "/app/pks-cli.dll"]

# Default command (show help)
CMD ["--help"]