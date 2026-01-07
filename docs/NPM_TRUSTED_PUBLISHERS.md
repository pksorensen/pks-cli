# npm Trusted Publishers Setup

This guide explains how to set up **npm Trusted Publishers** (Provenance) for PKS CLI, which is the modern, secure way to publish npm packages from GitHub Actions without needing access tokens.

## What is Trusted Publishers?

Trusted Publishers uses **OIDC (OpenID Connect)** to allow GitHub Actions to authenticate directly with npm, proving that packages come from your specific repository. This is **much more secure** than using long-lived access tokens.

### Benefits

- ✅ **No long-lived tokens** - No NPM_TOKEN to manage, rotate, or secure
- ✅ **Better security** - Can't be leaked, stolen, or accidentally committed
- ✅ **Automatic provenance** - Users can verify packages came from your repo
- ✅ **Simpler CI/CD** - No secrets to configure in GitHub
- ✅ **npm recommends it** - Modern best practice for package publishing

## Current Implementation

The PKS CLI workflow **supports both methods**:

1. **Trusted Publishers** (recommended) - Uses OIDC provenance
2. **NPM_TOKEN** (fallback) - Traditional token-based auth

The workflow automatically detects which method to use:
- If `NPM_TOKEN` secret is set → uses token auth
- If `NPM_TOKEN` is not set → uses Trusted Publishers

## Setup Instructions

### Option 1: Trusted Publishers (Recommended)

#### Step 1: Configure on npm

For **each package** in your organization:

1. Go to https://www.npmjs.com/settings/pks-cli/packages
2. Click on a package (e.g., `@pks-cli/pks`)
3. Go to **Settings** → **Publishing Access**
4. Click **"Add Trusted Publisher"**
5. Configure:
   - **Provider**: `GitHub Actions`
   - **Organization**: `pksorensen`
   - **Repository**: `pks-cli`
   - **Workflow filename**: `release-npm.yml`
   - **Environment name**: (leave empty)
6. Click **"Add"**

#### Repeat for all 7 packages:
- `@pks-cli/pks` (main)
- `@pks-cli/pks-linux-x64`
- `@pks-cli/pks-linux-arm64`
- `@pks-cli/pks-osx-x64`
- `@pks-cli/pks-osx-arm64`
- `@pks-cli/pks-win-x64`
- `@pks-cli/pks-win-arm64`

#### Step 2: Remove NPM_TOKEN Secret

Once all packages have Trusted Publishers configured:

1. Go to https://github.com/pksorensen/pks-cli/settings/secrets/actions
2. **Delete** the `NPM_TOKEN` secret
3. The workflow will automatically use provenance

#### Step 3: Test

Push a commit to trigger a release. The workflow will:
- Detect no `NPM_TOKEN` is set
- Use OIDC authentication
- Publish with `--provenance` flag
- Show: "🔐 Using Trusted Publishers (provenance) for authentication"

### Option 2: NPM_TOKEN (Current/Fallback)

This is what you just set up. It works, but Trusted Publishers is more secure.

#### Keep using NPM_TOKEN if:
- You're not ready to migrate yet
- You need time to configure all 7 packages
- You want to test the workflow first

#### To use NPM_TOKEN:
1. Keep the `NPM_TOKEN` secret in GitHub
2. The workflow will detect it and use token auth
3. Shows: "🔐 Using NPM_TOKEN for authentication"

## How It Works

### With Trusted Publishers (Provenance)

```yaml
# Workflow has these permissions
permissions:
  id-token: write  # Allows OIDC token generation

# Publish command
npm publish package.tgz --provenance --access public --tag latest
```

**Authentication flow:**
1. GitHub Actions generates an OIDC token
2. npm verifies the token came from your repository
3. npm verifies the workflow filename matches
4. Publish succeeds with provenance attestation
5. Package shows "Provenance: GitHub Actions" on npm

### With NPM_TOKEN (Traditional)

