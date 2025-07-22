# Installation Guide

This guide covers all the ways to install PKS CLI on different platforms and environments.

## System Requirements

### Minimum Requirements

- **.NET 8.0 SDK** or later
- **Windows 10/11**, **macOS 10.14+**, or **Linux** (Ubuntu 18.04+, etc.)
- **4 GB RAM** (8 GB recommended)
- **500 MB** free disk space
- **Terminal** with Unicode support

### Recommended Setup

- **Visual Studio Code** with C# extension
- **Git** for version control
- **Docker** (optional, for deployment features)
- **PowerShell 7+** on all platforms

## Installation Methods

### 🚀 Method 1: .NET Global Tool (Recommended)

This is the fastest and easiest way to install PKS CLI:

```bash
# Install the latest version
dotnet tool install -g pks-cli

# Verify installation
pks --version

# Update to latest version
dotnet tool update -g pks-cli
```

**Advantages:**
- Automatic updates available
- Works on all platforms
- Integrates with .NET ecosystem
- Easy to uninstall

### 🔧 Method 2: From Source

For development or custom builds:

```bash
# Clone the repository
git clone https://github.com/pksorensen/pks-cli.git
cd pks-cli/pks-cli/src

# Build in Release mode
dotnet build --configuration Release

# Create NuGet package
dotnet pack --configuration Release

# Install locally
dotnet tool install -g --add-source ./bin/Release pks-cli --force

# Verify installation
pks --version
```

**Advantages:**
- Get the latest features
- Contribute to development
- Custom modifications possible

### 🐳 Method 3: Docker (Coming Soon)

Container-based installation:

```bash
# Pull the Docker image
docker pull pkscli/pks-cli:latest

# Run PKS CLI in container
docker run --rm -it -v $(pwd):/workspace pkscli/pks-cli:latest --help

# Create an alias for easier use
alias pks='docker run --rm -it -v $(pwd):/workspace pkscli/pks-cli:latest'
```

### 📦 Method 4: Package Managers

#### Windows (Chocolatey)
```powershell
# Install Chocolatey first if needed
# Coming soon
choco install pks-cli
```

#### macOS (Homebrew)
```bash
# Coming soon
brew install pks-cli
```

#### Linux (Snap)
```bash
# Coming soon  
sudo snap install pks-cli
```

## Platform-Specific Instructions

### Windows

#### Prerequisites
```powershell
# Check .NET version
dotnet --version

# Install .NET 8 if needed
# Download from: https://dotnet.microsoft.com/download
```

#### Installation
```powershell
# Install PKS CLI
dotnet tool install -g pks-cli

# Add to PATH if needed (usually automatic)
# $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
```

#### PowerShell Profile Setup
Add to your PowerShell profile for better experience:

```powershell
# Add to $PROFILE
function pks-init { pks init $args }
function pks-deploy { pks deploy $args }
Set-Alias pk pks
```

### macOS

#### Prerequisites
```bash
# Check .NET version
dotnet --version

# Install .NET 8 using Homebrew
brew install --cask dotnet

# Or download from Microsoft
# https://dotnet.microsoft.com/download
```

#### Installation
```bash
# Install PKS CLI
dotnet tool install -g pks-cli

# Ensure tools directory is in PATH
echo 'export PATH="$PATH:$HOME/.dotnet/tools"' >> ~/.zshrc
source ~/.zshrc
```

#### Shell Configuration
For Zsh (default on macOS):

```bash
# Add to ~/.zshrc
alias pk='pks'
export PKS_DEFAULT_TEMPLATE='web'
```

### Linux (Ubuntu/Debian)

#### Prerequisites
```bash
# Install .NET 8
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y dotnet-sdk-8.0
```

#### Installation
```bash
# Install PKS CLI
dotnet tool install -g pks-cli

# Add to PATH
echo 'export PATH="$PATH:$HOME/.dotnet/tools"' >> ~/.bashrc
source ~/.bashrc
```

### Linux (CentOS/RHEL/Fedora)

#### Prerequisites
```bash
# Install .NET 8
sudo dnf install dotnet-sdk-8.0

# Or for older versions
sudo yum install dotnet-sdk-8.0
```

## Verification

After installation, verify everything is working:

```bash
# Check version
pks --version

# View help
pks --help

# Test basic functionality
pks ascii "PKS CLI" --style banner

# Check all commands are available  
pks init --help
pks agent --help
pks deploy --help
pks status --help
pks mcp --help
```

Expected output:
```
PKS CLI version 1.0.0
🤖 Professional Agentic Simplifier
Built with ❤️ using .NET 8
```

## Troubleshooting

### Common Issues

#### 1. "pks command not found"
**Solution:**
```bash
# Check if tools directory is in PATH
echo $PATH | grep -q ".dotnet/tools" && echo "✓ PATH configured" || echo "✗ PATH missing"

# Add to PATH manually
export PATH="$PATH:$HOME/.dotnet/tools"

# Make permanent (add to shell profile)
echo 'export PATH="$PATH:$HOME/.dotnet/tools"' >> ~/.bashrc
```

#### 2. ".NET SDK not found"
**Solution:**
```bash
# Check .NET installation
dotnet --info

# Install .NET 8 SDK
# Windows: Download from Microsoft
# macOS: brew install --cask dotnet
# Linux: Follow platform-specific instructions above
```

#### 3. "Access denied" on Windows
**Solution:**
```powershell
# Run PowerShell as Administrator
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser

# Then retry installation
dotnet tool install -g pks-cli
```

#### 4. PKS CLI crashes on startup
**Solution:**
```bash
# Check for conflicting global tools
dotnet tool list -g

# Reinstall PKS CLI
dotnet tool uninstall -g pks-cli
dotnet tool install -g pks-cli

# Clear .NET cache
dotnet nuget locals all --clear
```

### Getting More Help

If you encounter issues:

1. **Check System Requirements** - Ensure .NET 8+ is installed
2. **Update .NET SDK** - `dotnet --version` should show 8.0+
3. **Reinstall PKS CLI** - Sometimes a clean reinstall fixes issues
4. **Check GitHub Issues** - [Search for similar problems](https://github.com/pksorensen/pks-cli/issues)
5. **Create New Issue** - [Report new bugs or problems](https://github.com/pksorensen/pks-cli/issues/new)

## Uninstallation

To remove PKS CLI:

```bash
# Uninstall global tool
dotnet tool uninstall -g pks-cli

# Remove configuration files (optional)
rm -rf ~/.pks

# Remove from PATH (if added manually)
# Edit your shell profile and remove PKS CLI path entries
```

## Next Steps

Now that PKS CLI is installed:

1. **[Get Started](getting-started.md)** - Create your first project
2. **[Command Reference](commands/overview.md)** - Learn all the commands  
3. **[Configuration](advanced/configuration.md)** - Customize your setup

Ready to build something awesome? Let's get started! 🚀