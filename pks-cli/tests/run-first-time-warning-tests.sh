#!/bin/bash

# Script to run first-time warning system tests
# Usage: ./run-first-time-warning-tests.sh [options]

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TEST_PROJECT="$SCRIPT_DIR/PKS.CLI.Tests.csproj"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Default options
VERBOSE=false
COVERAGE=false
FILTER=""
OUTPUT_DIR="$SCRIPT_DIR/test-results"

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        -v|--verbose)
            VERBOSE=true
            shift
            ;;
        -c|--coverage)
            COVERAGE=true
            shift
            ;;
        -f|--filter)
            FILTER="$2"
            shift 2
            ;;
        -o|--output)
            OUTPUT_DIR="$2"
            shift 2
            ;;
        -h|--help)
            echo "Usage: $0 [options]"
            echo "Options:"
            echo "  -v, --verbose     Enable verbose output"
            echo "  -c, --coverage    Generate code coverage report"
            echo "  -f, --filter      Filter tests (e.g., 'ConfigurationService')"
            echo "  -o, --output      Output directory for test results"
            echo "  -h, --help        Show this help message"
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            exit 1
            ;;
    esac
done

echo -e "${BLUE}🧪 Running First-Time Warning System Tests${NC}"
echo "=================================================="

# Create output directory
mkdir -p "$OUTPUT_DIR"

# Base test filter for first-time warning tests
BASE_FILTER="Category=Unit|Category=Integration"
if [[ -n "$FILTER" ]]; then
    BASE_FILTER="($BASE_FILTER)&FullyQualifiedName~$FILTER"
fi

# Add first-time warning specific filters
WARNING_FILTER="($BASE_FILTER)&(FullyQualifiedName~ConfigurationService|FullyQualifiedName~FirstTimeWarning|FullyQualifiedName~SkipFirstTimeWarning|FullyQualifiedName~AcceptanceCriteria)"

echo -e "${YELLOW}Filter: $WARNING_FILTER${NC}"
echo ""

# Build test arguments
TEST_ARGS=(
    "test"
    "$TEST_PROJECT"
    "--filter" "$WARNING_FILTER"
    "--logger" "trx;LogFileName=first-time-warning-tests.trx"
    "--logger" "console;verbosity=normal"
    "--results-directory" "$OUTPUT_DIR"
    "--no-build"
    "--configuration" "Release"
)

if [[ "$VERBOSE" == "true" ]]; then
    TEST_ARGS+=("--verbosity" "detailed")
fi

if [[ "$COVERAGE" == "true" ]]; then
    TEST_ARGS+=(
        "--collect" "XPlat Code Coverage"
        "--" "DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover"
    )
    echo -e "${YELLOW}Code coverage enabled${NC}"
fi

# Build the test project first
echo -e "${BLUE}Building test project...${NC}"
cd "$PROJECT_ROOT"
dotnet build "$TEST_PROJECT" --configuration Release --no-restore

# Run the tests
echo -e "${BLUE}Running first-time warning tests...${NC}"
if dotnet "${TEST_ARGS[@]}"; then
    echo -e "${GREEN}✅ All first-time warning tests passed!${NC}"
    TEST_SUCCESS=true
else
    echo -e "${RED}❌ Some tests failed!${NC}"
    TEST_SUCCESS=false
fi

# Generate coverage report if enabled
if [[ "$COVERAGE" == "true" ]]; then
    echo -e "${BLUE}Generating coverage report...${NC}"
    
    # Find the coverage file
    COVERAGE_FILE=$(find "$OUTPUT_DIR" -name "coverage.opencover.xml" | head -1)
    
    if [[ -n "$COVERAGE_FILE" ]]; then
        # Install reportgenerator if not already available
        if ! command -v reportgenerator &> /dev/null; then
            echo "Installing ReportGenerator..."
            dotnet tool install -g dotnet-reportgenerator-globaltool
        fi
        
        # Generate HTML report
        reportgenerator \
            "-reports:$COVERAGE_FILE" \
            "-targetdir:$OUTPUT_DIR/coverage-report" \
            "-reporttypes:Html;Badges" \
            "-title:PKS CLI First-Time Warning Tests"
        
        echo -e "${GREEN}Coverage report generated: $OUTPUT_DIR/coverage-report/index.html${NC}"
    else
        echo -e "${YELLOW}Warning: Coverage file not found${NC}"
    fi
fi

# Display test summary
echo ""
echo "=================================================="
echo -e "${BLUE}Test Summary${NC}"
echo "=================================================="

# Count test results from TRX file
TRX_FILE="$OUTPUT_DIR/first-time-warning-tests.trx"
if [[ -f "$TRX_FILE" ]]; then
    # Simple parsing of TRX file for summary
    TOTAL_TESTS=$(grep -o 'total="[0-9]*"' "$TRX_FILE" | grep -o '[0-9]*' || echo "0")
    PASSED_TESTS=$(grep -o 'passed="[0-9]*"' "$TRX_FILE" | grep -o '[0-9]*' || echo "0")
    FAILED_TESTS=$(grep -o 'failed="[0-9]*"' "$TRX_FILE" | grep -o '[0-9]*' || echo "0")
    
    echo "Total Tests: $TOTAL_TESTS"
    echo "Passed: $PASSED_TESTS"
    echo "Failed: $FAILED_TESTS"
else
    echo "Test results file not found"
fi

# Test categories covered
echo ""
echo "Test Categories Covered:"
echo "• Unit Tests - ConfigurationService"
echo "• Unit Tests - SkipFirstTimeWarningAttribute"
echo "• Unit Tests - FirstTimeWarningService"
echo "• Unit Tests - Error Handling"
echo "• Integration Tests - End-to-End Workflow"
echo "• Acceptance Criteria Tests - All 27 Requirements"

echo ""
echo "Output Directory: $OUTPUT_DIR"

if [[ "$TEST_SUCCESS" == "true" ]]; then
    echo -e "${GREEN}🎉 First-time warning system tests completed successfully!${NC}"
    exit 0
else
    echo -e "${RED}💥 Some tests failed. Check the output above for details.${NC}"
    exit 1
fi