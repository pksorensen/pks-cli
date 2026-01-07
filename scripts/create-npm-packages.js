#!/usr/bin/env node

const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

/**
 * Platform configuration for PKS CLI npm packages
 */
const PLATFORMS = [
  {
    name: 'linux-x64',
    rid: 'linux-x64',
    packageName: '@pks-cli/pks-linux-x64',
    binaryName: 'pks',
    dir: 'pks-cli-linux-x64'
  },
  {
    name: 'linux-arm64',
    rid: 'linux-arm64',
    packageName: '@pks-cli/pks-linux-arm64',
    binaryName: 'pks',
    dir: 'pks-cli-linux-arm64'
  },
  {
    name: 'osx-x64',
    rid: 'osx-x64',
    packageName: '@pks-cli/pks-osx-x64',
    binaryName: 'pks',
    dir: 'pks-cli-osx-x64'
  },
  {
    name: 'osx-arm64',
    rid: 'osx-arm64',
    packageName: '@pks-cli/pks-osx-arm64',
    binaryName: 'pks',
    dir: 'pks-cli-osx-arm64'
  },
  {
    name: 'win-x64',
    rid: 'win-x64',
    packageName: '@pks-cli/pks-win-x64',
    binaryName: 'pks.exe',
    dir: 'pks-cli-win-x64'
  },
  {
    name: 'win-arm64',
    rid: 'win-arm64',
    packageName: '@pks-cli/pks-win-arm64',
    binaryName: 'pks.exe',
    dir: 'pks-cli-win-arm64'
  }
];

/**
 * Paths configuration
 */
const ROOT_DIR = path.resolve(__dirname, '..');
const NPM_DIR = path.join(ROOT_DIR, 'npm');
const SRC_DIR = path.join(ROOT_DIR, 'src');
const BUILD_OUTPUT_DIR = path.join(SRC_DIR, 'bin', 'Release', 'net10.0');
const DIST_DIR = path.join(ROOT_DIR, 'dist');

/**
 * Logging utilities
 */
const log = {
  info: (msg) => console.log(`ℹ️  ${msg}`),
  success: (msg) => console.log(`✅ ${msg}`),
  error: (msg) => console.error(`❌ ${msg}`),
  warn: (msg) => console.warn(`⚠️  ${msg}`),
  step: (msg) => console.log(`\n🔨 ${msg}`)
};

/**
 * Ensures a directory exists
 */
function ensureDir(dir) {
  if (!fs.existsSync(dir)) {
    fs.mkdirSync(dir, { recursive: true });
    log.info(`Created directory: ${dir}`);
  }
}

/**
 * Copies a file with error handling
 */
function copyFile(src, dest) {
  try {
    fs.copyFileSync(src, dest);
    // Set executable permissions for binaries (Unix-like systems)
    if (process.platform !== 'win32' && !dest.endsWith('.exe')) {
      fs.chmodSync(dest, 0o755);
    }
    return true;
  } catch (error) {
    log.error(`Failed to copy ${src} to ${dest}: ${error.message}`);
    return false;
  }
}

/**
 * Gets the version from the main package.json or .csproj
 */
function getVersion() {
  try {
    // Try to get version from .csproj
    const csprojPath = path.join(SRC_DIR, 'pks-cli.csproj');
    const csprojContent = fs.readFileSync(csprojPath, 'utf8');
    const versionMatch = csprojContent.match(/<Version>(.*?)<\/Version>/);
    if (versionMatch) {
      return versionMatch[1];
    }
  } catch (error) {
    log.warn('Could not read version from .csproj');
  }

  // Default version
  return '1.0.0';
}

/**
 * Updates package.json version
 */
function updatePackageVersion(packageJsonPath, version) {
  try {
    const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, 'utf8'));
    packageJson.version = version;

    // Update optionalDependencies versions if present
    if (packageJson.optionalDependencies) {
      Object.keys(packageJson.optionalDependencies).forEach(dep => {
        packageJson.optionalDependencies[dep] = version;
      });
    }

    fs.writeFileSync(packageJsonPath, JSON.stringify(packageJson, null, 2) + '\n');
    return true;
  } catch (error) {
    log.error(`Failed to update version in ${packageJsonPath}: ${error.message}`);
    return false;
  }
}

