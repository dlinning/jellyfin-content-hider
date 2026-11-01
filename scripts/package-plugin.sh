#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

VERSION="0.1.0.0"
PLUGIN_GUID="3867d4a9-959b-476d-b1a6-a912a4741924"
REPO_OWNER="dlinning"
REPO_NAME="jellyfin-content-hider"
DIST_DIR="$ROOT_DIR/dist/ContentHider"
ZIP_NAME="ContentHider_${VERSION}.zip"
ZIP_PATH="$ROOT_DIR/dist/$ZIP_NAME"

rm -rf "$ROOT_DIR/dist"
mkdir -p "$DIST_DIR"

dotnet publish "$ROOT_DIR/Jellyfin.Plugin.ContentHider.csproj" -c Release -o "$DIST_DIR/publish"

cp "$DIST_DIR/publish/Jellyfin.Plugin.ContentHider.dll" "$DIST_DIR/"
if [[ -f "$DIST_DIR/publish/Jellyfin.Plugin.ContentHider.pdb" ]]; then
    cp "$DIST_DIR/publish/Jellyfin.Plugin.ContentHider.pdb" "$DIST_DIR/"
fi
rm -rf "$DIST_DIR/publish"

TIMESTAMP_ISO="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
TIMESTAMP_SPACE="$(date -u +"%Y-%m-%d %H:%M:%S")"

cat > "$DIST_DIR/meta.json" <<EOF
{
  "category": "General",
  "changelog": "Initial release of Jellyfin Content Hider.",
  "description": "Allows each user to hide specific shows, movies, and collections across Web, Mobile, and TV clients.",
  "guid": "$PLUGIN_GUID",
  "name": "Content Hider",
  "overview": "Per-user movie, TV show, and collection hiding across all clients.",
  "owner": "$REPO_OWNER",
  "targetAbi": "12.0.0.0",
  "timestamp": "$TIMESTAMP_ISO",
  "version": "$VERSION",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": [
    "Jellyfin.Plugin.ContentHider.dll"
  ]
}
EOF

(cd "$DIST_DIR" && zip -qr "$ZIP_PATH" .)

# Compute MD5 checksum across macOS (md5 -q) and Linux (md5sum)
if command -v md5 >/dev/null 2>&1; then
    CHECKSUM=$(md5 -q "$ZIP_PATH")
elif command -v md5sum >/dev/null 2>&1; then
    CHECKSUM=$(md5sum "$ZIP_PATH" | awk '{print $1}')
else
    CHECKSUM=""
fi

RELEASE_URL="https://github.com/${REPO_OWNER}/${REPO_NAME}/releases/download/v${VERSION}/${ZIP_NAME}"

cat > "$ROOT_DIR/manifest.json" <<EOF
[
  {
    "guid": "$PLUGIN_GUID",
    "name": "Content Hider",
    "description": "Allows each user to hide specific shows, movies, and collections across Web, Mobile, and TV clients.",
    "overview": "Per-user movie, TV show, and collection hiding across all clients.",
    "owner": "$REPO_OWNER",
    "category": "General",
    "versions": [
      {
        "version": "$VERSION",
        "changelog": "Initial release of Jellyfin Content Hider.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "$RELEASE_URL",
        "checksum": "$CHECKSUM",
        "timestamp": "$TIMESTAMP_SPACE"
      }
    ]
  }
]
EOF

cp "$ROOT_DIR/manifest.json" "$ROOT_DIR/dist/manifest.json"

echo "=================================================================="
echo "Packaged plugin: $ZIP_PATH"
echo "MD5 Checksum:    $CHECKSUM"
echo "Manifest:        $ROOT_DIR/manifest.json"
echo ""
echo "Jellyfin Repository URL (after committing and pushing to GitHub):"
echo "  https://raw.githubusercontent.com/${REPO_OWNER}/${REPO_NAME}/main/manifest.json"
echo "or via GitHub Pages (if enabled):"
echo "  https://${REPO_OWNER}.github.io/${REPO_NAME}/manifest.json"
echo "=================================================================="
