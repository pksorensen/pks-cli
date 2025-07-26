#!/bin/bash

# PKS CLI Report Command Test Runner
# This script provides convenient ways to run various test categories for the report command

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Test result tracking
TESTS_PASSED=0
TESTS_FAILED=0

# Function to print colored output
print_info() {
    echo -e "${BLUE}[INFO]${NC} $1"
}

print_success() {
    echo -e "${GREEN}[SUCCESS]${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}[WARNING]${NC} $1"
}

print_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Function to run tests with specific filter
run_tests() {
    local filter="$1"
    local description="$2"
    local exit_on_failure="${3:-true}"
    
    print_info "Running $description..."
    echo "Filter: $filter"
    echo "----------------------------------------"
    
    if dotnet test --filter "$filter" --logger "console;verbosity=normal" --results-directory "./test-results"; then
        print_success "$description completed successfully"
        ((TESTS_PASSED++))
        return 0
    else
        print_error "$description failed"
        ((TESTS_FAILED++))
        if [ "$exit_on_failure" = "true" ]; then
            exit 1
        fi
        return 1
    fi
}

# Function to check environment variables for integration tests
check_integration_env() {
    print_info "Checking integration test environment..."
    
    if [ -z "$GITHUB_TEST_TOKEN" ]; then
        print_warning "GITHUB_TEST_TOKEN not set - integration tests will be skipped"
        return 1
    fi
    
    if [ -z "$GITHUB_TEST_REPOSITORY" ]; then
        print_warning "GITHUB_TEST_REPOSITORY not set - using default repository"
        export GITHUB_TEST_REPOSITORY="https://github.com/pksorensen/pks-cli"
    fi
    
    print_info "Integration test environment configured:"
    print_info "  Repository: $GITHUB_TEST_REPOSITORY"
    print_info "  Token: ${GITHUB_TEST_TOKEN:0:8}... (truncated)"
    
    if [ "$GITHUB_ALLOW_ISSUE_CREATION" = "true" ]; then
        print_warning "Issue creation is ENABLED - this will create real GitHub issues!"
        read -p "Continue? (y/N): " -n 1 -r
        echo
        if [[ ! $REPLY =~ ^[Yy]$ ]]; then
            print_info "Issue creation disabled for this test run"
            unset GITHUB_ALLOW_ISSUE_CREATION
        fi
    fi
    
    return 0
}

# Function to generate test report
generate_report() {
    print_info "Generating test report..."
    
    local total_tests=$((TESTS_PASSED + TESTS_FAILED))
    local success_rate=0
    
    if [ $total_tests -gt 0 ]; then
        success_rate=$((TESTS_PASSED * 100 / total_tests))
    fi
    
    echo
    echo "========================================"
    echo "           TEST SUMMARY"
    echo "========================================"
    echo "Total Test Categories: $total_tests"
    echo "Passed: $TESTS_PASSED"
    echo "Failed: $TESTS_FAILED"
    echo "Success Rate: $success_rate%"
    echo "========================================"
    
    if [ $TESTS_FAILED -eq 0 ]; then
        print_success "All test categories passed!"
        return 0
    else
        print_error "$TESTS_FAILED test categories failed"
        return 1
    fi
}

# Parse command line arguments
case "${1:-all}" in
    "unit")
        print_info "Running unit tests for report command..."
        run_tests "Category=Unit&Command=Report" "Report Command Unit Tests"
        ;;
    
    "unit-all")
        print_info "Running all unit tests..."
        run_tests "Category=Unit" "All Unit Tests"
        ;;
        
    "auth")
        print_info "Running authentication tests..."
        run_tests "Command=Auth|TestType=Authentication" "Authentication Tests"
        ;;
        
    "integration")
        print_info "Running integration tests for report command..."
        if check_integration_env; then
            run_tests "Category=Integration&Component=GitHub" "GitHub Integration Tests"
        else
            print_warning "Skipping integration tests - environment not configured"
            exit 0
        fi
        ;;
        
    "error")
        print_info "Running error scenario tests..."
        run_tests "TestType=Error*" "Error Scenario Tests"
        ;;
        
    "critical")
        print_info "Running critical priority tests..."
        run_tests "Priority=Critical" "Critical Priority Tests"
        ;;
        
    "report")
        print_info "Running all report command tests..."
        run_tests "Command=Report" "All Report Command Tests" false
        run_tests "Command=Auth" "All Auth Command Tests" false
        generate_report
        exit $?
        ;;
        
    "ci")
        print_info "Running CI test suite..."
        
        # Run unit tests
        run_tests "Category=Unit" "Unit Tests" false
        
        # Run integration tests if configured
        if check_integration_env; then
            run_tests "Category=Integration" "Integration Tests" false
        else
            print_warning "Integration tests skipped in CI - no credentials provided"
        fi
        
        # Run critical tests
        run_tests "Priority=Critical" "Critical Tests" false
        
        generate_report
        exit $?
        ;;
        
    "all")
        print_info "Running complete test suite..."
        
        # Unit tests
        run_tests "Category=Unit&Command=Report" "Report Unit Tests" false
        run_tests "Category=Unit&Command=Auth" "Auth Unit Tests" false
        run_tests "TestType=Error*" "Error Scenario Tests" false
        
        # Integration tests (if configured)
        if check_integration_env; then
            run_tests "Category=Integration&Component=GitHub" "GitHub Integration Tests" false
            run_tests "Category=Integration&Component=Authentication" "Auth Integration Tests" false
        else
            print_warning "Integration tests skipped - environment not configured"
        fi
        
        generate_report
        exit $?
        ;;
        
    "help"|"-h"|"--help")
        echo "PKS CLI Report Command Test Runner"
        echo
        echo "Usage: $0 [test-category]"
        echo
        echo "Test Categories:"
        echo "  unit         - Report command unit tests only"
        echo "  unit-all     - All unit tests"
        echo "  auth         - Authentication-related tests"
        echo "  integration  - Integration tests with GitHub API"
        echo "  error        - Error scenario tests"
        echo "  critical     - Critical priority tests only"
        echo "  report       - All report command tests (unit + auth)"
        echo "  ci           - Full CI test suite"
        echo "  all          - Complete test suite (default)"
        echo "  help         - Show this help message"
        echo
        echo "Environment Variables:"
        echo "  GITHUB_TEST_TOKEN         - GitHub token for integration tests"
        echo "  GITHUB_TEST_REPOSITORY    - Test repository URL (optional)"
        echo "  GITHUB_ALLOW_ISSUE_CREATION - Set to 'true' to allow real issue creation"
        echo
        echo "Examples:"
        echo "  $0 unit                    # Run unit tests only"
        echo "  $0 integration             # Run integration tests"
        echo "  GITHUB_TEST_TOKEN=ghp_xxx $0 integration"
        echo "  $0 ci                      # Run CI test suite"
        exit 0
        ;;
        
    *)
        print_error "Unknown test category: $1"
        echo "Use '$0 help' to see available options"
        exit 1
        ;;
esac

# If we get here, a single test category was run
if [ $TESTS_FAILED -eq 0 ]; then
    print_success "Test category completed successfully!"
    exit 0
else
    print_error "Test category failed!"
    exit 1
fi