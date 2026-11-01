#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

VERSION=""

# Parse arguments: require singular -v or --version flag
while [[ $# -gt 0 ]]; do
  case "$1" in
    -v|--version)
      if [[ -n "$VERSION" ]]; then
        echo "Error: Multiple --version / -v flags specified. Only a single version flag is allowed." >&2
        exit 1
      fi
      if [[ -z "${2:-}" || "${2:-}" == -* ]]; then
        echo "Error: Missing value for $1 flag. Usage: $0 --version <version>" >&2
        exit 1
      fi
      VERSION="$2"
      shift 2
      ;;
    --version=*)
      if [[ -n "$VERSION" ]]; then
        echo "Error: Multiple --version / -v flags specified. Only a single version flag is allowed." >&2
        exit 1
      fi
      VERSION="${1#*=}"
      if [[ -z "$VERSION" ]]; then
        echo "Error: Missing value for --version flag. Usage: $0 --version <version>" >&2
        exit 1
      fi
      shift 1
      ;;
    -v=*)
      if [[ -n "$VERSION" ]]; then
        echo "Error: Multiple --version / -v flags specified. Only a single version flag is allowed." >&2
        exit 1
      fi
      VERSION="${1#*=}"
      if [[ -z "$VERSION" ]]; then
        echo "Error: Missing value for -v flag. Usage: $0 -v <version>" >&2
        exit 1
      fi
      shift 1
      ;;
    -h|--help)
      echo "Usage: $0 --version <version> (or -v <version>)"
      exit 0
      ;;
    *)
      echo "Error: Unexpected argument '$1'. Usage: $0 --version <version> (or -v <version>)" >&2
      exit 1
      ;;
  esac
done

if [[ -z "$VERSION" ]]; then
  echo "Error: --version (or -v) flag is required with no fallback. Usage: $0 --version <version>" >&2
  exit 1
fi

# Clean and normalize version formatting
CLEAN_VERSION="${VERSION#v}"
TAG_NAME="v${CLEAN_VERSION}"

# Ensure 4-part version for Jellyfin (e.g. 0.0.2 -> 0.0.2.0)
FOUR_PART_VERSION="$CLEAN_VERSION"
DOT_COUNT=$(echo "$FOUR_PART_VERSION" | awk -F. '{print NF-1}')
while [ "$DOT_COUNT" -lt 3 ]; do
  FOUR_PART_VERSION="${FOUR_PART_VERSION}.0"
  DOT_COUNT=$((DOT_COUNT + 1))
done

PLUGIN_GUID="3867d4a9-959b-476d-b1a6-a912a4741924"
REPO_OWNER="dlinning"
REPO_NAME="jellyfin-content-hider"
DIST_DIR="$ROOT_DIR/dist/ContentHider"
ZIP_NAME="ContentHider_${FOUR_PART_VERSION}.zip"
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
  "changelog": "Release ${TAG_NAME} of Jellyfin Content Hider.",
  "description": "Allows each user to hide specific shows, movies, and collections across Web, Mobile, and TV clients using an isolated on-disk SQLite database.",
  "guid": "$PLUGIN_GUID",
  "name": "Content Hider",
  "overview": "Per-user movie, TV show, and collection hiding across all clients.",
  "owner": "$REPO_OWNER",
  "targetAbi": "12.0.0.0",
  "timestamp": "$TIMESTAMP_ISO",
  "version": "$FOUR_PART_VERSION",
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

RELEASE_URL="https://github.com/${REPO_OWNER}/${REPO_NAME}/releases/download/${TAG_NAME}/${ZIP_NAME}"

# Update manifest.json by appending/updating this version while preserving older versions
python3 - <<PYEOF
import json, os

manifest_file = "$ROOT_DIR/manifest.json"
new_entry = {
    "version": "$FOUR_PART_VERSION",
    "changelog": "Release ${TAG_NAME} of Jellyfin Content Hider.",
    "targetAbi": "12.0.0.0",
    "sourceUrl": "$RELEASE_URL",
    "checksum": "$CHECKSUM",
    "timestamp": "$TIMESTAMP_SPACE"
}

data = []
if os.path.exists(manifest_file):
    try:
        with open(manifest_file, "r") as f:
            data = json.load(f)
    except Exception:
        data = []

if not data:
    data = [{
        "guid": "$PLUGIN_GUID",
        "name": "Content Hider",
        "description": "Allows each user to hide specific shows, movies, and collections across Web, Mobile, and TV clients using an isolated on-disk SQLite database.",
        "overview": "Per-user movie, TV show, and collection hiding across all clients.",
        "owner": "$REPO_OWNER",
        "category": "General",
        "versions": []
    }]

versions = [v for v in data[0].get("versions", []) if v.get("version") != "$FOUR_PART_VERSION"]
versions.insert(0, new_entry)
data[0]["versions"] = versions

with open(manifest_file, "w") as f:
    json.dump(data, f, indent=2)
    f.write("\n")
PYEOF

cp "$ROOT_DIR/manifest.json" "$ROOT_DIR/dist/manifest.json"

echo "=================================================================="
echo "Packaged plugin: $ZIP_PATH"
echo "MD5 Checksum:    $CHECKSUM"
echo "Release URL:     $RELEASE_URL"
echo "Manifest:        $ROOT_DIR/manifest.json"
echo "=================================================================="
