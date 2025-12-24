# Docker build script for PKS CLI (PowerShell version)
# Builds the Docker image locally for testing

param(
    [string]$ImageTag = "latest",
    [switch]$Minimal
)

# Configuration
$ImageName = "pks-cli"
$Registry = "registry.kjeldager.io/si14agents/cli"
$DockerFile = if ($Minimal) { "Dockerfile.minimal" } else { "Dockerfile" }
$ImageSuffix = if ($Minimal) { "-minimal" } else { "" }
$FullImageName = "${Registry}:${ImageTag}${ImageSuffix}"

Write-Host "Building PKS CLI Docker image..." -ForegroundColor Cyan
Write-Host "Image: $FullImageName" -ForegroundColor White
Write-Host "Dockerfile: $DockerFile" -ForegroundColor White
Write-Host "Context: $(Get-Location)" -ForegroundColor White

# Check if Docker is available
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: Docker is not installed or not in PATH" -ForegroundColor Red
    exit 1
}

# Build the Docker image
Write-Host "Building Docker image..." -ForegroundColor Yellow
try {
    $LocalTag = "${ImageName}:${ImageTag}${ImageSuffix}"
    $LocalLatest = "${ImageName}:latest${ImageSuffix}"
    
    docker build --tag "$LocalTag" --tag "$LocalLatest" --tag "${FullImageName}" --file "$DockerFile" .
    
    if ($LASTEXITCODE -ne 0) {
        throw "Docker build failed"
    }
}
catch {
    Write-Host "ERROR: Docker build failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

Write-Host "SUCCESS: Docker image built successfully!" -ForegroundColor Green
Write-Host "Local tags created:" -ForegroundColor White
Write-Host "  - $LocalTag" -ForegroundColor Gray
Write-Host "  - $LocalLatest" -ForegroundColor Gray
Write-Host "  - ${FullImageName}" -ForegroundColor Gray

# Test the image
Write-Host "Testing the built image..." -ForegroundColor Yellow
try {
    docker run --rm "$LocalLatest" --version 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "WARNING: Version check failed (expected for some CLIs)" -ForegroundColor Yellow
    }
}
catch {
    Write-Host "WARNING: Could not test image version" -ForegroundColor Yellow
}

# Show image size
Write-Host "Checking image size..." -ForegroundColor Yellow
try {
    $sizeInfo = docker images "$LocalLatest" --format "table {{.Size}}" | Select-Object -Skip 1
    Write-Host "Image size: $sizeInfo" -ForegroundColor Cyan
}
catch {
    Write-Host "Could not determine image size" -ForegroundColor Yellow
}

Write-Host "SUCCESS: Build completed successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  - Test locally: docker run --rm -it $LocalLatest" -ForegroundColor Gray
$publishFlag = if ($Minimal) { " -Minimal" } else { "" }
Write-Host "  - Publish: .\scripts\docker-publish.ps1 $ImageTag$publishFlag" -ForegroundColor Gray