#!/bin/bash
set -euo pipefail  # Exit on error, undefined vars, and pipeline failures
IFS=$'\n\t'       # Stricter word splitting

# 1. Save ALL Docker-related iptables rules BEFORE any flushing
echo "Saving Docker iptables configuration..."
DOCKER_FILTER_SAVE=$(iptables-save | grep -E "^-A (DOCKER|DOCKER-CT|DOCKER-BRIDGE|FORWARD)" || true)
DOCKER_NAT_SAVE=$(iptables-save -t nat | grep -E "^(-A DOCKER|POSTROUTING.*MASQUERADE)" || true)

# Flush existing rules and delete existing ipsets
iptables -F
iptables -X
iptables -t nat -F
iptables -t nat -X
iptables -t mangle -F
iptables -t mangle -X
ipset destroy allowed-domains 2>/dev/null || true

# 2. Recreate Docker chains (required before restoring rules)
echo "Recreating Docker iptables chains..."
iptables -t nat -N DOCKER 2>/dev/null || true
iptables -t nat -N DOCKER_OUTPUT 2>/dev/null || true
iptables -t nat -N DOCKER_POSTROUTING 2>/dev/null || true
iptables -N DOCKER 2>/dev/null || true
iptables -N DOCKER-ISOLATION-STAGE-1 2>/dev/null || true
iptables -N DOCKER-ISOLATION-STAGE-2 2>/dev/null || true
iptables -N DOCKER-USER 2>/dev/null || true
iptables -N DOCKER-FORWARD 2>/dev/null || true
iptables -N DOCKER-CT 2>/dev/null || true
iptables -N DOCKER-BRIDGE 2>/dev/null || true

