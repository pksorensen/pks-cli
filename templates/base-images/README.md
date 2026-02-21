# PKS CLI Base Images

This directory contains Dockerfiles that are built into base images and published to GitHub Container Registry (GHCR). These base images significantly reduce build times for devcontainer templates by pre-installing common development tools and dependencies.

## Purpose

Base images serve several critical functions:

1. **Reduce Build Time**: Pre-install heavy dependencies (Node.js, .NET SDKs, Playwright browsers, etc.) once instead of on every devcontainer build
2. **Improve Consistency**: Ensure all developers use the same base environment with identical tool versions
3. **Optimize CI/CD**: Speed up automated builds and deployments by leveraging cached base layers
4. **Simplify Maintenance**: Update common tools in one place rather than across multiple templates

## Available Base Images

### pks-fullstack-base
**Registry**: `ghcr.io/pksorensen/pks-fullstack-base:latest`

A comprehensive base image for fullstack development including:
- **Node.js 20**: JavaScript/TypeScript development
- **.NET 8 + .NET 10 SDKs**: Modern .NET development with latest and LTS versions
- **Playwright**: Browser automation and testing with Chromium pre-installed
- **DevTunnel CLI**: Secure tunneling for local development
- **Git Credential Manager**: Azure DevOps authentication with device code flow
- **.NET Aspire CLI**: Cloud-native application development
- **Claude Code**: AI-powered development assistant
- **Development Tools**: git-delta, fzf, zsh with powerline10k, FFmpeg, and more

**Build Args**:
- `TZ`: Timezone (default: UTC)
- `CLAUDE_CODE_VERSION`: Version of Claude Code to install (default: latest)
- `GIT_DELTA_VERSION`: Version of git-delta (default: 0.18.2)
- `ZSH_IN_DOCKER_VERSION`: Version of zsh-in-docker (default: 1.2.0)
- `GCM_VERSION`: Git Credential Manager version (default: 2.6.0)

## How Base Images Work

### 1. GitHub Actions Build Pipeline

Base images are automatically built and published via GitHub Actions when changes are detected:

```yaml
# .github/workflows/build-base-images.yml
name: Build Base Images
on:
  push:
    paths:
      - 'templates/base-images/**'
  workflow_dispatch:

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Login to GHCR
        uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Build and Push
        # Build each image from base-images.json
```

### 2. Template Reference

Devcontainer templates reference the base image instead of building from scratch:

**Before** (full build every time):
```dockerfile
FROM node:20
# Install everything: 5-10 minutes
RUN apt update && apt install -y ...
RUN npm install -g ...
# etc.
```

**After** (using base image):
```dockerfile
FROM ghcr.io/pksorensen/pks-fullstack-base:latest
# Only install project-specific tools: 30 seconds
COPY init-firewall.sh /usr/local/bin/
# Configure project-specific settings
```

### 3. Build Time Comparison

| Scenario | Without Base Image | With Base Image | Time Saved |
|----------|-------------------|-----------------|------------|
| First Build | 8-12 minutes | 1-2 minutes | 75-85% |
| Rebuild (no cache) | 8-12 minutes | 1-2 minutes | 75-85% |
| Rebuild (cached) | 30-60 seconds | 15-30 seconds | 50% |

## Adding New Base Images

To create a new base image:

### 1. Create Dockerfile

Create a new directory under `templates/base-images/`:

```bash
mkdir templates/base-images/my-new-base
```

Create your Dockerfile:

```dockerfile
# templates/base-images/my-new-base/Dockerfile
FROM ubuntu:22.04

# Install common tools
RUN apt update && apt install -y \
    git \
    curl \
    build-essential

# Install language-specific tools
# ...

# Set up user environment
USER node
```

### 2. Register in base-images.json

Add your image to the configuration:

```json
{
  "images": [
    {
      "name": "my-new-base",
      "displayName": "My New Base Image",
      "description": "Description of what this base image provides",
      "dockerfile": "my-new-base/Dockerfile",
      "registry": "ghcr.io/pksorensen",
      "tags": ["latest", "1.0.0"],
      "platforms": ["linux/amd64"],
      "buildArgs": {
        "SOME_VERSION": "1.0.0"
      }
    }
  ]
}
```

### 3. Test Locally

Build and test your image locally before pushing:

```bash
cd templates/base-images/my-new-base
docker build -t my-new-base:test .
docker run -it my-new-base:test /bin/bash
```

### 4. Push Changes

Commit and push your changes. GitHub Actions will automatically build and publish the image:

```bash
git add templates/base-images/
git commit -m "feat: add my-new-base image"
git push
```

### 5. Monitor Build

Check the GitHub Actions workflow to ensure successful build:
- Navigate to: https://github.com/pksorensen/pks-cli/actions
- Find the "Build Base Images" workflow
- Verify successful completion

### 6. Update Templates

Update devcontainer templates to reference your new base image:

```dockerfile
# .devcontainer/Dockerfile
FROM ghcr.io/pksorensen/my-new-base:latest

# Add template-specific configurations
```

## Best Practices

### Version Management

1. **Always tag with semantic versions**: Use `latest` for development, specific versions for production
2. **Update base-images.json when versions change**: Keep configuration in sync with Dockerfiles
3. **Test thoroughly before promoting**: Validate new versions before updating `latest` tag

### Layer Optimization

1. **Group related RUN commands**: Combine `apt update && apt install` to reduce layers
2. **Clean up in same layer**: Remove temporary files in the same RUN command that created them
3. **Order by change frequency**: Place less frequently changing commands first

### Security

1. **Use specific version tags**: Avoid `FROM node:latest` in base images
2. **Keep images updated**: Regularly rebuild to include security patches
3. **Minimize attack surface**: Only install necessary tools
4. **Run as non-root user**: End Dockerfile with `USER node` or similar

### Documentation

1. **Document all build args**: Explain purpose and default values
2. **List installed tools**: Maintain accurate inventory in README
3. **Explain design decisions**: Document why certain tools are included

## Troubleshooting

### Image Not Found

If templates fail to pull base image:

```bash
# Verify image exists
docker pull ghcr.io/pksorensen/pks-fullstack-base:latest

# Check GitHub Actions build logs
# Navigate to: https://github.com/pksorensen/pks-cli/actions
```

### Build Failures

If GitHub Actions build fails:

1. **Check Dockerfile syntax**: Validate locally first
2. **Verify build args**: Ensure all required args have defaults
3. **Review platform support**: Confirm target platforms are supported
4. **Check rate limits**: GitHub API and Docker Hub may have rate limits

### Outdated Images

If templates use outdated base images:

1. **Force rebuild**: Manually trigger GitHub Actions workflow
2. **Update local cache**: Run `docker pull ghcr.io/pksorensen/pks-fullstack-base:latest`
3. **Check version tags**: Verify correct tag is specified in template

## Maintenance Schedule

Base images should be updated regularly:

- **Security patches**: As soon as vulnerabilities are announced
- **Tool updates**: Monthly for non-breaking updates
- **Major versions**: Quarterly with proper testing and migration guides

## Related Documentation

- [GitHub Container Registry Documentation](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry)
- [Docker Multi-stage Builds](https://docs.docker.com/build/building/multi-stage/)
- [Devcontainer Reference](https://containers.dev/implementors/json_reference/)

## Support

For issues with base images:
1. Check GitHub Actions build logs
2. Review this README
3. Open an issue at: https://github.com/pksorensen/pks-cli/issues
