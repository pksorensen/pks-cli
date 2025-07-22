# Troubleshooting

This guide helps you resolve common issues when using PKS CLI.

## Installation Issues

### Command Not Found

**Problem:**
```bash
bash: pks: command not found
```

**Solutions:**

1. **Check if PKS CLI is installed:**
   ```bash
   dotnet tool list -g | grep pks-cli
   ```

2. **Install if missing:**
   ```bash
   dotnet tool install -g pks-cli
   ```

3. **Add tools directory to PATH:**
   ```bash
   # Linux/macOS
   export PATH="$PATH:$HOME/.dotnet/tools"
   
   # Windows PowerShell
   $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
   ```

### .NET SDK Issues

**Problem:**
```
The command could not be loaded, possibly because:
  * You intended to execute a .NET application:
      The application 'pks' does not exist.
```

**Solutions:**

1. **Install .NET 8.0 SDK:**
   ```bash
   # Check current version
   dotnet --version
   
   # Should show 8.0.x or later
   ```

2. **Download from Microsoft:**
   - Visit: https://dotnet.microsoft.com/download
   - Install .NET 8.0 SDK for your platform

## Project Initialization Issues

### Directory Already Exists

**Problem:**
```
❌ Error: Directory 'my-project' already exists
```

**Solutions:**

1. **Use force flag:**
   ```bash
   pks init my-project --force
   ```

2. **Choose different name:**
   ```bash
   pks init my-project-v2
   ```

3. **Remove existing directory:**
   ```bash
   rm -rf my-project
   pks init my-project
   ```

### Template Not Found

**Problem:**
```
❌ Error: Template 'custom' not found
```

**Solutions:**

1. **List available templates:**
   ```bash
   pks init --list-templates
   ```

2. **Use valid template:**
   ```bash
   pks init my-project --template api
   ```

Available templates: `console`, `api`, `web`, `agent`, `library`

## Agent Issues

### Agent Creation Failed

**Problem:**
```
❌ Error: Failed to create agent 'MyBot'
```

**Solutions:**

1. **Check agent name uniqueness:**
   ```bash
   pks agent list
   ```

2. **Use different name:**
   ```bash
   pks agent create --name MyBot2 --type developer
   ```

3. **Remove existing agent:**
   ```bash
   pks agent remove MyBot
   pks agent create --name MyBot --type developer
   ```

### Agent Won't Start

**Problem:**
```
❌ Error: Agent 'DevBot' failed to start
```

**Solutions:**

1. **Check agent status:**
   ```bash
   pks agent status DevBot
   ```

2. **View agent logs:**
   ```bash
   pks agent logs DevBot
   ```

3. **Restart agent:**
   ```bash
   pks agent stop DevBot
   pks agent start DevBot
   ```

## Deployment Issues

### Build Failures

**Problem:**
```
❌ Error: Build failed with 1 error(s)
```

**Solutions:**

1. **Check build output:**
   ```bash
   dotnet build --verbosity detailed
   ```

2. **Clean and rebuild:**
   ```bash
   dotnet clean
   dotnet restore
   dotnet build
   ```

3. **Check for missing dependencies:**
   ```bash
   dotnet list package --outdated
   dotnet restore --force
   ```

### Port Already in Use

**Problem:**
```
❌ Error: Port 5000 is already in use
```

**Solutions:**

1. **Use different port:**
   ```bash
   dotnet run --urls="http://localhost:5001"
   ```

2. **Kill existing process:**
   ```bash
   # Linux/macOS
   lsof -ti:5000 | xargs kill -9
   
   # Windows
   netstat -ano | findstr :5000
   taskkill /PID <PID> /F
   ```

3. **Configure in appsettings.json:**
   ```json
   {
     "Urls": "http://localhost:5001"
   }
   ```

## MCP Integration Issues

### MCP Server Won't Start

**Problem:**
```
❌ Error: Failed to start MCP server on port 3000
```

**Solutions:**

1. **Check if port is available:**
   ```bash
   netstat -an | grep 3000
   ```

2. **Use different port:**
   ```bash
   pks mcp start --port 4000
   ```

3. **Kill existing process:**
   ```bash
   # Find and kill process using port 3000
   lsof -ti:3000 | xargs kill -9
   ```

### AI Tools Can't Connect

**Problem:**
```
❌ Error: Connection refused to MCP server
```

