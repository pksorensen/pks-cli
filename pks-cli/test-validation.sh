#!/bin/bash

# Test script to validate the project name validation logic

cd /workspace/pks-cli/src

echo "Testing Project Name Validation..."
echo "=================================="

# Test 1: Reserved name
echo -n "Test 1 (Reserved name 'CON'): "
output=$(echo "TestProject" | timeout 2 DOTNET_ENVIRONMENT=Production dotnet run --no-launch-profile -- init "CON" --description "Test" 2>&1 | sed 's/\x1b\[[0-9;]*m//g')
if echo "$output" | grep -i "reserved" >/dev/null; then
    echo "PASS"
else
    echo "FAIL"
fi

# Test 2: Invalid characters
echo -n "Test 2 (Invalid char '/'): "
output=$(echo "TestProject" | timeout 2 DOTNET_ENVIRONMENT=Production dotnet run --no-launch-profile -- init "project/name" --description "Test" 2>&1 | sed 's/\x1b\[[0-9;]*m//g')
if echo "$output" | grep -i "invalid" >/dev/null; then
    echo "PASS"
else
    echo "FAIL"
fi

# Test 3: Empty name
echo -n "Test 3 (Empty name): "
output=$(echo -e "\nTestProject" | timeout 2 DOTNET_ENVIRONMENT=Production dotnet run --no-launch-profile -- init "" --description "Test" 2>&1 | sed 's/\x1b\[[0-9;]*m//g')
if echo "$output" | grep -i "empty" >/dev/null; then
    echo "PASS"
else
    echo "FAIL"
fi

# Test 4: Starts with dot
echo -n "Test 4 (Starts with dot): "
output=$(echo "TestProject" | timeout 2 DOTNET_ENVIRONMENT=Production dotnet run --no-launch-profile -- init ".project" --description "Test" 2>&1 | sed 's/\x1b\[[0-9;]*m//g')
if echo "$output" | grep -i "dot" >/dev/null; then
    echo "PASS"
else
    echo "FAIL"
fi

# Test 5: Valid name (this should timeout because it starts initialization)
echo -n "Test 5 (Valid name 'ValidProject'): "
if echo "TestProject" | timeout 2 dotnet run -- init "ValidProject" --description "Test" 2>&1 | grep -q "timeout"; then
    echo "PASS (validation passed, command started)"
else
    # Check if it actually started initialization (no validation errors)
    if echo "TestProject" | timeout 2 dotnet run -- init "ValidProject" --description "Test" 2>&1 | grep -qv "❌"; then
        echo "PASS (validation passed)"
    else
        echo "FAIL"
    fi
fi

echo ""
echo "Validation tests completed!"