#!/usr/bin/env node

const { spawn } = require('child_process');
const { existsSync } = require('fs');
const { join } = require('path');
const { platform, arch } = process;

/**
 * Maps Node.js platform/arch to PKS CLI platform package names
 */
function getPlatformPackage() {
  const platformMap = {
    'linux-x64': '@pks-cli/pks-linux-x64',
    'linux-arm64': '@pks-cli/pks-linux-arm64',
    'darwin-x64': '@pks-cli/pks-osx-x64',
    'darwin-arm64': '@pks-cli/pks-osx-arm64',
    'win32-x64': '@pks-cli/pks-win-x64',
    'win32-arm64': '@pks-cli/pks-win-arm64'
  };

  const key = `${platform}-${arch}`;
  return platformMap[key] || null;
}

/**
 * Finds the platform-specific binary path
 */
function getBinaryPath() {
  const packageName = getPlatformPackage();

  if (!packageName) {
    console.error(`Unsupported platform: ${platform}-${arch}`);
    console.error('Supported platforms: linux-x64, linux-arm64, darwin-x64, darwin-arm64, win32-x64, win32-arm64');
    process.exit(1);
  }

  // Try to find the platform package in node_modules
  const paths = [
    // Installed as dependency
    join(__dirname, '..', '..', packageName.replace('@pks-cli/', 'pks-cli-'), 'pks'),
    join(__dirname, '..', '..', packageName.replace('@pks-cli/', 'pks-cli-'), 'pks.exe'),
    // Installed in parent node_modules
    join(__dirname, '..', '..', '..', packageName.replace('@pks-cli/', 'pks-cli-'), 'pks'),
    join(__dirname, '..', '..', '..', packageName.replace('@pks-cli/', 'pks-cli-'), 'pks.exe'),
  ];

  for (const path of paths) {
    if (existsSync(path)) {
      return path;
    }
  }

  console.error(`PKS CLI binary not found for ${platform}-${arch}`);
  console.error('Please ensure the platform-specific package is installed.');
  console.error(`Expected package: ${packageName}`);
  console.error('Try running: npm install --force');
  process.exit(1);
}

/**
 * Executes the platform-specific PKS CLI binary
 */
function runPks() {
  const binaryPath = getBinaryPath();

  // Spawn the binary with all arguments passed through
  const child = spawn(binaryPath, process.argv.slice(2), {
    stdio: 'inherit',
    shell: false
  });

  child.on('exit', (code, signal) => {
    if (signal) {
      process.kill(process.pid, signal);
    } else {
      process.exit(code || 0);
    }
  });

  child.on('error', (err) => {
    console.error('Failed to execute PKS CLI:', err.message);
    process.exit(1);
  });

  // Handle process termination
  process.on('SIGINT', () => {
    child.kill('SIGINT');
  });

  process.on('SIGTERM', () => {
    child.kill('SIGTERM');
  });
}

// Run PKS CLI
runPks();