```yaml
# Workflow uses secret
env:
  NODE_AUTH_TOKEN: ${{ secrets.NPM_TOKEN }}

# Publish command
npm publish package.tgz --access public --tag latest
```

**Authentication flow:**
1. npm reads `NODE_AUTH_TOKEN` from environment
2. Validates token against npm registry
3. Publish succeeds if token has permissions
4. No provenance attestation

## Verification

### Check if Provenance is Enabled

After publishing with provenance, verify on npm:

1. Go to package page: https://www.npmjs.com/package/@pks-cli/pks
2. Look for **"Provenance"** badge
3. Click to see:
   - Repository: `pksorensen/pks-cli`
   - Workflow: `release-npm.yml`
   - Commit SHA
   - Build attestation

### Check Workflow Logs

In GitHub Actions logs, you'll see:

```
🔐 Using Trusted Publishers (provenance) for authentication
📦 Publishing to 'latest' channel (stable)
Publishing pks-cli-linux-x64-1.0.0.tgz...
Publishing pks-cli-pks-1.0.0.tgz...
✅ All packages published successfully to 'latest' channel
```

## Migration Path

### Gradual Migration (Recommended)

1. **Week 1**: Keep `NPM_TOKEN`, configure 2-3 packages with Trusted Publishers
2. **Week 2**: Configure remaining packages
3. **Week 3**: Test a release (will still use NPM_TOKEN)
4. **Week 4**: Remove `NPM_TOKEN` secret, next release uses provenance

### Immediate Migration

1. Configure all 7 packages with Trusted Publishers (15 minutes)
2. Delete `NPM_TOKEN` from GitHub secrets
3. Next release automatically uses provenance

## Troubleshooting

### Error: "npm ERR! code ENEEDAUTH"

**Cause**: Neither NPM_TOKEN nor Trusted Publishers is configured

**Fix**: Set up one of the two methods above

### Error: "npm ERR! Provenance generation failed"

**Cause**: Missing `id-token: write` permission

**Fix**: Already configured in workflow, check if you modified permissions

### Error: "npm ERR! Repository not authorized"

**Cause**: Trusted Publisher not configured on npm for this package

**Fix**: Go through Step 1 setup for the specific package

### Packages show no provenance badge

**Cause**: Still using NPM_TOKEN

**Fix**: Remove `NPM_TOKEN` secret to switch to provenance

## Security Considerations

### Trusted Publishers (Provenance)

✅ **Secure by default**:
- No token to leak
- Automatic attestation
- Verifiable builds
- Can't be reused outside GitHub Actions

⚠️ **Requirements**:
- Must configure each package
- Repository must be public (for free accounts)
- Workflow filename must match exactly

### NPM_TOKEN

⚠️ **Security concerns**:
- Long-lived secret
- Can be leaked in logs
- Needs rotation
- Works outside CI/CD
- No provenance attestation

✅ **When appropriate**:
- Private repositories (paid accounts)
- Need flexibility
- Legacy systems

## Best Practices

1. **Use Trusted Publishers** for all public packages
2. **Enable 2FA** on npm account (works with both methods)
3. **Rotate NPM_TOKEN** regularly if using it
4. **Monitor publish logs** for unauthorized attempts
5. **Verify provenance** after each release

## References

- [npm Provenance Documentation](https://docs.npmjs.com/generating-provenance-statements)
- [GitHub OIDC Documentation](https://docs.github.com/en/actions/deployment/security-hardening-your-deployments/about-security-hardening-with-openid-connect)
- [npm Trusted Publishers Blog Post](https://github.blog/2023-04-19-introducing-npm-package-provenance/)

## Summary

**Current Status**: ✅ Workflow supports both methods

**Recommended**: Switch to Trusted Publishers by:
1. Configuring all 7 packages on npm
2. Removing `NPM_TOKEN` from GitHub secrets
3. Next release will use provenance automatically

**Benefit**: More secure, simpler, and follows npm's recommended practices.
