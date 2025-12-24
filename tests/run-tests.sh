#!/bin/bash

# Test runner script for PKS CLI with categorization and timeout handling

# Default values
CATEGORY="All"
SPEED="All"
RELIABILITY="All"
EXCLUDE_UNSTABLE=false
EXCLUDE_SLOW=false
ONLY_FAST=false
TIMEOUT_MINUTES=5
VERBOSE=false
DEBUG=false

# Help function
show_help() {
    echo "PKS CLI Test Runner"
    echo "=================="
    echo ""
    echo "Usage: $0 [OPTIONS]"
    echo ""
    echo "Options:"
    echo "  --category CATEGORY       Filter by test category (Unit, Integration, EndToEnd, Performance, Smoke)"
    echo "  --speed SPEED            Filter by test speed (Fast, Medium, Slow)"
    echo "  --reliability RELIABILITY Filter by test reliability (Stable, Unstable, Experimental)"
    echo "  --exclude-unstable       Exclude unstable tests"
    echo "  --exclude-slow           Exclude slow tests"
    echo "  --only-fast              Run only fast tests"
    echo "  --timeout-minutes MINS   Set timeout in minutes (default: 5)"
    echo "  --verbose                Enable verbose output"
    echo "  --debug                  Enable debug output"
    echo "  --help                   Show this help message"
    echo ""
    echo "Examples:"
    echo "  $0 --category Unit --only-fast"
    echo "  $0 --exclude-unstable --exclude-slow"
    echo "  $0 --category Integration --timeout-minutes 10"
}

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --category)
            CATEGORY="$2"
            shift 2
            ;;
        --speed)
            SPEED="$2"
            shift 2
            ;;
        --reliability)
            RELIABILITY="$2"
            shift 2
            ;;
        --exclude-unstable)
            EXCLUDE_UNSTABLE=true
            shift
            ;;
        --exclude-slow)
            EXCLUDE_SLOW=true
            shift
            ;;
        --only-fast)
            ONLY_FAST=true
            shift
            ;;
        --timeout-minutes)
            TIMEOUT_MINUTES="$2"
            shift 2
            ;;
        --verbose)
            VERBOSE=true
            shift
            ;;
        --debug)
            DEBUG=true
            shift
            ;;
        --help)
            show_help
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            show_help
            exit 1
            ;;
    esac
done

echo -e "\033[36mPKS CLI Test Runner\033[0m"
echo -e "\033[36m==================\033[0m"

# Build filter expression
FILTER_PARTS=()

# Category filter
if [ "$CATEGORY" != "All" ]; then
    FILTER_PARTS+=("Category=$CATEGORY")
fi

# Speed filter
if [ "$SPEED" != "All" ]; then
    FILTER_PARTS+=("Speed=$SPEED")
elif [ "$ONLY_FAST" = true ]; then
    FILTER_PARTS+=("Speed=Fast")
elif [ "$EXCLUDE_SLOW" = true ]; then
    FILTER_PARTS+=("Speed!=Slow")
fi

# Reliability filter  
if [ "$RELIABILITY" != "All" ]; then
    FILTER_PARTS+=("Reliability=$RELIABILITY")
elif [ "$EXCLUDE_UNSTABLE" = true ]; then
    FILTER_PARTS+=("Reliability!=Unstable")
fi

# Build dotnet test command
TEST_ARGS=(
    "test"
    "--settings" ".runsettings"
    "--logger" "console;verbosity=normal"
    "--logger" "trx"
    "--logger" "html"
    "--results-directory" "../../test-artifacts/results"
    "--collect" "XPlat Code Coverage"
)

# Add filter if we have any parts
if [ ${#FILTER_PARTS[@]} -gt 0 ]; then
    FILTER_EXPRESSION=$(IFS='&'; echo "${FILTER_PARTS[*]}")
    TEST_ARGS+=("--filter" "$FILTER_EXPRESSION")
    echo -e "\033[33mRunning tests with filter: $FILTER_EXPRESSION\033[0m"
else
    echo -e "\033[32mRunning all tests\033[0m"
fi

if [ "$VERBOSE" = true ]; then
    TEST_ARGS+=("--verbosity" "detailed")
fi

if [ "$DEBUG" = true ]; then
    echo -e "\033[35mTest command: dotnet ${TEST_ARGS[*]}\033[0m"
fi

echo -e "\033[32mStarting test execution with $TIMEOUT_MINUTES minute timeout...\033[0m"

# Run tests with timeout
timeout "${TIMEOUT_MINUTES}m" dotnet "${TEST_ARGS[@]}"
EXIT_CODE=$?

echo ""
echo -e "\033[36mTest Execution Summary\033[0m"
echo -e "\033[36m=====================\033[0m"

case $EXIT_CODE in
    0)
        echo -e "\033[32m✅ Tests completed successfully\033[0m"
        ;;
    124)
        echo -e "\033[31m⏰ Tests timed out\033[0m"
        ;;
    *)
        echo -e "\033[31m❌ Tests failed\033[0m"
        ;;
esac

echo ""
echo -e "\033[33mAvailable test categories:\033[0m"
echo -e "\033[37m  Category: Unit, Integration, EndToEnd, Performance, Smoke\033[0m"
echo -e "\033[37m  Speed: Fast, Medium, Slow\033[0m"
echo -e "\033[37m  Reliability: Stable, Unstable, Experimental\033[0m"
echo ""
echo -e "\033[33mExample usage:\033[0m"
echo -e "\033[37m  $0 --category Unit --only-fast\033[0m"
echo -e "\033[37m  $0 --exclude-unstable --exclude-slow\033[0m"
echo -e "\033[37m  $0 --category Integration --timeout-minutes 10\033[0m"

exit $EXIT_CODE