/**
 * Creates platform-specific package
 */
function createPlatformPackage(platform, version) {
  log.step(`Creating package for ${platform.name}`);

  const platformDir = path.join(NPM_DIR, platform.dir);
  const binarySource = path.join(BUILD_OUTPUT_DIR, platform.rid, 'publish', platform.binaryName);
  const binaryDest = path.join(platformDir, platform.binaryName);

  // Check if binary exists
  if (!fs.existsSync(binarySource)) {
    log.error(`Binary not found: ${binarySource}`);
    log.info('Make sure to build the project first with:');
    log.info(`  dotnet publish src/pks-cli.csproj -c Release -r ${platform.rid} --self-contained`);
    return false;
  }

  // Update package version
  const packageJsonPath = path.join(platformDir, 'package.json');
  if (!updatePackageVersion(packageJsonPath, version)) {
    return false;
  }

  // Copy binary
  if (!copyFile(binarySource, binaryDest)) {
    return false;
  }

  log.success(`Binary copied: ${platform.binaryName}`);

  // Create npm package
  try {
    log.info('Running npm pack...');
    const output = execSync('npm pack', {
      cwd: platformDir,
      encoding: 'utf8'
    });

    const tarballName = output.trim().split('\n').pop();
    const tarballPath = path.join(platformDir, tarballName);

    // Move tarball to dist directory
    ensureDir(DIST_DIR);
    const distTarball = path.join(DIST_DIR, tarballName);
    fs.renameSync(tarballPath, distTarball);

    log.success(`Package created: ${tarballName}`);
    return true;
  } catch (error) {
    log.error(`Failed to create package: ${error.message}`);
    return false;
  }
}

/**
 * Creates main wrapper package
 */
function createMainPackage(version) {
  log.step('Creating main wrapper package');

  const mainDir = path.join(NPM_DIR, 'pks-cli');
  const packageJsonPath = path.join(mainDir, 'package.json');

  // Update package version
  if (!updatePackageVersion(packageJsonPath, version)) {
    return false;
  }

  // Create npm package
  try {
    log.info('Running npm pack...');
    const output = execSync('npm pack', {
      cwd: mainDir,
      encoding: 'utf8'
    });

    const tarballName = output.trim().split('\n').pop();
    const tarballPath = path.join(mainDir, tarballName);

    // Move tarball to dist directory
    ensureDir(DIST_DIR);
    const distTarball = path.join(DIST_DIR, tarballName);
    fs.renameSync(tarballPath, distTarball);

    log.success(`Main package created: ${tarballName}`);
    return true;
  } catch (error) {
    log.error(`Failed to create main package: ${error.message}`);
    return false;
  }
}

/**
 * Main execution
 */
function main() {
  console.log('📦 PKS CLI npm Package Creator\n');

  // Get version
  const version = getVersion();
  log.info(`Version: ${version}`);

  // Ensure dist directory exists
  ensureDir(DIST_DIR);

  // Create platform packages
  let successCount = 0;
  let failureCount = 0;

  for (const platform of PLATFORMS) {
    if (createPlatformPackage(platform, version)) {
      successCount++;
    } else {
      failureCount++;
    }
  }

  // Create main package
  if (createMainPackage(version)) {
    successCount++;
  } else {
    failureCount++;
  }

  // Summary
  log.step('Summary');
  log.info(`Total packages: ${PLATFORMS.length + 1}`);
  log.success(`Successfully created: ${successCount}`);

  if (failureCount > 0) {
    log.error(`Failed: ${failureCount}`);
    process.exit(1);
  }

  log.success('\n🎉 All packages created successfully!');
  log.info(`\nPackages are available in: ${DIST_DIR}`);
  log.info('\nTo publish:');
  log.info('  cd dist');
  log.info('  npm publish pks-cli-<platform>-<version>.tgz --access public');
  log.info('  npm publish pks-<version>.tgz --access public');
}

// Run main function
if (require.main === module) {
  try {
    main();
  } catch (error) {
    log.error(`Unexpected error: ${error.message}`);
    console.error(error.stack);
    process.exit(1);
  }
}

module.exports = { createPlatformPackage, createMainPackage, getVersion };
