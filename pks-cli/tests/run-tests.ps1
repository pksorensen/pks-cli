# Test runner script for PKS CLI with categorization and timeout handling
param(
    [string]$Category = "All",
    [string]$Speed = "All", 
    [string]$Reliability = "All",
    [switch]$ExcludeUnstable,
    [switch]$ExcludeSlow,
    [switch]$OnlyFast,
    [int]$TimeoutMinutes = 5,
    [switch]$Verbose,
    [switch]$Debug
)

Write-Host "PKS CLI Test Runner" -ForegroundColor Cyan
Write-Host "==================" -ForegroundColor Cyan

# Build filter expression
$filter = @()

# Category filter
if ($Category -ne "All") {
    $filter += "Category=$Category"
}

# Speed filter
if ($Speed -ne "All") {
    $filter += "Speed=$Speed"
} elseif ($OnlyFast) {
    $filter += "Speed=Fast"
} elseif ($ExcludeSlow) {
    $filter += "Speed!=Slow"
}

# Reliability filter
if ($Reliability -ne "All") {
    $filter += "Reliability=$Reliability"
} elseif ($ExcludeUnstable) {
    $filter += "Reliability!=Unstable"
}

# Build dotnet test command
$testArgs = @(
    "test"
    "--settings", ".runsettings"
    "--logger", "console;verbosity=normal"
    "--logger", "trx"
    "--logger", "html"
    "--results-directory", "../../test-artifacts/results"
    "--collect", "XPlat Code Coverage"
)

if ($filter.Count -gt 0) {
    $filterExpression = $filter -join "&"
    $testArgs += "--filter", $filterExpression
    Write-Host "Running tests with filter: $filterExpression" -ForegroundColor Yellow
} else {
    Write-Host "Running all tests" -ForegroundColor Green
}

if ($Verbose) {
    $testArgs += "--verbosity", "detailed"
}

if ($Debug) {
    Write-Host "Test command: dotnet $($testArgs -join ' ')" -ForegroundColor Magenta
}

# Set timeout
$timeoutMs = $TimeoutMinutes * 60 * 1000

Write-Host "Starting test execution with $TimeoutMinutes minute timeout..." -ForegroundColor Green

# Run tests with timeout
$job = Start-Job -ScriptBlock {
    param($testArgs)
    Set-Location $using:PWD
    & dotnet @testArgs
} -ArgumentList @(,$testArgs)

$completed = Wait-Job $job -Timeout ($TimeoutMinutes * 60)

if ($completed) {
    $output = Receive-Job $job
    Write-Host $output
    $exitCode = $job.State -eq "Completed" ? 0 : 1
} else {
    Write-Host "Tests timed out after $TimeoutMinutes minutes. Stopping..." -ForegroundColor Red
    Stop-Job $job
    $exitCode = 124
}

Remove-Job $job -Force

# Display results summary
Write-Host ""
Write-Host "Test Execution Summary" -ForegroundColor Cyan
Write-Host "=====================" -ForegroundColor Cyan

if ($exitCode -eq 0) {
    Write-Host "✅ Tests completed successfully" -ForegroundColor Green
} elseif ($exitCode -eq 124) {
    Write-Host "⏰ Tests timed out" -ForegroundColor Red
} else {
    Write-Host "❌ Tests failed" -ForegroundColor Red
}

Write-Host ""
Write-Host "Available test categories:" -ForegroundColor Yellow
Write-Host "  Category: Unit, Integration, EndToEnd, Performance, Smoke" -ForegroundColor Gray
Write-Host "  Speed: Fast, Medium, Slow" -ForegroundColor Gray
Write-Host "  Reliability: Stable, Unstable, Experimental" -ForegroundColor Gray
Write-Host ""
Write-Host "Example usage:" -ForegroundColor Yellow
Write-Host "  .\run-tests.ps1 -Category Unit -OnlyFast" -ForegroundColor Gray
Write-Host "  .\run-tests.ps1 -ExcludeUnstable -ExcludeSlow" -ForegroundColor Gray
Write-Host "  .\run-tests.ps1 -Category Integration -TimeoutMinutes 10" -ForegroundColor Gray

exit $exitCode