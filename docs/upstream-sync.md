# Pulling upstream changes into our forks

LANDock builds on **Apollo** (the host) and **Moonlight** (the client). Both are kept as **forks** under this project's
GitHub account so that we can carry our own changes and still take new upstream releases:

| | Fork | Upstream | Our branch | Based on |
|---|---|---|---|---|
| Apollo | [shohamh/Apollo](https://github.com/shohamh/Apollo) | [ClassicOldSong/Apollo](https://github.com/ClassicOldSong/Apollo) | `landock` | `v0.4.8` + 1 commit (virtual display attach fix) |
| Moonlight | [shohamh/moonlight-qt](https://github.com/shohamh/moonlight-qt) | [moonlight-stream/moonlight-qt](https://github.com/moonlight-stream/moonlight-qt) | `landock` | `v6.2.0` + no commits yet |

## How the branches are organised

- **The fork's default branch** (`master`) is a clean mirror of upstream. We never commit to it, so GitHub's
  "Sync fork" button and `gh repo sync` always fast-forward it.
- **`landock`** is upstream's release tag plus *only our commits*. That is what LANDock builds. Keeping our commits few,
  small and well described is what makes pulling upstream cheap.
- **`scripts/versions.env`** pins the exact commit of each `landock` branch, so a build never changes by surprise.
- `patches/` holds exports of our commits (for reading and for the GPL source offer in the host release); the build
  uses the fork, not the patch files.

## Pulling in a new upstream release

1. **Notice it.** The *Upstream watch* workflow runs every Monday and opens an issue when Apollo or Moonlight has a
   release newer than the one we are based on. (You can also run it by hand from the Actions tab.)
2. **Create a candidate branch.** On a machine with `gh` logged in:

   ```bash
   ./scripts/sync-upstream.sh apollo v0.4.9        # or: moonlight v6.3.0   (no tag = latest upstream release)
   ```

   It fast-forwards the fork's default branch, starts a new branch `landock-v0.4.9` at the upstream tag, re-applies our
   commits one by one, and pushes it to the fork (after asking). If one of our commits conflicts with the new upstream
   code it stops and tells you where; fix it, `git cherry-pick --continue`, push.
3. **Build the candidate from source in CI.** Actions > **Build from source** > Run workflow, with `apollo_ref` (or
   `moonlight_ref`) set to the candidate branch. This builds without touching the pinned versions or publishing
   anything. The script prints the equivalent `gh workflow run` command.
4. **Test it.** Unpack or build the candidate on a PC and run `./tests/run-display-tests.sh`, then try a real stream
   from the desktop PC.
5. **Adopt it.** Change the three `*_REF`, `*_COMMIT`, `*_UPSTREAM_BASE` lines in `scripts/versions.env` (the script
   prints them), add a `CHANGELOG.md` entry, commit, and publish with `./scripts/release.sh <version>`.

## Adding a new change of our own

Make it in a clone of the fork on the `landock` branch (one focused commit with a message that says *why*), push, then
update the pinned commit in `scripts/versions.env` and re-export the patches
(`git format-patch <upstream-tag>..landock -o patches/`). If the change would help everyone, open a pull request
upstream too; once it is merged there, our commit disappears the next time we sync.

## Good to know

- Apollo's `master` was not buildable at the commit we looked at (a field used before it existed), which is one reason
  we base on release tags rather than on `master`.
- The Apollo release tag `v0.4.8` pins an older hash for its Boost download than the file GitHub serves today;
  `scripts/versions.env` carries the corrected hash. A newer Apollo release may fix that upstream, in which case the
  workaround can go.
- Syncing never touches `host/` or `client/` scripts; only the pinned sources change. Our tests (`tests/`) are what
  tells you whether a new upstream release still behaves the way LANDock needs.
