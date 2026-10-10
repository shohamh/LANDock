#!/bin/bash
# sync-upstream.sh <apollo|moonlight> [<upstream release tag or branch>] : pull upstream changes into our fork.
#
# What it does (nothing is released or pinned automatically):
#   1. fast-forwards the fork's default branch to upstream (`gh repo sync`), so the fork stays a clean mirror
#   2. rebuilds our `landock` branch on top of the new upstream release: it takes the new tag and re-applies, one by one,
#      the commits that are ours (the ones between the old upstream base and the pinned commit in scripts/versions.env)
#   3. pushes the result to the fork as a CANDIDATE branch `landock-<tag>` (asks first; NO_PUSH=1 stops before pushing)
#   4. prints the lines to put into scripts/versions.env once the candidate has been built and tested
# If one of our commits conflicts with the new upstream code it stops and tells you where; resolve it, then continue
# with `git cherry-pick --continue` in build/sync/<name> and push the branch yourself.
#
# Needs: gh (logged in, with access to the forks), git.   Example:   ./scripts/sync-upstream.sh apollo v0.4.9
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
set -a; source "$ROOT/scripts/versions.env"; set +a
NAME=${1:?usage: sync-upstream.sh <apollo|moonlight> [<tag or branch>]}
case "$NAME" in
  apollo)    FORK=${APOLLO_REPO#https://github.com/};    FORK=${FORK%.git}; UP=$APOLLO_UPSTREAM_REPO;    BASE=$APOLLO_UPSTREAM_BASE;    OURS=$APOLLO_COMMIT ;;
  moonlight) FORK=${MOONLIGHT_REPO#https://github.com/}; FORK=${FORK%.git}; UP=$MOONLIGHT_UPSTREAM_REPO; BASE=$MOONLIGHT_UPSTREAM_BASE; OURS=$MOONLIGHT_COMMIT ;;
  *) echo "first argument must be apollo or moonlight"; exit 1 ;;
esac
UPPER=$(echo "$NAME" | tr a-z A-Z)
TARGET=${2:-$(gh api "repos/$UP/releases/latest" --jq .tag_name)}
say() { echo "[sync-upstream] $*"; }

say "$NAME: fork $FORK, upstream $UP, our base $BASE, target $TARGET"
say "1/4 fast-forward the fork's default branch to upstream"
gh repo sync "$FORK" --source "$UP" || say "  (could not fast-forward the default branch; continuing)"

WORK=$ROOT/build/sync/$NAME
say "2/4 work copy in build/sync/$NAME"
rm -rf "$WORK"; mkdir -p "$(dirname "$WORK")"
git clone -q --filter=blob:none "https://github.com/$FORK.git" "$WORK"
cd "$WORK"
git remote add upstream "https://github.com/$UP.git"
git fetch -q upstream --tags
git rev-parse -q --verify "refs/tags/$TARGET^{commit}" >/dev/null || git rev-parse -q --verify "upstream/$TARGET^{commit}" >/dev/null \
  || { echo "ERROR: $TARGET is neither a tag nor a branch of $UP" >&2; exit 1; }
NEW=landock-$TARGET
START=$(git rev-parse -q --verify "refs/tags/$TARGET^{commit}" || git rev-parse "upstream/$TARGET^{commit}")
git checkout -q -b "$NEW" "$START"

OUR_COMMITS=$(git rev-list --reverse "$BASE..$OURS" || true)
COUNT=$(echo -n "$OUR_COMMITS" | grep -c . || true)
say "3/4 re-applying our $COUNT commit(s) on $TARGET"
for c in $OUR_COMMITS; do
  say "  $(git log -1 --format='%h %s' "$c")"
  if ! git cherry-pick -x "$c" >/dev/null 2>&1; then
    echo
    echo "CONFLICT while applying $c onto $TARGET."
    echo "Resolve it in: $WORK   (git status, fix, git add, git cherry-pick --continue), then push the branch with:"
    echo "  git push origin $NEW"
    exit 2
  fi
done
NEW_SHA=$(git rev-parse HEAD)

say "4/4 candidate $NEW at $NEW_SHA"
if [ "${NO_PUSH:-}" != "1" ]; then
  read -r -p "push $NEW to $FORK now? [y/N] " yn
  [ "$yn" = "y" ] && git push -q origin "$NEW" || { echo "not pushed. Push later with: git -C $WORK push origin $NEW"; }
fi

# export our commits as patches for the source offer (Apollo only; Moonlight carries none yet)
if [ "$NAME" = apollo ] && [ "$COUNT" -gt 0 ]; then
  rm -f "$ROOT"/patches/0*.patch
  git format-patch -q "$START..$NEW" -o "$ROOT/patches"
  say "exported $COUNT patch file(s) to patches/"
fi

echo
echo "Next:"
echo "  1. build it:   gh workflow run build.yml -R <this repository> -f ${NAME}_ref=$NEW"
echo "  2. test it:    ./tests/run-display-tests.sh   (on a machine with the host built from the candidate)"
echo "  3. if good, change scripts/versions.env to:"
echo "       ${UPPER}_REF=$NEW"
echo "       ${UPPER}_COMMIT=$NEW_SHA"
echo "       ${UPPER}_UPSTREAM_BASE=$TARGET"
echo "     add a CHANGELOG entry, commit, and cut a release with scripts/release.sh"