# 3. Restore saved Docker NAT rules
if [ -n "$DOCKER_NAT_SAVE" ]; then
    echo "Restoring Docker NAT rules..."
    while IFS= read -r rule; do
        # Convert -A format to iptables command
        iptables -t nat ${rule#-A } 2>/dev/null || echo "Warning: Failed to restore NAT rule: $rule"
    done <<< "$DOCKER_NAT_SAVE"
fi

# 4. Restore saved Docker filter rules
if [ -n "$DOCKER_FILTER_SAVE" ]; then
    echo "Restoring Docker filter rules..."
    while IFS= read -r rule; do
        # Convert -A format to iptables command
        iptables ${rule#-A } 2>/dev/null || echo "Warning: Failed to restore filter rule: $rule"
    done <<< "$DOCKER_FILTER_SAVE"
fi

echo "Docker iptables restoration complete"

# First allow DNS and localhost before any restrictions
# Allow outbound DNS
iptables -A OUTPUT -p udp --dport 53 -j ACCEPT
# Allow inbound DNS responses
iptables -A INPUT -p udp --sport 53 -j ACCEPT
# Allow outbound SSH
iptables -A OUTPUT -p tcp --dport 22 -j ACCEPT
# Allow inbound SSH responses
iptables -A INPUT -p tcp --sport 22 -m state --state ESTABLISHED -j ACCEPT
# Allow localhost
iptables -A INPUT -i lo -j ACCEPT
iptables -A OUTPUT -o lo -j ACCEPT

# Create ipset with CIDR support
ipset create allowed-domains hash:net

# Fetch GitHub meta information and aggregate + add their IP ranges
echo "Fetching GitHub IP ranges..."
gh_ranges=$(curl -s https://api.github.com/meta)
if [ -z "$gh_ranges" ]; then
    echo "ERROR: Failed to fetch GitHub IP ranges"
    exit 1
fi

if ! echo "$gh_ranges" | jq -e '.web and .api and .git' >/dev/null; then
    echo "ERROR: GitHub API response missing required fields"
    exit 1
fi

echo "Processing GitHub IPs..."
while read -r cidr; do
    if [[ ! "$cidr" =~ ^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}/[0-9]{1,2}$ ]]; then
        echo "ERROR: Invalid CIDR range from GitHub meta: $cidr"
        exit 1
    fi
    echo "Adding GitHub range $cidr"
    ipset add allowed-domains "$cidr" -exist
done < <(echo "$gh_ranges" | jq -r '(.web + .api + .git)[]' | aggregate -q)

# Fetch Microsoft Azure Service Tags for authentication services
# URL: https://www.microsoft.com/en-us/download/details.aspx?id=56519
# Updates: Weekly (every Monday)
# This solves the dynamic DNS problem for login.microsoftonline.com and related auth services
echo "Fetching Microsoft Azure Service Tags..."

# Try multiple date formats to handle weekly updates
# Current date: Nov 20, 2025, try recent Mondays
SERVICETAGS_CACHE="/tmp/azure-servicetags.json"
SERVICETAGS_SUCCESS=false

for date_suffix in "20251117" "20251124" "20251110" "20251103"; do
    SERVICETAGS_URL="https://download.microsoft.com/download/7/1/d/71d86715-5596-4529-9b13-da13a5de5b63/ServiceTags_Public_${date_suffix}.json"
    echo "Trying Azure Service Tags URL with date: $date_suffix"
    
    if curl -s -o "$SERVICETAGS_CACHE" "$SERVICETAGS_URL"; then
        # Verify the file is valid JSON and contains expected data
        if jq -e '.values[] | select(.name == "AzureActiveDirectory")' "$SERVICETAGS_CACHE" >/dev/null 2>&1; then
            echo "Successfully downloaded Azure Service Tags from $date_suffix"
            SERVICETAGS_SUCCESS=true
            break
        else
            echo "Downloaded file for $date_suffix is invalid or missing AzureActiveDirectory data"
        fi
    else
        echo "Failed to download Azure Service Tags for date: $date_suffix"
    fi
done

if [ "$SERVICETAGS_SUCCESS" != "true" ]; then
    echo "WARNING: Failed to download Azure Service Tags from all attempted URLs"
    echo "Continuing without Azure AD IP ranges - some Microsoft services may be blocked"
    # Don't exit - continue with the rest of the firewall setup
else
    echo "Processing Azure Active Directory IP ranges..."
    echo "DEBUG: Extracting AAD ranges from JSON..."
    aad_ranges=$(jq -r '.values[] | select(.name == "AzureActiveDirectory") | .properties.addressPrefixes[] | select(contains(":") | not)' "$SERVICETAGS_CACHE")
    echo "DEBUG: Extraction completed"

    if [ -z "$aad_ranges" ]; then
        echo "WARNING: No AzureActiveDirectory ranges found in service tags"
        echo "Continuing without Azure AD IP ranges"
    else
        echo "DEBUG: Starting to process $(echo "$aad_ranges" | wc -l) Azure AD IP ranges..."
        
        # Temporarily disable strict error handling for Azure AD processing
        set +e
        
        range_count=0
        skipped_count=0
        
        while read -r cidr; do
            # Skip empty lines
            if [ -z "$cidr" ]; then
                continue
            fi
            
            echo "DEBUG: Processing CIDR: '$cidr'"
            
            # More robust CIDR validation
            if [[ "$cidr" =~ ^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}/[0-9]{1,2}$ ]]; then
                # Try to add to ipset with error handling
                ipset add allowed-domains "$cidr" -exist 2>/dev/null
                if [ $? -eq 0 ]; then
                    ((range_count++))
                else
                    echo "WARNING: Failed to add CIDR range to ipset: $cidr"
                    ((skipped_count++))
                fi
            else
                echo "WARNING: Invalid CIDR format from Azure service tags: '$cidr'"
                ((skipped_count++))
            fi
            
            # Show progress every 10 ranges for better visibility
            if [ $(((range_count + skipped_count) % 10)) -eq 0 ]; then
                echo "DEBUG: Processed $((range_count + skipped_count)) ranges so far (added: $range_count, skipped: $skipped_count)..."
            fi
        done < <(echo "$aad_ranges")

        # Re-enable strict error handling
        set -e
        
        echo "Azure AD processing completed: added $range_count ranges, skipped $skipped_count invalid ranges"
        echo "DEBUG: Azure AD processing completed successfully"
    fi
fi

echo "DEBUG: Moving to CDN IP processing..."



# Add specific IPs for CDN services that return multiple IPs
# NOTE: These may need periodic updates if the services change their IPs
echo "Adding specific IPs for CDN services (Google Fonts, aka.ms, Cloudflare/Docker Hub, Quay.io CDN, Microsoft CDN)..."
for ip in \
    "142.250.0.0/15" \
    "172.217.0.0/16" \
    "216.58.192.0/19" \
    "2.17.1.249" \
    "104.121.237.164" \
    "104.16.0.0/13" \
    "172.64.0.0/13" \
    "5.186.0.0/16" \
    "57.150.0.0/16"; do
    echo "Adding CDN IP/range $ip"
    ipset add allowed-domains "$ip" -exist
done
echo "DEBUG: CDN IP processing completed"

# Resolve and add other allowed domains
# Use more resilient DNS resolution with retries and warnings instead of hard failures
echo "Resolving and adding allowed domains..."

# Temporarily disable strict error handling for DNS resolution
set +e

failed_domains=0
total_domains=0

for domain in \
    "registry.npmjs.org" \
    "api.anthropic.com" \
    "sentry.io" \
    "statsig.anthropic.com" \
    "anthropic.gallery.vsassets.io" \
    "dbaeumer.gallery.vsassets.io" \
    "statsig.com" \
    "marketplace.visualstudio.com" \
    "vscode.blob.core.windows.net" \
    "update.code.visualstudio.com" \
    "fonts.googleapis.com" \
    "fonts.gstatic.com" \
    "aspire.dev" \
    "api.nuget.org" \
    "download.visualstudio.microsoft.com" \
    "dotnetcli.azureedge.net" \
    "aka.ms" \
    "expo.dev" \
    "api.expo.dev" \
    "exp.host" \
    "expo.io" \
    "tunnelsassetsprod.blob.core.windows.net" \
    "global.rel.tunnels.api.visualstudio.com" \
    "learn.microsoft.com" \
    "reactnativereusables.com" \
    "ui.shadcn.com" \
    "quay.io" \
    "docs.microsoft.com" \
    "registry-1.docker.io" \
    "auth.docker.io" \
    "distribution.virk.dk" \
    "production.cloudflare.docker.com"; do
    
    ((total_domains++))
    echo "DEBUG: Starting resolution for domain: $domain"
    
    # Try DNS resolution with timeout
    ips=$(timeout 10s dig +noall +answer A "$domain" | awk '$4 == "A" {print $5}' 2>/dev/null || true)
    echo "DEBUG: DNS query completed for $domain, got IPs: $ips"
    
    if [ -z "$ips" ]; then
        echo "WARNING: Failed to resolve $domain (timeout or no A records)"
        ((failed_domains++))
        continue
    fi
    
    domain_ips_added=0
    while read -r ip; do
        if [[ "$ip" =~ ^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}$ ]]; then
            echo "Adding $ip for $domain"
            ipset add allowed-domains "$ip" -exist
            ((domain_ips_added++))
        else
            echo "WARNING: Invalid IP from DNS for $domain: $ip"
        fi
    done < <(echo "$ips")
    
    if [ "$domain_ips_added" -eq 0 ]; then
        echo "WARNING: No valid IPs found for $domain"
        ((failed_domains++))
    else
        echo "Successfully added $domain_ips_added IPs for $domain"
    fi
