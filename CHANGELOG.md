# Changelog

All notable changes to LANDock. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the
project uses [Semantic Versioning](https://semver.org/). Releases are cut with `scripts/release.sh`; the section for
a version becomes the text of its GitHub release.

## [Unreleased]

## [0.4.1] - 2026-10-13

### Changed
- **"Copy" is now "Duplicate" in the settings.** The laptop's general setting and each desktop's own setting read "Duplicate the
  laptop screen onto", and "No copy" is "No duplicate".
- **The below placements name the desktop's screen.** "Laptop below the rightmost screen" is now "Laptop below the desktop's
  rightmost screen", and the same for the leftmost screen.

## [0.4.0] - 2026-10-13

### Added
- **A notice when a paired desktop is nearby.** A small window above the taskbar (on the left for right-to-left languages)
  shows the desktop's name and a diagram of its screens (positions, sizes, resolutions). It appears when the laptop hears the
  desktop's ultrasound or sees it on the LAN, and only for paired desktops that are not connected. It never takes the focus.
  **Connect** starts the laptop's virtual monitors and asks the desktop to start its Moonlight windows; **Snooze** hides it for
  10 minutes; **Dismiss** hides it until the desktop has been away. Turn it off in the Host tab.

- **One connection per connected screen.** The desktop turns on a display for each screen it has connected (automatic; a
  setting in the Client tab turns it off). Connect on the notice starts exactly as many virtual displays on the laptop as the
  desktop has screens, and the desktop opens one connection for each.
- **Where the laptop's screen sits.** The laptop's screen can be left, right, below and centred, below the rightmost screen, or
  below the leftmost screen, relative to the desktop's screens. The laptop's screen stays where Windows keeps the primary
  screen; the virtual displays move. Set it in the Host tab; a desktop can have its own choice in the Desktop PCs section.
- **A copy of the laptop's screen on a desktop screen.** The desktop's primary or largest screen can show the laptop's own screen
  (a stream of the laptop's screen instead of a virtual display). Windows cannot copy a screen across two graphics adapters, so
  the copy is made by the stream. Changes to the copy take effect at the next connection.
- **The display setup comes back.** The laptop's display setup from before the first connection is put back when its displays
  are gone (after the desktop has stopped for about a minute).

### Fixed
- **A display's Moonlight no longer shows its laptop as offline after a host restart.** The laptop's Apollo instances now keep a
  fixed identity from their first start (before, it was made new at each start until the first pairing).
- **Restarting the host right after stopping it** no longer fails with "already running": `host down` waits until the host has
  stopped.
- **Pairing no longer fails while the laptop starts its displays.** The desktop waited 10 seconds for the laptop to start all its
  virtual monitors; that call now has 120 seconds.
- **`host down` stops a host that runs in the tray.** Before, it only stopped a host started from the command line, and it
  reported "stopped" anyway.
- **Updates only go to a higher version.** The host and client parts are offered only with a newer release, so an older release
  never replaces a newer install.
- **A portrait virtual display stays portrait when the laptop moves the displays.** Moving a display's position reset its
  orientation to landscape, and the layout watcher turned it back every few seconds (`vddtool layout` now keeps the orientation).
- **A below placement puts the desktop's screen over the laptop on the laptop's top edge.** Before, a shorter screen could float
  above the laptop with a gap, and Windows kept its display where it was. The desktop's screens keep their arrangement.
- **A display for a screen of another size is made again.** A display the driver keeps was reused with the size of its first stream,
  so a 1280x1024 screen could get a 1920x1080 display. When an instance's size changes, it restarts and its display is removed first.
- **Display changes of the instances take turns.** Each instance changes the PC's display configuration by reading all of it and
  writing all of it back. Two instances doing that at once could put an old mode back (the 1280x1024 screen above). Apollo's
  display change and `vddtool layout` now wait for each other (a named mutex in the user's session). The Apollo fork's `landock`
  branch has the change (commit `bffd7ba`).
- **A display for a portrait desktop screen is turned to portrait** when Apollo brings it up landscape.
- **A desktop's copy stream keeps the desktop "connected"** while it is open: the desktop sends a heartbeat every few seconds, so
  the notice no longer comes back for a copy-only desktop.
- **The host's `vddtool` helper ends with the host.** Its process is in a job object, so a killed host does not leave it running
  and blocking the installer.

### Changed
- **One Moonlight for all displays on the desktop PC.** Every display used to have its own copy of Moonlight, and Windows
  asked about each copy's firewall access (up to six questions, again after every test run). Now all displays share one
  copy in `%LOCALAPPDATA%\LANDock\moonlight`, and each display keeps its own pairing and settings in it.
- **The firewall rules are made at install time.** The installer's firewall option allows the host (Apollo and LANDock) on
  the laptop and Moonlight on the desktop PC, with one administrator prompt. Uninstalling removes them.

## [0.3.0] - 2026-10-11

Experimental: the monitor layout was built and tested on one PC with virtual screens only. A second physical monitor, a
portrait monitor and the mouse on a second PC have not been tested yet.

### Added
- **The laptop mirrors the desktop's screens.** The desktop reports its screens (position, size, rotation, which one is
  primary) at each connection. Each display goes where the screen it shows is, so screens above or below, landscape and
  portrait mix as in the Windows display settings. Older desktops keep the three fixed displays.
- **Up to six displays** (M1, M2, TV, M4, M5, M6). A display's Apollo instance starts when a desktop asks for it.
- **Live screens.** A screen plugged in while connected gets a display (its own, or the first free one) and starts. A
  screen that goes away has its display stopped after 30 seconds, and it starts again when the screen comes back. A
  screen whose size changes gets its display restarted. Moves are reported to the laptop at once. `client follow` follows
  the screens from the command line.
- **Display choices per screen.** The Host tab's grid lists each screen the desktop reported (also while it is offline).
  Choices saved by older versions still apply.
- **Screens are identified by their monitor device id**, which stays with the monitor when Windows renames its display.

### Changed
- Pairing covers all six displays, so a screen added later needs no PIN. The first pairing starts six Moonlight copies;
  Windows may ask about the firewall once for each copy.
- Pairing runs the handshake first, so the displays start before they are paired. A display that is not running is
  reported at once instead of after minutes of retries.
- `client assign` also turns the display on when it gives it a screen (`--off` keeps it off).

### Known limitations
- A screen that no display shows leaves a gap in the arrangement; Windows closes gaps, so the displays end up touching but
  not where the desktop has them.
- On one PC (where the laptop's displays are also the desktop's screens), removing a display makes the laptop re-lay the
  displays for about 40 seconds. Not seen where the screens are separate machines.
- The mouse is not tested end to end. The mapping of desktop pixels to laptop pixels is checked by calculation.

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
