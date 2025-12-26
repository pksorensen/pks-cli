#!/usr/bin/env bash
# Discovers all template packages in the templates/ directory
# Template packages are identified by the presence of a .csproj file
# Outputs JSON array for use in GitHub Actions matrix strategy

set -euo pipefail

TEMPLATES_DIR="templates"
SHARED_CONFIG=".releaserc.template.js"

# Initialize JSON array
echo "{"
echo "  \"include\": ["

FIRST=true

# Find all directories in templates/ that contain a .csproj file
for template_dir in "$TEMPLATES_DIR"/*; do
  if [ ! -d "$template_dir" ]; then
    continue
  fi

  # Look for .csproj file
  csproj_file=$(find "$template_dir" -maxdepth 1 -name "*.csproj" | head -n 1 || true)

  if [ -z "$csproj_file" ]; then
    # No .csproj file, skip this directory (it's not a template package)
    continue
  fi

  # Extract template name from directory name
  template_name=$(basename "$template_dir")

  # Extract PackageId from .csproj file
  package_id=$(grep -oP '<PackageId>\K[^<]+' "$csproj_file" || echo "")

  if [ -z "$package_id" ]; then
    echo "⚠️  Warning: No PackageId found in $csproj_file, skipping" >&2
    continue
  fi

  # Add comma separator for all items after the first
  if [ "$FIRST" = false ]; then
    echo "    ,"
  fi
  FIRST=false

  # Output JSON object for this template
  cat <<EOF
    {
      "template": "$template_name",
      "template_path": "$template_dir",
      "package_id": "$package_id",
      "config_file": "$SHARED_CONFIG"
    }
EOF

done

# Close JSON array
echo ""
echo "  ]"
echo "}"
