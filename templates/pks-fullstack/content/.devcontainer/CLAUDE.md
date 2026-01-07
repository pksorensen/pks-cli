# DevContainer Security Configuration

## Purpose

This DevContainer includes multiple security measures to protect the development environment:
1. **Restrictive firewall** (`init-firewall.sh`) - default-deny network policy
2. **Docker credential isolation** - prevents docker-in-docker from accessing host credentials

The firewall operates on a **default-deny** policy: all outbound connections are blocked except those explicitly allowed to trusted domains required for development work.

## Docker Credential Security

### VS Code Credential Helper Disabled

This template disables VS Code's automatic Docker credential helper via:

```json
"dev.containers.dockerCredentialHelper": false
```

**Why**: VS Code automatically creates `~/.docker/config.json` with a credential helper that allows docker-in-docker to access host Docker credentials via IPC. This is disabled for security to prevent the inner Docker daemon from using host credentials.

**Result**: Docker commands inside the container (via docker-in-docker) cannot access private registries using host credentials. You must run `docker login` inside the container if needed.

**Note**: The postStartCommand does NOT create `~/.docker/config.json` - it doesn't exist until you manually run `docker login`.

## Allowed Domains

The firewall permits access to:

- **GitHub**: Web, API, and Git operations (IP ranges fetched dynamically)
- **NPM Registry**: `registry.npmjs.org`
- **Anthropic Services**: `api.anthropic.com`, `statsig.anthropic.com`, `anthropic.gallery.vsassets.io`
- **Expo Services**: `expo.dev`, `api.expo.dev`, `exp.host`, `expo.io` (for React Native/Expo development)
- **Microsoft Services**: `aspire.dev`, `api.nuget.org`, `aka.ms`, `download.visualstudio.microsoft.com`, `dotnetcli.azureedge.net`, `learn.microsoft.com`, `docs.microsoft.com`
- **Development Tools**: VSCode Marketplace, Sentry, Statsig, Google Fonts
- **Local Network**: Communication with host machine and local services
- **DNS & SSH**: Essential for name resolution and secure connections
- **Localhost**: Loopback interface traffic

## Firewall Mechanics

1. **Default Policy**: DROP all traffic not explicitly allowed
2. **REJECT Action**: Blocked outbound connections receive immediate ICMP admin-prohibited responses
3. **IPSet Management**: Allowed IP ranges stored in `allowed-domains` ipset for efficient matching
4. **Dynamic Resolution**:
   - GitHub IP ranges fetched from GitHub Meta API
   - Microsoft Azure Active Directory ranges fetched from Azure Service Tags (updated weekly)
   - Other domains resolved via DNS
5. **Docker DNS Preservation**: Internal Docker DNS resolution (127.0.0.11) is preserved

## Container Rebuild Required

**IMPORTANT**: Changes to firewall configuration require a manual container rebuild by the human conductor.

**AI agents ARE permitted to edit the `init-firewall.sh` script** to add necessary domains for development tasks. However:

- AI agents **CANNOT modify iptables directly** (no sudo access in container)
- AI agents **CANNOT run the firewall script** (no sudo access in container)
- AI agents **CANNOT temporarily disable the firewall** (requires sudo)
- Changes **only take effect after container rebuild** by the human conductor
- The **human maintains security control** through the rebuild process

This approach allows AI agents to prepare firewall updates while ensuring the security boundary remains under explicit human control. The human reviews changes and decides when to rebuild the container.

**Note for debugging**: If you need to test with the firewall temporarily disabled, the human must manually run firewall commands with sudo privileges outside of the AI agent session.

## Verification

The firewall includes automatic verification on initialization:
- Confirms blockage of unauthorized sites (e.g., `example.com`)
- Confirms access to required sites (e.g., `api.github.com`)

If verification fails, container initialization is aborted.

## CDN Services and Specific IPs

Some services use CDNs that return different IPs on different queries. For security reasons, we use **specific IPs** rather than broad IP ranges wherever possible.

**Security Philosophy**: Only whitelist the minimum necessary IPs. Accept that CDN services may require periodic updates when IPs change.

### Google Services (Google Fonts)
- **Ranges**: 142.250.0.0/15, 172.217.0.0/16, 216.58.192.0/19 (~20M IPs)
- **Covers**: fonts.googleapis.com, fonts.gstatic.com
- **Note**: Google's massive CDN makes specific IPs impractical. Consider removing if security is paramount and using fallback fonts instead.

### Microsoft Services

#### Azure Active Directory (Authentication)
- **Source**: Microsoft Azure Service Tags (official)
- **URL**: https://www.microsoft.com/en-us/download/details.aspx?id=56519
- **Update Frequency**: Weekly (every Monday)
- **Ranges**: ~150 CIDR blocks (~50K IPs) from `AzureActiveDirectory` service tag
- **Covers**: login.microsoftonline.com, login.microsoft.com, login.live.com, account.microsoft.com
- **Note**: Downloaded automatically during firewall initialization. Solves dynamic DNS/load balancing issues with Microsoft auth services.

#### aka.ms (Redirect Service)
- **Specific IPs**: 2.17.1.249, 104.121.237.164
- **Covers**: aka.ms redirect service
- **Note**: These IPs may need periodic updates if Microsoft changes their aka.ms infrastructure.

## Troubleshooting EHOSTUNREACH Errors

If you see `connect EHOSTUNREACH` errors:

1. **Identify the blocked IP** from the error message
2. **Determine ownership** using `whois <IP>` or similar tools
3. **For CDN services** (like Google, Cloudflare, etc.): Add their published IP ranges to the firewall
4. **For single-domain services**: Add the domain to the DNS resolution loop
5. **Rebuild the container** manually for changes to take effect

Example: `Error: connect EHOSTUNREACH 172.217.16.74:443` indicates Google's IP range needs to be whitelisted.
