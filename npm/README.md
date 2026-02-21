# PKS CLI npm Package Distribution

This directory contains the npm package structure for distributing PKS CLI via npm.

## Structure

```
npm/
├── pks-cli/                 # Main wrapper package (@pks-cli/pks)
│   ├── bin/pks.js          # Platform detection & binary launcher
│   ├── postinstall.js      # Installation verification
│   ├── package.json        # Main package with optionalDependencies
│   ├── README.md           # User-facing documentation
│   └── LICENSE             # MIT License
│
└── pks-cli-{platform}/      # Platform-specific packages (6 total)
    ├── package.json        # Platform constraints (os/cpu)
    └── README.md           # Platform-specific docs
```

## Platform Packages

- `@pks-cli/pks-linux-x64` - Linux x64
- `@pks-cli/pks-linux-arm64` - Linux ARM64
- `@pks-cli/pks-osx-x64` - macOS Intel
- `@pks-cli/pks-osx-arm64` - macOS Apple Silicon
- `@pks-cli/pks-win-x64` - Windows x64
- `@pks-cli/pks-win-arm64` - Windows ARM64

## Building & Publishing

### 1. Build Platform Binaries

First, build all platform-specific binaries:

```bash
# From repository root
dotnet publish src/pks-cli.csproj -c Release -r linux-x64 --self-contained
dotnet publish src/pks-cli.csproj -c Release -r linux-arm64 --self-contained
dotnet publish src/pks-cli.csproj -c Release -r osx-x64 --self-contained
dotnet publish src/pks-cli.csproj -c Release -r osx-arm64 --self-contained
dotnet publish src/pks-cli.csproj -c Release -r win-x64 --self-contained
dotnet publish src/pks-cli.csproj -c Release -r win-arm64 --self-contained
```

### 2. Create npm Packages

Run the package creation script:

```bash
node scripts/create-npm-packages.js
```

This will:
- Copy binaries from build output to platform packages
- Update all package versions
- Run `npm pack` for each package
- Move tarballs to `dist/` directory

### 3. Publish to npm

Publish all packages to npm registry:

```bash
cd dist

# Publish platform packages first
npm publish pks-cli-linux-x64-*.tgz --access public
npm publish pks-cli-linux-arm64-*.tgz --access public
npm publish pks-cli-osx-x64-*.tgz --access public
npm publish pks-cli-osx-arm64-*.tgz --access public
npm publish pks-cli-win-x64-*.tgz --access public
npm publish pks-cli-win-arm64-*.tgz --access public

# Publish main package last
npm publish pks-*.tgz --access public
```

## Version Management

### Sync Versions Across All Packages

```bash
# Sync from .csproj version
./scripts/sync-npm-versions.sh

# Or specify version manually
./scripts/sync-npm-versions.sh 1.2.3
```

This updates:
- All platform package.json files
- Main package.json version
- optionalDependencies versions in main package

## Testing Locally

### Test Package Creation

```bash
# Build one platform for testing
dotnet publish src/pks-cli.csproj -c Release -r linux-x64 --self-contained

# Run package creation
node scripts/create-npm-packages.js

# Check output
ls -lh dist/
```

### Test Installation

```bash
cd dist

# Install main package globally
npm install -g ./pks-*.tgz

# Test the command
pks --help
```

## How It Works

### Installation Flow

1. **User installs**: `npm install -g @pks-cli/pks`

2. **npm downloads**:
   - Main package `@pks-cli/pks`
   - Platform-specific package (e.g., `@pks-cli/pks-linux-x64`)

3. **Postinstall runs**:
   - Verifies platform package is installed
   - Shows helpful messages if something is wrong

4. **User runs**: `pks [command]`

5. **Wrapper executes**:
   - `bin/pks.js` detects platform/architecture
   - Locates platform-specific binary
   - Spawns binary with all arguments
   - Forwards exit codes and signals

### Platform Detection

The `bin/pks.js` wrapper:
- Uses Node.js `process.platform` and `process.arch`
- Maps to correct platform package name
- Searches for binary in node_modules
- Provides helpful errors if binary not found

### Optional Dependencies

Platform packages are declared as `optionalDependencies`:
- npm only downloads package for current platform
- Installation succeeds even if some platforms fail
- Reduces download size for users

## Troubleshooting

### Binary Not Found

If users see "PKS CLI binary not found":

```bash
# Force reinstall
npm install -g @pks-cli/pks --force

# Or install platform package manually
npm install -g @pks-cli/pks-linux-x64
```

### Version Mismatch

If platform package version doesn't match main package:

```bash
# Sync all versions
./scripts/sync-npm-versions.sh

# Recreate packages
node scripts/create-npm-packages.js
```

### Missing Binaries

If `create-npm-packages.js` fails with "Binary not found":

```bash
# Ensure you've built for that platform
dotnet publish src/pks-cli.csproj -c Release -r linux-x64 --self-contained

# Check build output exists
ls src/bin/Release/net10.0/linux-x64/publish/
```

## CI/CD Integration

### GitHub Actions Workflow Example

```yaml
name: Publish npm Packages

on:
  release:
    types: [published]

jobs:
  build-and-publish:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3

      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '10.0.x'

      - name: Setup Node.js
        uses: actions/setup-node@v3
        with:
          node-version: '18'
          registry-url: 'https://registry.npmjs.org'

      - name: Build all platforms
        run: |
          dotnet publish src/pks-cli.csproj -c Release -r linux-x64 --self-contained
          dotnet publish src/pks-cli.csproj -c Release -r linux-arm64 --self-contained
          dotnet publish src/pks-cli.csproj -c Release -r osx-x64 --self-contained
          dotnet publish src/pks-cli.csproj -c Release -r osx-arm64 --self-contained
          dotnet publish src/pks-cli.csproj -c Release -r win-x64 --self-contained
          dotnet publish src/pks-cli.csproj -c Release -r win-arm64 --self-contained

      - name: Create npm packages
        run: node scripts/create-npm-packages.js

      - name: Publish to npm
        run: |
          cd dist
          npm publish *.tgz --access public
        env:
          NODE_AUTH_TOKEN: ${{ secrets.NPM_TOKEN }}
```

## References

- [npm optionalDependencies](https://docs.npmjs.com/cli/v10/configuring-npm/package-json#optionaldependencies)
- [npm os/cpu constraints](https://docs.npmjs.com/cli/v10/configuring-npm/package-json#os)
- [esbuild's platform packages](https://github.com/evanw/esbuild/tree/main/npm) (inspiration)

## License

MIT License - see LICENSE file for details.
