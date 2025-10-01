# Docker run script for PKS CLI (PowerShell version)
# Convenient wrapper for running PKS CLI in Docker

param(
    [Parameter(ValueFromRemainingArguments)]
    [string[]]$Arguments
)

# Configuration
$ImageName = "pks-cli"
$ImageTag = if ($env:PKS_CLI_VERSION) { $env:PKS_CLI_VERSION } else { "latest" }
$Registry = "registry.kjeldager.io/si14agents/cli"

# Determine which image to use
$UseRegistry = $env:USE_REGISTRY -eq "true"
if ($UseRegistry) {
    $DockerImage = "${Registry}:${ImageTag}"
} else {
    $DockerImage = "${ImageName}:${ImageTag}"
}

# Check if image exists locally, pull if using registry
if ($UseRegistry) {
    Write-Host "🐳 Using registry image: $DockerImage" -ForegroundColor Cyan
    try {
        docker image inspect "$DockerImage" 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "📥 Pulling image from registry..." -ForegroundColor Yellow
            docker pull "$DockerImage"
            if ($LASTEXITCODE -ne 0) {
                Write-Host "❌ Failed to pull image from registry" -ForegroundColor Red
                exit 1
            }
        }
    }
    catch {
        Write-Host "❌ Error checking registry image: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "🐳 Using local image: $DockerImage" -ForegroundColor Cyan
    try {
        docker image inspect "$DockerImage" 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "❌ Local image not found. Build it first with: .\scripts\docker-build.ps1" -ForegroundColor Red
            exit 1
        }
    }
    catch {
        Write-Host "❌ Error checking local image: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

# Set up volume mounts for project directories
$DockerArgs = @()

# Mount current directory if it looks like a project directory
$CurrentPath = Get-Location
if ((Test-Path "pks-cli.sln") -or (Get-ChildItem "*.csproj" -ErrorAction SilentlyContinue) -or (Test-Path "package.json")) {
    $DockerArgs += "-v"
    $DockerArgs += "${CurrentPath}:/workspace"
    $DockerArgs += "-w"
    $DockerArgs += "/workspace"
}

# Mount git config if it exists
$GitConfigPath = Join-Path $env:USERPROFILE ".gitconfig"
if (Test-Path $GitConfigPath) {
    $DockerArgs += "-v"
    $DockerArgs += "${GitConfigPath}:/home/pksuser/.gitconfig:ro"
}

# Interactive mode
$DockerArgs += "-it"
$DockerArgs += "--rm"

# Run the container
Write-Host "🚀 Running PKS CLI in Docker..." -ForegroundColor Green
if ($Arguments) {
    Write-Host "Command: pks $($Arguments -join ' ')" -ForegroundColor Gray
} else {
    Write-Host "Command: pks (default)" -ForegroundColor Gray
}

try {
    $AllArgs = $DockerArgs + @($DockerImage) + $Arguments
    & docker run @AllArgs
}
catch {
    Write-Host "❌ Error running Docker container: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}