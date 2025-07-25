#!/bin/bash

# Integration Testing Baseline Script
# Tests core functionality to establish production readiness baseline

set -e

echo "🔄 PKS CLI Integration Testing Baseline"
echo "======================================"

# Test Categories for Baseline Assessment
CRITICAL_TESTS=(
    "PKS.CLI.Tests.Infrastructure.*"
    "PKS.CLI.Tests.Commands.InitCommand*"
)

IMPORTANT_TESTS=(
    "PKS.CLI.Tests.Services.*Service*"
    "PKS.CLI.Tests.Commands.Agent*"
)

INTEGRATION_TESTS=(
    "PKS.CLI.Tests.Integration.Devcontainer.*EndToEnd*"
    "PKS.CLI.Tests.Integration.Mcp.*Connection*"
)

# Colors for output
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

run_test_category() {
    local category="$1"
    local description="$2"
    
    echo -e "\n${YELLOW}Testing: $description${NC}"
    echo "Filter: $category"
    
    # Run tests with timeout and capture results
    timeout 60s dotnet test --filter "$category" --verbosity minimal --logger "console;verbosity=minimal" > test_output.log 2>&1 || true
    
    # Parse results from output
    TOTAL=$(grep -oP 'Total tests: \K\d+' test_output.log 2>/dev/null || echo "0")
    PASSED=$(grep -oP 'Passed: \K\d+' test_output.log 2>/dev/null || echo "0")
    FAILED=$(grep -oP 'Failed: \K\d+' test_output.log 2>/dev/null || echo "0")
    SKIPPED=$(grep -oP 'Skipped: \K\d+' test_output.log 2>/dev/null || echo "0")
    
    if [ "$TOTAL" -gt 0 ]; then
        PASS_RATE=$(( PASSED * 100 / TOTAL ))
        echo -e "  Total: $TOTAL, Passed: ${GREEN}$PASSED${NC}, Failed: ${RED}$FAILED${NC}, Skipped: $SKIPPED"
        echo -e "  Pass Rate: ${GREEN}$PASS_RATE%${NC}"
        
        # Show first few failures for context
        if [ "$FAILED" -gt 0 ]; then
            echo -e "\n  ${RED}Sample Failures:${NC}"
            grep "FAIL\]" test_output.log | head -3 | sed 's/^/    /'
        fi
    else
        echo -e "  ${YELLOW}No tests found or tests timed out${NC}"
    fi
    
    rm -f test_output.log
}

# Build first
echo "🔨 Building solution..."
dotnet build --verbosity minimal

if [ $? -ne 0 ]; then
    echo -e "${RED}❌ Build failed. Cannot proceed with testing.${NC}"
    exit 1
fi

echo -e "${GREEN}✅ Build successful${NC}"

# Run test categories
echo -e "\n🧪 Running Baseline Tests"
echo "========================="

run_test_category "FullyQualifiedName~PKS.CLI.Tests.Infrastructure" "Infrastructure & Test Base Classes"
run_test_category "FullyQualifiedName~PKS.CLI.Tests.Commands.InitCommand" "Init Command (Core Functionality)"
run_test_category "FullyQualifiedName~Service&Category!=Integration" "Service Layer (Unit Tests)"

# Summary
echo -e "\n📊 Integration Baseline Summary"
echo "==============================="
echo "The baseline test run is complete."
echo ""
echo "Quality Gates for Production:"
echo "- ✅ Build successful (achieved)"
echo "- 🎯 80%+ pass rate on critical tests (check results above)"
echo "- 🎯 No hanging tests (60s timeout enforced)"
echo "- 🎯 Clean dependency injection (infrastructure tests)"
echo ""
echo -e "${YELLOW}Next Steps:${NC}"
echo "1. Review failing tests from each category"
echo "2. Fix critical issues in order of priority"
echo "3. Re-run full test suite when baseline is stable"
echo ""
echo -e "${GREEN}Integration baseline assessment complete!${NC}"