#!/usr/bin/env bash
# Builds the plugin for Jellyfin 12.x and 10.11.x and packages installable zips into ./dist.
# Uses a local .NET 10 SDK if present, otherwise Docker.
#
# Optional: REPO_URL=https://example.com/jellyfin ./build.sh
#   also writes dist/manifest.json so you can add the folder as a Jellyfin plugin repository.
set -euo pipefail

VERSION="1.1.0.0"
ROOT="$(cd "$(dirname "$0")" && pwd)"
PROJ_DIR="src/Jellyfin.Plugin.JellyseerrRequester"
DIST="$ROOT/dist"
rm -rf "$DIST" && mkdir -p "$DIST"

build() { # $1 = JellyfinTarget, $2 = output dir (relative to repo root)
  if command -v dotnet >/dev/null 2>&1; then
    dotnet publish "$ROOT/$PROJ_DIR" -c Release -p:JellyfinTarget="$1" -o "$ROOT/$2"
  else
    docker run --rm -v "$ROOT":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
      dotnet publish "$PROJ_DIR" -c Release -p:JellyfinTarget="$1" -o "/src/$2"
  fi
}

package() { # $1 = JellyfinTarget, $2 = targetAbi, $3 = zip suffix
  local out="build/$3"
  rm -rf "$ROOT/$out"
  build "$1" "$out"
  local zip="jellyseerr-requester_${VERSION}_${3}.zip"
  (cd "$ROOT/$out" && zip -q "$DIST/$zip" Jellyfin.Plugin.JellyseerrRequester.dll)
  echo "$zip $2"
}

ENTRIES=()
ENTRIES+=("$(package 12 12.0.0.0 jf12 | tail -1)")
# ENTRIES+=("$(package 10.11 10.11.0.0 jf10.11 | tail -1)")

if [[ -n "${REPO_URL:-}" ]]; then
  python3 - "$DIST" "$REPO_URL" "$VERSION" "${ENTRIES[@]}" <<'PY'
import hashlib, json, os, sys, datetime
dist, url, version, *entries = sys.argv[1:]
versions = []
for e in entries:
    zip_name, abi = e.split()
    md5 = hashlib.md5(open(os.path.join(dist, zip_name), "rb").read()).hexdigest()
    versions.append({
        "version": version,
        "changelog": "See README.",
        "targetAbi": abi,
        "sourceUrl": url.rstrip("/") + "/" + zip_name,
        "checksum": md5,
        "timestamp": datetime.datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%SZ"),
    })
manifest = [{
    "guid": "980e2692-ff98-45ad-9eb2-0df748ceb82e",
    "name": "Jellyseerr Requester",
    "description": "Search and request movies and TV shows from Jellyseerr directly inside Jellyfin.",
    "overview": "Request movies and TV shows from Jellyseerr inside Jellyfin",
    "owner": "sylvain",
    "category": "General",
    "versions": versions,
}]
json.dump(manifest, open(os.path.join(dist, "manifest.json"), "w"), indent=2)
print("wrote", os.path.join(dist, "manifest.json"))
PY
fi

ls -1 "$DIST"
