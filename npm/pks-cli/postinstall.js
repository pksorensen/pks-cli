#!/usr/bin/env node

const { platform, arch } = process;
const { existsSync } = require('fs');
const { join } = require('path');

/**
 * Gets the expected platform package name
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
 * Checks if the platform-specific package is installed
 */
function checkPlatformPackage() {
  const packageName = getPlatformPackage();

  if (!packageName) {
    console.warn(`⚠️  Unsupported platform: ${platform}-${arch}`);
    console.warn('Supported platforms: linux-x64, linux-arm64, darwin-x64, darwin-arm64, win32-x64, win32-arm64');
    return false;
  }

  const packageDir = packageName.replace('@pks-cli/', '');
  const paths = [
    // Scoped: node_modules/@pks-cli/cli-linux-x64/
    join(__dirname, '..', packageDir),
    // Parent node_modules (hoisted)
    join(__dirname, '..', '..', '@pks-cli', packageDir),
  ];

  for (const path of paths) {
    if (existsSync(path)) {
      console.log(`✅ PKS CLI installed successfully for ${platform}-${arch}`);
      return true;
    }
  }

  return false;
}

/**
 * Provides installation instructions if platform package is missing
 */
function showInstallationHelp() {
  const packageName = getPlatformPackage();

  console.warn('⚠️  Platform-specific package not found');
  console.warn('');
  console.warn('This can happen if:');
  console.warn('1. Optional dependencies were skipped during installation');
  console.warn('2. The platform package failed to download');
  console.warn('3. Corporate proxy or firewall is blocking the download');
  console.warn('');
  console.warn('To fix this, try:');
  console.warn('');
  console.warn(`  npm install ${packageName}`);
  console.warn('');
  console.warn('Or reinstall with:');
  console.warn('');
  console.warn('  npm install @pks-cli/cli --force');
  console.warn('');

  // Non-zero exit in CI environments only
  if (process.env.CI === 'true') {
    console.error('❌ Installation failed in CI environment');
    process.exit(1);
  }
}

/**
 * Main postinstall logic
 */
function postinstall() {
  console.log('🚀 Setting up PKS CLI...');

  const isInstalled = checkPlatformPackage();

  if (!isInstalled) {
    showInstallationHelp();
  }
}

// Run postinstall
try {
  postinstall();
} catch (error) {
  console.error('Postinstall script failed:', error.message);
  // Don't fail the installation, just warn
  if (process.env.CI === 'true') {
    process.exit(1);
  }
}
