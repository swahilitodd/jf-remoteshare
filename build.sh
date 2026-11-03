#!/bin/bash
# Builds both plugin variants and packages each as an install zip in ./dist:
#   RemoteShare_<ver>_jellyfin-12.zip     (Jellyfin 12.x, .NET 10)
#   RemoteShare_<ver>_jellyfin-10.11.zip  (Jellyfin 10.11.x, .NET 9)
set -euo pipefail
cd "$(dirname "$0")"

VERSION="${1:-0.1.0.0}"
PROJECT=Jellyfin.Plugin.RemoteShare
GUID=9a871b46-6581-4f0c-a1cd-54da8de66932

rm -rf dist && mkdir -p dist

package() {
  local tfm="$1" abi="$2" label="$3"
  local out="dist/build-$label"
  dotnet publish "$PROJECT" -c Release -f "$tfm" -o "$out" -p:Version="$VERSION" -p:AssemblyVersion="$VERSION" -p:FileVersion="$VERSION" >/dev/null
  local stage="dist/RemoteShare_$VERSION"
  rm -rf "$stage" && mkdir -p "$stage"
  cp "$out/$PROJECT.dll" "$stage/"
  cat > "$stage/meta.json" <<EOF
{
  "category": "General",
  "changelog": "",
  "description": "Share libraries with other Jellyfin servers, and add libraries shared with you to your own.",
  "guid": "$GUID",
  "name": "Remote Share",
  "overview": "Server-to-server library sharing",
  "owner": "swahilitodd",
  "targetAbi": "$abi",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "$VERSION",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": ["$PROJECT.dll"]
}
EOF
  (cd dist && zip -qr "RemoteShare_${VERSION}_jellyfin-$label.zip" "RemoteShare_$VERSION")
  rm -rf "$stage" "$out"
  echo "dist/RemoteShare_${VERSION}_jellyfin-$label.zip"
}

package net10.0 12.0.0.0 12
package net9.0 10.11.0.0 10.11
