#!/bin/bash
# release.sh <version> : cut a release. Checks the tree, bumps VERSION, checks CHANGELOG, commits, tags, pushes.
# The tag push starts .github/workflows/release.yml, which builds the host, packages both zips and publishes the release.
#   ./scripts/release.sh 0.2.0
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd); cd "$ROOT"
VER=${1:?usage: release.sh <version>  (for example 0.2.0, or 0.2.0-rc1 for a pre-release)}
[[ "$VER" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]] || { echo "version must look like 1.2.3 or 1.2.3-rc1"; exit 1; }
[ -z "$(git status --porcelain)" ] || { echo "the working tree has uncommitted changes; commit or stash them first"; exit 1; }
[ "$(git rev-parse --abbrev-ref HEAD)" = "main" ] || { echo "release from the main branch"; exit 1; }
git rev-parse "v$VER" >/dev/null 2>&1 && { echo "tag v$VER already exists"; exit 1; }
grep -q "^## \[$VER\]" CHANGELOG.md || { echo "CHANGELOG.md has no '## [$VER]' section; add one first"; exit 1; }
echo "$VER" > VERSION
git add VERSION
git diff --cached --quiet || git commit -q -m "Release v$VER"
git tag -a "v$VER" -m "LANDock v$VER"
echo "created tag v$VER. Pushing it starts the release build."
read -r -p "push main and v$VER now? [y/N] " yn
[ "$yn" = "y" ] && git push origin main "v$VER" || echo "not pushed; push later with: git push origin main v$VER"
