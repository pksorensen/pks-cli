#!/bin/bash

# Performance-optimized test execution script
# Runs only fast and stable tests with optimized settings

set -e

echo "🚀 Running Fast Tests with Performance Optimizations..."

# Check if .NET is available
if ! command -v dotnet &> /dev/null; then
    echo "❌ .NET SDK not found. Please install .NET 8 or later."
    exit 1
fi

# Clean previous test artifacts
echo "🧹 Cleaning previous test artifacts..."
rm -rf test-artifacts/results/* 2>/dev/null || true

# Run fast tests only
echo "⚡ Running fast tests with parallel execution..."
dotnet test \
    --configuration Release \
    --verbosity minimal \
    --logger "console;verbosity=normal" \
    --filter "Speed=Fast|Category=Unit" \
    --collect:"XPlat Code Coverage" \
    --settings tests/.runsettings \
    --results-directory test-artifacts/results \
    --no-build \
    --no-restore \
    -- TestRunParameters.Parameter1=FastRun

echo "✅ Fast tests completed!"