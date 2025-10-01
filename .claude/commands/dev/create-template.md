---
description: Create a PKS CLI template package from a devcontainer folder
argument-hint: <template-folder-path>
allowed-tools: Read, Write, Edit, Bash, Glob
---

# PKS CLI Template Package Creator

I'll help you create a complete PKS CLI template package from your devcontainer files, similar to the claude-dotnet-9 template.

## Arguments
Template folder path: `$ARGUMENTS`

## Task

I will automate the entire template creation process:

### Phase 1: Validation & Discovery
1. Verify the provided folder path exists and contains devcontainer files
2. Extract template name from the folder path (e.g., "my-template" from "templates/my-template")
3. Check for required devcontainer files:
   - `devcontainer.json`
   - `Dockerfile`
   - Optional: `.env`, setup scripts, etc.

### Phase 2: Analyze Content & Identify Variables
1. Read all devcontainer-related files
2. Identify values that should be parameterized:
   - Project/container names
   - Timezone values
   - Memory limits
   - Feature flags
   - Environment variables
   - VS Code extensions
3. Suggest template parameters based on found values

### Phase 3: Create Template Structure
1. **Create `.template.config/template.json`** with:
   - Template identity and metadata
   - Extracted parameters (symbols)
   - Appropriate defaults
   - Post-actions for script permissions
   - Conditional file inclusion if needed

2. **Create `.csproj` file** with:
   - Package ID: `PKS.Templates.<TemplateName>`
   - Descriptive metadata
   - Content inclusion rules
   - Proper NuGet packaging configuration

3. **Create `README.md`** documenting:
   - Template features
   - Usage instructions
   - Parameter reference table
   - Configuration examples
   - Troubleshooting tips

4. **Copy template icon** from existing template or create placeholder

### Phase 4: Parameterize Files
1. Create `content/` directory structure
2. Copy devcontainer files to `content/.devcontainer/`
3. Replace hardcoded values with template placeholders:
   - `PROJECT_NAME` → actual project name parameter
   - `TIMEZONE_VALUE` → timezone parameter
   - `NODE_MEMORY_LIMIT` → memory limit parameter
   - `GITHUB_PAT_VALUE` → optional PAT parameter
   - Add any custom replacements identified

### Phase 5: Integration
1. Add template to the solution file (`pks-cli.sln`)
2. Build the template project
3. Create NuGet package
4. Verify package creation in artifacts folder

### Phase 6: Testing Instructions
Provide commands to test the template:
```bash
# Local development test
cd pks-cli/src
dotnet run -- init TestProject --devcontainer --template <template-name>

# After installation
pks init MyProject --devcontainer --template <template-name>
```

## Expected Output

A complete, distribution-ready template package with:
- ✅ Proper NuGet package configuration
- ✅ Template metadata and parameters
- ✅ Parameterized devcontainer files
- ✅ Comprehensive documentation
- ✅ Added to solution and buildable
- ✅ Testing instructions

## Process

I will:
1. Use TodoWrite to track progress through each phase
2. Show you identified parameters before creating template.json
3. Ask for confirmation on parameter defaults if unclear
4. Provide a summary of all created files
5. Test the build and package creation
6. Give you next steps for testing

Let me start by validating the folder path and discovering the devcontainer files...
