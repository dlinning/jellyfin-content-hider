#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

VERSION="${1:-}"
CHANGELOG="${2:-}"

# If version wasn't passed as an argument, prompt interactively
if [[ -z "$VERSION" ]]; then
  echo "=================================================="
  echo "       Jellyfin Content Hider - Release Tool      "
  echo "=================================================="
  read -r -p "Enter release version (e.g. 0.0.6): " VERSION
fi

# Clean and normalize version (strip leading 'v')
CLEAN_VERSION="${VERSION#v}"
CLEAN_VERSION="${CLEAN_VERSION// /}"

if [[ -z "$CLEAN_VERSION" ]]; then
  echo "Error: Version cannot be empty." >&2
  exit 1
fi

TAG_NAME="v${CLEAN_VERSION}"

# Check if tag already exists locally
if git rev-parse "$TAG_NAME" >/dev/null 2>&1; then
  echo "Error: Git tag '$TAG_NAME' already exists locally." >&2
  exit 1
fi

# If changelog wasn't passed as an argument, prompt interactively
if [[ -z "$CHANGELOG" ]]; then
  read -r -p "Enter change message / changelog: " CHANGELOG
fi

CHANGELOG="$(echo "$CHANGELOG" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"

if [[ -z "$CHANGELOG" ]]; then
  echo "Error: Change message cannot be empty." >&2
  exit 1
fi

echo ""
echo "--------------------------------------------------"
echo "Release Summary:"
echo "  Version:        $CLEAN_VERSION ($TAG_NAME)"
echo "  Change message: $CHANGELOG"
echo "--------------------------------------------------"
read -r -p "Proceed with packaging, commit, tag, and push? (y/N): " CONFIRM
if [[ ! "$CONFIRM" =~ ^[Yy]$ ]]; then
  echo "Release cancelled."
  exit 0
fi

echo ""
echo "==> Step 1: Packaging plugin and updating manifest.json..."
chmod +x "$ROOT_DIR/scripts/package-plugin.sh"
"$ROOT_DIR/scripts/package-plugin.sh" --version "$CLEAN_VERSION" --changelog "$CHANGELOG"

echo ""
echo "==> Step 2: Staging files..."
git add -A

echo ""
echo "==> Step 3: Creating commit..."
# If there are changes to commit, commit them; otherwise proceed with existing commit
if git diff --cached --quiet; then
  echo "No uncommitted file changes detected. Proceeding to tagging..."
else
  git commit -m "release: ${TAG_NAME} - ${CHANGELOG}"
fi

echo ""
echo "==> Step 4: Pushing commit to origin main..."
git push origin main

echo ""
echo "==> Step 5: Creating and pushing tag ${TAG_NAME}..."
git tag -a "$TAG_NAME" -m "Release ${TAG_NAME}: ${CHANGELOG}"
git push origin "$TAG_NAME"

echo ""
echo "=================================================="
echo "  Release ${TAG_NAME} successfully pushed!        "
echo "  GitHub Actions will now publish the release     "
echo "  assets and update GitHub Pages.                 "
echo "=================================================="
