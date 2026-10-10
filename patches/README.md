# Patches

The source of truth for our changes to Apollo is the **`landock` branch of our fork**,
<https://github.com/shohamh/Apollo/tree/landock>, which sits on top of an upstream release tag. The files here are
**exports of those commits** (made with `git format-patch`) for reading and for the source offer in the host release;
the build does not apply them. `scripts/sync-upstream.sh` re-exports them after an upstream sync.

| File | What it does |
|---|---|
| `0001-virtual-display-extend-topology.patch` | Apollo (`src/platform/windows/virtual_display.cpp`): wait up to 8 s for a new virtual display and ask Windows for an extended topology if it attached as a clone of the main screen. |
| `debug/touchport-log.patch` | Debug only, never built into a release: logs the pointer mapping values and each absolute-mouse event, used to find out why the pointer test looked wrong on one machine. |
