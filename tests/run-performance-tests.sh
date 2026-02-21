#!/bin/bash

# Performance test execution script
# Runs tests with detailed timing analysis

set -e

echo "📊 Running Performance Analysis Tests..."

# Check if .NET is available
if ! command -v dotnet &> /dev/null; then
    echo "❌ .NET SDK not found. Please install .NET 8 or later."
    exit 1
fi

# Clean previous test artifacts
echo "🧹 Cleaning previous test artifacts..."
rm -rf test-artifacts/results/* 2>/dev/null || true

# Start time tracking
start_time=$(date +%s)

# Run all tests with performance monitoring
echo "⏱️  Running all tests with performance monitoring..."
dotnet test \
    --configuration Release \
    --verbosity normal \
    --logger "console;verbosity=detailed" \
    --logger "trx;LogFileName=performance-analysis.trx" \
    --collect:"XPlat Code Coverage" \
    --settings tests/.runsettings \
    --results-directory test-artifacts/results \
    --no-build \
    --no-restore \
    2>&1 | tee test-performance-log.txt

# End time tracking
end_time=$(date +%s)
duration=$((end_time - start_time))

echo "⏰ Total execution time: ${duration} seconds"

# Analyze results
if [ -f test-performance-log.txt ]; then
    echo "📈 Performance Analysis:"
    echo "  - Tests taking > 1 second:"
    grep -E "\[[0-9]{3,}ms\]|\[[0-9]+s\]" test-performance-log.txt | head -10 || echo "    No slow tests found"
    echo "  - Failed tests:"
    grep -c "FAIL" test-performance-log.txt || echo "    No failed tests"
    echo "  - Total duration: ${duration}s"
fi

echo "✅ Performance analysis completed!"