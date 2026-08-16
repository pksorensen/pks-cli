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
    'linux-x64': '@pks-cli/cli-linux-x64',
    'linux-arm64': '@pks-cli/cli-linux-arm64',
    'darwin-x64': '@pks-cli/cli-osx-x64',
    'darwin-arm64': '@pks-cli/cli-osx-arm64',
    'win32-x64': '@pks-cli/cli-win-x64',
    'win32-arm64': '@pks-cli/cli-win-arm64'
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

  // Package name without scope, e.g. 'cli-linux-x64'
  const packageDir = packageName.replace('@pks-cli/', '');

  // Try to find the platform package in node_modules
  // __dirname is node_modules/@pks-cli/cli/bin/
  // Binary may be at package root or in bin/ subdirectory
  const scopeDir = join(__dirname, '..', '..');           // node_modules/@pks-cli/
  const parentScopeDir = join(__dirname, '..', '..', '..', '@pks-cli'); // ../node_modules/@pks-cli/
  // Not hoisted: npm nests the optional dependency inside this package when the
  // install has nowhere to hoist it to -- which is exactly what `npm i -g` does,
  // so a global install found no binary at all until this root was added.
  const ownScopeDir = join(__dirname, '..', 'node_modules', '@pks-cli');

  const roots = [scopeDir, parentScopeDir, ownScopeDir];
  const paths = [];
  for (const root of roots) {
    paths.push(
      join(root, packageDir, 'bin', 'pks'),
      join(root, packageDir, 'bin', 'pks.exe'),
      join(root, packageDir, 'pks'),
      join(root, packageDir, 'pks.exe'),
    );
  }

  for (const path of paths) {
    if (existsSync(path)) {
      return path;
    }
  }

  // Last resort: let Node walk the node_modules chain itself, which covers layouts
  // (pnpm, yarn PnP-less workspaces) that no hardcoded root above anticipates.
  try {
    const manifest = require.resolve(`${packageName}/package.json`, { paths: [__dirname] });
    const packageRoot = join(manifest, '..');
    for (const candidate of [
      join(packageRoot, 'bin', 'pks'),
      join(packageRoot, 'bin', 'pks.exe'),
      join(packageRoot, 'pks'),
      join(packageRoot, 'pks.exe'),
    ]) {
      if (existsSync(candidate)) {
        return candidate;
      }
    }
  } catch {
    // Fall through to the error below -- resolution failing just means "not installed".
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
