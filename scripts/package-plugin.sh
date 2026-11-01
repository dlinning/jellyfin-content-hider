#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

VERSION="0.1.0.0"
PLUGIN_GUID="3867d4a9-959b-476d-b1a6-a912a4741924"
DIST_DIR="$ROOT_DIR/dist/ContentHider"

rm -rf "$ROOT_DIR/dist"
mkdir -p "$DIST_DIR"

dotnet publish "$ROOT_DIR/Jellyfin.Plugin.ContentHider.csproj" -c Release -o "$DIST_DIR/publish"

cp "$DIST_DIR/publish/Jellyfin.Plugin.ContentHider.dll" "$DIST_DIR/"
if [[ -f "$DIST_DIR/publish/Jellyfin.Plugin.ContentHider.pdb" ]]; then
    cp "$DIST_DIR/publish/Jellyfin.Plugin.ContentHider.pdb" "$DIST_DIR/"
fi
rm -rf "$DIST_DIR/publish"

TIMESTAMP="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

cat > "$DIST_DIR/meta.json" <<EOF
{
  "category": "General",
  "changelog": "Initial release of Jellyfin Content Hider.",
  "description": "Allows each user to hide specific shows, movies, and collections across Web, Mobile, and TV clients.",
  "guid": "$PLUGIN_GUID",
  "name": "Content Hider",
  "overview": "Per-user movie and TV show hiding across all clients.",
  "owner": "jellyfin-content-hider",
  "targetAbi": "12.4.0.0",
  "timestamp": "$TIMESTAMP",
  "version": "$VERSION",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": [
    "Jellyfin.Plugin.ContentHider.dll"
  ]
}
EOF

(cd "$DIST_DIR" && zip -qr "../ContentHider_${VERSION}.zip" .)
echo "Packaged plugin in $DIST_DIR and $ROOT_DIR/dist/ContentHider_${VERSION}.zip"