done

# Re-enable strict error handling
set -e

echo "DNS Resolution Summary: $((total_domains - failed_domains))/$total_domains domains resolved successfully"

if [ "$failed_domains" -gt 0 ]; then
    echo "WARNING: $failed_domains domains failed to resolve - some services may be blocked"
fi

# Only fail if too many critical domains failed (more than half)
if [ "$failed_domains" -gt $((total_domains / 2)) ]; then
    echo "ERROR: Too many domains failed to resolve ($failed_domains/$total_domains) - network may be unstable"
    set -e
    exit 1
fi

# Get host IP from default route
HOST_IP=$(ip route | grep default | cut -d" " -f3)
if [ -z "$HOST_IP" ]; then
    echo "ERROR: Failed to detect host IP"
    exit 1
fi

HOST_NETWORK=$(echo "$HOST_IP" | sed "s/\.[0-9]*$/.0\/24/")
echo "Host network detected as: $HOST_NETWORK"

# Allow Docker's default bridge network IP range
# Docker uses 172.16.0.0/12 for bridge networks (covers 172.16.0.0 - 172.31.255.255)
# This allows communication with containers in networks created by Docker/Aspire at any time
echo "Allowing Docker bridge network range (172.16.0.0/12)..."
iptables -A INPUT -s 172.16.0.0/12 -j ACCEPT
iptables -A OUTPUT -d 172.16.0.0/12 -j ACCEPT

# Set up remaining iptables rules
iptables -A INPUT -s "$HOST_NETWORK" -j ACCEPT
iptables -A OUTPUT -d "$HOST_NETWORK" -j ACCEPT

# Set default policies to DROP first
iptables -P INPUT DROP
iptables -P FORWARD DROP
iptables -P OUTPUT DROP

# First allow established connections for already approved traffic
iptables -A INPUT -m state --state ESTABLISHED,RELATED -j ACCEPT
iptables -A OUTPUT -m state --state ESTABLISHED,RELATED -j ACCEPT

# Then allow only specific outbound traffic to allowed domains
iptables -A OUTPUT -m set --match-set allowed-domains dst -j ACCEPT

# Explicitly REJECT all other outbound traffic for immediate feedback
iptables -A OUTPUT -j REJECT --reject-with icmp-admin-prohibited

echo "Firewall configuration complete"
echo "Verifying firewall rules..."
if curl --connect-timeout 5 https://example.com >/dev/null 2>&1; then
    echo "ERROR: Firewall verification failed - was able to reach https://example.com"
    exit 1
else
    echo "Firewall verification passed - unable to reach https://example.com as expected"
fi

# Verify GitHub API access
if ! curl --connect-timeout 5 https://api.github.com/zen >/dev/null 2>&1; then
    echo "ERROR: Firewall verification failed - unable to reach https://api.github.com"
    exit 1
else
    echo "Firewall verification passed - able to reach https://api.github.com as expected"
fi