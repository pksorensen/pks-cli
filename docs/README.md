# PKS CLI Documentation

This directory contains the complete documentation for PKS CLI built using DocFX.

## Structure

```
docs/
├── docfx.json              # DocFX configuration
├── index.md                # Documentation homepage
├── toc.yml                 # Main table of contents
├── articles/               # Documentation articles
│   ├── getting-started.md  # Getting started guide
│   ├── installation.md     # Installation instructions
│   ├── contributing.md     # Contributing guidelines
│   ├── troubleshooting.md  # Troubleshooting guide
│   ├── commands/           # Command reference
│   │   ├── toc.yml
│   │   ├── overview.md
│   │   ├── init.md
│   │   ├── agent.md
│   │   └── ...
│   ├── tutorials/          # Step-by-step tutorials
│   │   ├── toc.yml
│   │   ├── first-project.md
│   │   └── ...
│   ├── architecture/       # Architecture documentation
│   │   ├── toc.yml
│   │   ├── overview.md
│   │   └── ...
│   └── advanced/           # Advanced topics
│       ├── toc.yml
│       ├── configuration.md
│       └── ...
└── _site/                  # Generated documentation site
```

## Building Documentation

### Prerequisites
- .NET 8.0 SDK
- DocFX (`dotnet tool install -g docfx`)

### Build Commands

```bash
# Build documentation
cd docs
docfx docfx.json

# Build and serve locally
docfx docfx.json --serve

# Serve on specific port
docfx serve _site --port 8080
```

### Development

When adding new documentation:

1. **Add new articles** to the appropriate `articles/` subdirectory
2. **Update table of contents** (`toc.yml`) files to include new pages  
3. **Test the build** with `docfx docfx.json`
4. **Verify links** work correctly
5. **Commit changes** to trigger automatic deployment

### Deployment

Documentation is automatically built and deployed via GitHub Actions:

- **Trigger**: Push to `main` branch with changes to `docs/` or source code
- **Build**: DocFX generates static site
- **Deploy**: Published to GitHub Pages
- **URL**: https://pksorensen.github.io/pks-cli/

### Writing Guidelines

#### Markdown Best Practices
- Use clear, descriptive headings
- Include code examples with proper syntax highlighting
- Add table of contents for long pages
- Use consistent formatting for commands and options

#### Documentation Structure
- **Getting Started** - For new users
- **Command Reference** - Complete command documentation
- **Tutorials** - Step-by-step guides
- **Architecture** - Technical deep dives
- **Advanced** - Complex topics and customization

#### Code Examples
```bash
# Always include working examples
pks init my-project --template api --agentic

# Show expected output when helpful
✨ Creating project 'my-project'...
🎉 Project initialized successfully!
```

#### Cross-References
- Link related topics using relative paths
- Use descriptive link text
- Validate all links during build

### Troubleshooting Build Issues

#### Common Problems

**Missing Files:**
```bash
# Error: Unable to find file "articles/missing.md"
# Solution: Create the file or remove from toc.yml
```

**Broken Links:**
```bash
# Error: Invalid file link: ~/articles/nonexistent.md
# Solution: Fix the link or create the target file
```

**API Documentation:**
```bash
# Warning: No .NET API project detected
# Solution: Ensure source code builds successfully
cd ../pks-cli/src && dotnet build
```

#### Validation

```bash
# Check for broken links
docfx docfx.json --dry-run

# Validate table of contents
docfx build docfx.json --log verbosity:detailed
```

### Contributing

When contributing to documentation:

1. **Follow the existing structure** and naming conventions
2. **Test locally** before submitting
3. **Include examples** and use cases
4. **Update navigation** (toc.yml files) appropriately
5. **Keep content current** with latest CLI features

### Automation

The documentation system includes:
- **Auto-deployment** via GitHub Actions
- **Link validation** during build
- **Search indexing** for site search
- **Sitemap generation** for SEO

### Future Enhancements

Planned improvements:
- API documentation from XML comments
- Interactive examples
- Video tutorials
- Localization support
- Community contributions

For questions about documentation, create an issue on the [main repository](https://github.com/pksorensen/pks-cli/issues).

Happy documenting! 📚