**Solutions:**

1. **Verify server is running:**
   ```bash
   curl http://localhost:3000/health
   ```

2. **Check firewall settings:**
   ```bash
   # Allow port in firewall
   sudo ufw allow 3000
   ```

3. **Use localhost binding:**
   ```bash
   pks mcp start --host 0.0.0.0 --port 3000
   ```

## Performance Issues

### Slow Command Execution

**Problem:**
Commands take a long time to execute

**Solutions:**

1. **Disable animations:**
   ```bash
   export PKS_DISABLE_ANIMATIONS=true
   pks status
   ```

2. **Use quiet mode:**
   ```bash
   pks deploy --quiet
   ```

3. **Check system resources:**
   ```bash
   # Monitor CPU and memory usage
   top
   htop
   ```

### High Memory Usage

**Problem:**
PKS CLI consumes too much memory

**Solutions:**

1. **Restart agents:**
   ```bash
   pks agent restart --all
   ```

2. **Limit concurrent agents:**
   ```bash
   # In pks.config.json
   {
     "agents": {
       "maxConcurrentAgents": 2
     }
   }
   ```

3. **Clear cache:**
   ```bash
   dotnet nuget locals all --clear
   ```

## Configuration Issues

### Invalid Configuration

**Problem:**
```
❌ Error: Invalid configuration file
```

**Solutions:**

1. **Validate JSON syntax:**
   ```bash
   # Use online JSON validator or
   python -m json.tool pks.config.json
   ```

2. **Reset to defaults:**
   ```bash
   rm ~/.pks/config.json
   pks init --reset-config
   ```

3. **Use example configuration:**
   ```json
   {
     "agents": {
       "autoSpawn": true,
       "defaultType": "developer"
     },
     "ui": {
       "colorScheme": "cyan",
       "animations": true
     }
   }
   ```

## Environment-Specific Issues

### Windows Issues

**PowerShell Execution Policy:**
```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser
```

**Path Issues:**
```powershell
$env:PATH += ";$env:USERPROFILE\.dotnet\tools"
[Environment]::SetEnvironmentVariable("Path", $env:PATH, "User")
```

### macOS Issues

**Homebrew .NET Installation:**
```bash
brew update
brew install --cask dotnet
```

**Permission Denied:**
```bash
sudo chown -R $(whoami) ~/.dotnet
```

### Linux Issues

**Missing Dependencies:**
```bash
# Ubuntu/Debian
sudo apt update
sudo apt install -y dotnet-sdk-8.0

# CentOS/RHEL
sudo dnf install -y dotnet-sdk-8.0
```

**AppArmor/SELinux:**
```bash
# Check if blocking execution
sudo dmesg | grep -i denied
sudo ausearch -m avc -ts recent
```

## Getting More Help

If these solutions don't resolve your issue:

### 1. Enable Verbose Logging
```bash
pks --verbose [command]
export PKS_LOG_LEVEL=debug
```

### 2. Check System Information
```bash
# System info
uname -a
dotnet --info
pks --version

# Environment variables
env | grep PKS
```

### 3. Collect Diagnostics
```bash
# Create diagnostics package
pks diagnostics --output diagnostics.zip
```

### 4. Report the Issue

Create a detailed bug report at: https://github.com/pksorensen/pks-cli/issues/new

Include:
- **Environment**: OS, .NET version, PKS CLI version
- **Command**: Full command that failed
- **Error Message**: Complete error output
- **Steps to Reproduce**: Numbered steps
- **Expected vs Actual**: What should happen vs what happens
- **Logs**: Relevant log files or verbose output

### 5. Community Support

- **GitHub Discussions**: https://github.com/pksorensen/pks-cli/discussions
- **Stack Overflow**: Tag your questions with `pks-cli`

## Prevention Tips

### Keep PKS CLI Updated
```bash
dotnet tool update -g pks-cli
```

### Regular Maintenance
```bash
# Clear caches
dotnet nuget locals all --clear

# Update global tools
dotnet tool update -g pks-cli

# Check for issues
pks diagnostics --health-check
```

### Configuration Backup
```bash
# Backup your configuration
cp ~/.pks/config.json ~/.pks/config.json.backup
```

Remember: Most issues are resolved quickly with a clean installation or configuration reset. Don't hesitate to ask for help in the community! 🚀