# Changelog

All notable changes to LANDock. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the
project uses [Semantic Versioning](https://semver.org/). Releases are cut with `scripts/release.sh`; the section for
a version becomes the text of its GitHub release.

## [0.2.1] - 2026-10-11

### Changed
- **Settings window is larger by default**: it opens at up to 1100 by 820 pixels, within the screen's work area, so the tabs
  show their controls with less scrolling.
- **Moonlight copies keep their place**: each laptop's copies live in a folder named by the laptop's id, which stays the same
  when the desktop pairs again. Windows asks the firewall question once per program path, so the question is no longer asked
  again for every pairing. Existing installs ask once more after this update.
- **Forget** removes only the Moonlight pairing data; the Moonlight copies stay.

### Fixed
- A failed action (such as Start displays for a desktop that is no longer paired) shows its message in a box. The message
  used to be cleared at once, so it was never seen.
- The release build builds ggwave and ships `ggwave.dll` with the app (the v0.2.0 build failed its tests without it).

## [0.2.0] - 2026-10-11

### Added
- **Pairing from the settings window**: *Add host…* takes a laptop's address and control port, checks it, shows its name
  and certificate fingerprint, and pairs with the PIN. Saved hosts keep their address, token, streams and monitor choices.
- **Handshake at the start of every connection**: the desktop asks for each display at the size of the monitor it goes to,
  and the laptop agrees to a size and returns the stream port. The result is saved.
- **Quick reconnect**: after pairing, *Start displays* connects with the saved token; no PIN is needed.
- **Display choices per desktop** (Host tab): for each paired desktop, turn each virtual display on or off, and set the
  largest size (720p to 4K) and frame rate (30 or 60) it may use. The choices are saved with the desktop and apply at its
  next connection; the last size agreed with it is shown next to them.
- **Control port on the laptop**: HTTPS, chosen automatically from 49900 (kept after that), with its certificate fingerprint
  shown on the Host tab. The firewall step now also allows `LANDock.exe`.
- Command line: `client check`, `client add-host`, `client hosts`, `client assign`, `client remove-host`; `--host` on the other client commands.

- **Finding laptops**: LAN discovery over mDNS (DNS-SD `_landock._tcp`, Makaretu.Dns, MIT). The laptop advertises its name,
  control port and pairing state, and what it heard; the desktop lists the laptops it finds.
- **Ultrasound identification** with [ggwave](https://github.com/ggerganov/ggwave) (MIT, built from a pinned commit by
  `scripts/build-ggwave.sh`): each desktop has an id, plays it as ultrasound when *Let laptops hear this PC by sound* is on,
  and the laptop that hears it reports it (it stays off until *Listen on the microphone* is turned on).
- **Microphone bars**: the laptop's Host tab shows the level of the microphone in the ultrasound band (15 to 19.5 kHz),
  one bar per frequency, so you can see whether the sound is picked up.
- **Ordered list of laptops** (Client tab): nearby (heard this PC) first, then recency, favorites, and the order set with
  Move up / Move down; auto-connect to the first paired laptop that is online (setting).
- `LANDock ultrasound test [seconds] [Hz]`: plays a tone or this PC's id and prints the microphone and speaker levels.
- **Audible mode** (the default): the desktop id is a short ggwave chirp (1.9 to 6.4 kHz) that everyone hears, announced
  every 45 to 60 s. **Ultrasound mode** (15 to 19.5 kHz, inaudible) stays available: General tab > Sound > Mode.
- **Pets**: the audible chirp is loud and ultrasound is heard by dogs and cats. Both sound features are off by default; keep
  them off near pets.
- **Sound devices**: General tab > Sound: pick the microphone (laptop) and the speakers (desktop), or leave them on
  automatic (which avoids virtual devices). The choices are saved in the settings file and applied at once.

### Changed
- The desktop's settings hold a list of hosts instead of one laptop address. Settings written by 0.1.0 are not migrated:
  add the laptop again.

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
