# Test script to validate PowerShell Docker scripts syntax
# Run this to check if the scripts have proper syntax

Write-Host "🧪 Testing PowerShell Docker scripts syntax..." -ForegroundColor Cyan

$scripts = @(
    "docker-build.ps1",
    "docker-publish.ps1", 
    "docker-run.ps1"
)

$allPassed = $true

foreach ($script in $scripts) {
    $scriptPath = Join-Path $PSScriptRoot $script
    
    if (Test-Path $scriptPath) {
        Write-Host "Testing $script..." -ForegroundColor Yellow
        
        try {
            # Parse the script to check syntax
            $null = [System.Management.Automation.PSParser]::Tokenize((Get-Content $scriptPath -Raw), [ref]$null)
            Write-Host "  ✅ $script - Syntax OK" -ForegroundColor Green
        }
        catch {
            Write-Host "  ❌ $script - Syntax Error: $($_.Exception.Message)" -ForegroundColor Red
            $allPassed = $false
        }
    }
    else {
        Write-Host "  ⚠️  $script - File not found" -ForegroundColor Yellow
        $allPassed = $false
    }
}

Write-Host ""
if ($allPassed) {
    Write-Host "✅ All PowerShell Docker scripts passed syntax validation!" -ForegroundColor Green
} else {
    Write-Host "❌ Some scripts have issues. Please review the errors above." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Test build: .\docker-build.ps1" -ForegroundColor Gray
Write-Host "  2. Test run: .\docker-run.ps1 --help" -ForegroundColor Gray