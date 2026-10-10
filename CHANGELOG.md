# Changelog

All notable changes to LANDock. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the
project uses [Semantic Versioning](https://semver.org/). Releases are cut with `scripts/release.sh`; the section for
a version becomes the text of its GitHub release.

## [0.1.0] - 2026-10-10

First release. Experimental: tested on a single laptop (host and client on the same machine over loopback); the first
test with a second PC is still to come.

### Added
- **The LANDock app** (Windows, per-user, self-contained): a tray icon (click for the settings window; right-click for
  start/stop host, start/stop displays, check for updates) and a settings window with Host, Client, Updates and
  General/About tabs. It also has a command line (`LANDock host up`, `LANDock client pair|start|stop|status`, ...).
- **Host** (the laptop): three Apollo instances (one per virtual display, ports 49989 / 50989 / 51989) under a supervisor
  that restarts any that crash; per-machine configs and credentials generated automatically; a random pairing PIN with
  a pairing window (10 minutes by default); a layout watcher that keeps the displays in the order
  laptop | M1 | M2 | TV whatever order streams connect in; keep-awake; cleanup of orphaned virtual displays;
  a one-click Windows Firewall rule; a clear message when the SudoVDA driver is missing.
- **Client** (the desktop PC): one Moonlight per stream, each in its own folder with its own pairing; pairing that
  retries when the PIN arrives late; every window placed on a chosen monitor (fullscreen or windowed) once the first
  video frame arrives.
- **Automatic updates**: the app looks at the GitHub releases every few minutes (default 10, configurable), compares a
  per-part version (app, host, client) from the release's `manifest.json`, downloads only what changed, checks the
  SHA256, and installs after the user agrees (or by itself if "install automatically" is chosen; host and client parts
  then wait until nothing is streaming).
- **Windows installer** built in CI with Inno Setup: per-user (no administrator rights), choose Host and/or Client,
  optional start with Windows and firewall rule, clean uninstall.
- **`vddtool`**: list displays, set their positions, remove orphaned virtual displays, extend/clone the topology.
- **Patch 0001 for Apollo v0.4.8**: fixes virtual displays that Windows attaches as a clone of the main screen
  (longer wait plus an explicit extend), so a virtual display always becomes its own monitor.
- **Tests**: unit tests for the app (`app/LANDock.Tests`, including the updater against a fake GitHub) and an
  end-to-end runner (`tests/run-display-tests.sh`) that opens a Notepad on each virtual display, moves it around,
  types into it (read back and compared), sweeps the mouse across all displays, and drags windows across every display
  with the real mouse.
- **CI/CD**: GitHub Actions builds the app, Apollo (from our fork) and Moonlight (from our fork) from source, packages
  the installer and the update parts, and publishes a release on a version tag. A weekly job tells us when Apollo or
  Moonlight publish a new release, and `scripts/sync-upstream.sh` rebases our forks onto it.

### Known limitations
- Only Windows hosts and clients; the host needs the SudoVDA driver (installed by Apollo's installer).
- The installer and the executables are not code-signed, so Windows SmartScreen may warn on first run.
- Pointer/keyboard from a *separate* client machine, audio, and performance over Wi-Fi are untested.
- Rotated (portrait) virtual displays are untested.
