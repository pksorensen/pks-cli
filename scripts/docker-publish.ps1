# Docker publish script for PKS CLI (PowerShell version)
# Publishes the Docker image to registry.kjeldager.io/si14agents/cli

param(
    [string]$ImageTag = "latest"
)

# Configuration
$ImageName = "pks-cli"
$Registry = "registry.kjeldager.io/si14agents/cli"
$FullImageName = "${Registry}:${ImageTag}"

Write-Host "Publishing PKS CLI Docker image..." -ForegroundColor Cyan
Write-Host "Target: $FullImageName" -ForegroundColor White

# Check if Docker is available
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: Docker is not installed or not in PATH" -ForegroundColor Red
    exit 1
}

# Check if image exists locally
try {
    docker image inspect "${ImageName}:${ImageTag}" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Image not found"
    }
}
catch {
    Write-Host "ERROR: Local image ${ImageName}:${ImageTag} not found" -ForegroundColor Red
    Write-Host "Run .\scripts\docker-build.ps1 $ImageTag first" -ForegroundColor Yellow
    exit 1
}

# Login to registry (optional - you might need to customize this)
Write-Host "Logging into registry..." -ForegroundColor Yellow
Write-Host "Note: Make sure you're logged into registry.kjeldager.io" -ForegroundColor Gray
Write-Host "If not logged in, run: docker login registry.kjeldager.io" -ForegroundColor Gray

# Check if logged in by attempting to query the registry
if ($ImageTag -ne "latest") {
    try {
        docker manifest inspect "${Registry}:latest" 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "WARNING: Cannot access registry. Please ensure you're logged in:" -ForegroundColor Yellow
            Write-Host "   docker login registry.kjeldager.io" -ForegroundColor Gray
            $response = Read-Host "Continue anyway? (y/N)"
            if ($response -notmatch "^[Yy]$") {
                exit 1
            }
        }
    }
    catch {
        Write-Host "WARNING: Registry access check failed" -ForegroundColor Yellow
    }
}

# Tag for registry if not already tagged
try {
    docker image inspect "${FullImageName}" 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Tagging image for registry..." -ForegroundColor Yellow
        docker tag "${ImageName}:${ImageTag}" "${FullImageName}"
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to tag image"
        }
    }
}
catch {
    Write-Host "ERROR: Failed to tag image: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# Push to registry
Write-Host "Pushing to registry..." -ForegroundColor Yellow
try {
    docker push "${FullImageName}"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to push image"
    }
}
catch {
    Write-Host "ERROR: Failed to push image: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# Also push as latest if this is a versioned tag
if ($ImageTag -ne "latest") {
    Write-Host "Also pushing as latest..." -ForegroundColor Yellow
    try {
        docker tag "${ImageName}:${ImageTag}" "${Registry}:latest"
        docker push "${Registry}:latest"
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to push latest tag"
        }
    }
    catch {
        Write-Host "ERROR: Failed to push latest tag: $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host "SUCCESS: Successfully published to registry!" -ForegroundColor Green
Write-Host ""
Write-Host "Published images:" -ForegroundColor White
Write-Host "  - ${FullImageName}" -ForegroundColor Gray
if ($ImageTag -ne "latest") {
    Write-Host "  - ${Registry}:latest" -ForegroundColor Gray
}
Write-Host ""
Write-Host "Usage:" -ForegroundColor White
Write-Host "  docker pull ${FullImageName}" -ForegroundColor Gray
Write-Host "  docker run --rm -it ${FullImageName}" -ForegroundColor Gray