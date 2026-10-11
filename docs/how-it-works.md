# How LANDock works

## The problem

Moonlight/Sunshine-style streaming assumes **one stream = one session = one display**. LANDock needs the laptop to
behave like one desktop with several extra monitors, each shown on a different monitor of another PC. The virtual
display driver (SudoVDA) and the video pipeline can already do many displays; the limit is in the session logic of the
streaming software at both ends. See [design-study.md](design-study.md) for the research behind the choice below.

## The approach: one stream per display

```
 desktop PC                                    laptop
 ┌───────────────┐   stream 1 (port 49989)   ┌──────────────────────────┐
 │ Moonlight M1  │◄──────────────────────────│ Apollo instance C ─ VD 1 │
 │ Moonlight M2  │◄── stream 2 (port 50989) ─│ Apollo instance D ─ VD 2 │
 │ Moonlight TV  │◄── stream 3 (port 51989) ─│ Apollo instance E ─ VD 3 │
 └───────────────┘   mouse + keyboard up     └──────────────────────────┘
```

- The host runs **three Apollo instances** with their own ports, certificates and state. When a client connects to
  an instance, Apollo asks the SudoVDA driver for a **new virtual display** just for that client and streams it.
  (Apollo's own [multi-instance technique](https://github.com/ClassicOldSong/Apollo/wiki/How-to-start-multiple-instances-of-Apollo).)
- The client runs **three Moonlight windows**, one per instance, each paired separately (each instance is a separate
  "computer" to Moonlight, so every stream has its own copy of Moonlight with its own pairing data).
- All virtual displays live in one Windows desktop on the laptop, so windows can be dragged between them and the
  pointer moves between them.

## The pieces the app adds

| Piece | What it does |
|---|---|
| **Supervisor** | Starts the instances one after another (starting them together crashed one in testing) and restarts any that exits. |
| **Layout watcher** | Every few seconds it reads which display each instance created and moves the displays to *laptop \| M1 \| M2 \| TV*, tops aligned, whichever order the streams connected in (`vddtool layout`). |
| **Pairing** | See below. |
| **Orphan cleanup** | A virtual display outlives a killed instance (the driver keeps it alive while an Apollo service pings it). On start and stop the app removes the displays of every client it knows (`vddtool remove`). |
| **Keep awake** | A locked or sleeping laptop cannot receive input. While the host runs, it holds a "display required" request (`SetThreadExecutionState`); nothing about the system is changed permanently. |
| **Window placement** | Moonlight has no option for which monitor to use. After the first video frame arrives, the app moves the window onto the chosen monitor and switches it to fullscreen with Moonlight's own hotkey (Ctrl+Alt+Shift+X), and verifies that it covers the monitor. |

### The patch we carry for Apollo

Windows sometimes attaches a new virtual monitor as a **clone** of the main screen instead of extending the
desktop. [`patches/0001`](../patches/0001-virtual-display-extend-topology.patch) makes Apollo wait longer for the new
display and, if it came up as a clone, ask Windows for an extended topology. Without it a stream could show the laptop's
own screen again instead of a new monitor.

### Pointer and keyboard

Moonlight is started with *absolute mouse*. Each stream maps your pointer's position inside its window to the
matching position on its virtual display (Apollo's per-session touch-port: an offset plus the size of the whole
virtual desktop), and moving past the edge of a window continues on the next monitor, so the pointer is an exact
(±1 px in our tests) match across laptop and all displays. Keys go to the focused Moonlight window and are injected
into the laptop's session. Injection needs an **unlocked, awake, non-elevated** session: an elevated window in front, the
lock screen or UAC prompts block it (that is also why the host keeps the laptop awake).

## Pairing, the handshake and security

The laptop runs a small **control port** (HTTPS, 49900 by default, or the first free port after it) next to its Apollo
streams. Desktop PCs talk to it first, in three steps:

1. **Check.** `GET /api/identity` (no login) tells the desktop the laptop's host id, its name and the fingerprint of its
   certificate. The desktop shows the fingerprint; you compare it with the laptop's Host tab. The certificate is made
   once per laptop and never changes unless you reinstall.
2. **Pair.** `POST /api/pair` with the 4-digit PIN of the open pairing window returns a **token** for this desktop. The
   laptop keeps only a hash of the token and lists the desktop by name (*Paired desktops*). The desktop keeps the token
   encrypted for its Windows user. Moonlight then pairs each display with the same PIN, as before.
3. **Session.** Every start, `POST /api/session` with the token and the size of the monitor each display goes to. The
   laptop agrees to each size (at most 3840x2160, 30 or 60 frames per second) and returns the stream port for each
   display. The desktop saves what was agreed and starts Moonlight with it.

After pairing, reconnecting needs only the token: no PIN. Every connection checks the certificate against the
fingerprint saved at pairing, so a different laptop at the same address is refused, and a laptop whose certificate
changed is reported instead of silently trusted.

Other rules that still hold:

- Starting the host creates a **random 4-digit PIN** and a **pairing window** (10 minutes by default). Outside the window
  nobody new can pair, so a leaked PIN is useless later. Press *New pairing PIN* to open a new window.
- Paired desktops get full permissions (keyboard, mouse, clipboard...). **Treat a paired desktop as fully trusted.**
- Windows Firewall allows `hostin\sunshine.exe` (the streams) and `LANDock.exe` (the control port) on *private*
  networks only. Nothing is exposed to the internet; do not forward the ports.
- The app never asks for administrator rights. The one step that needs them (the firewall rules) is a separate prompt.

## Installer and updates

- The installer (Inno Setup, built in CI) installs per user into `%LOCALAPPDATA%\Programs\LANDock`:
  `LANDock.exe`, `host\` (Apollo, vddtool, patches, `SOURCE.md`) and `client\moonlight\`. Settings, keys, logs and the
  per-stream Moonlight copies live in `%LOCALAPPDATA%\LANDock`, so uninstalling keeps your pairing unless you say otherwise.
- Every release publishes `manifest.json`: for each part (`app`, `host`, `client`) a version id, the zip's file name and
  its SHA256. The app and the client/host ids are chosen so that they only change when that part's *content* changes (the
  pinned source commit), so an app-only release does not make anyone download Apollo again.
- The updater polls the GitHub releases API (ETag-cached, so quiet checks are cheap), reads the manifest, and plans:
  the app when the release is newer; host and client when their ids differ from the installed ones. It downloads each
  zip, **refuses it unless the SHA256 matches**, unpacks it next to the target, stops what uses it, swaps the folder in
  two renames (the old one is put back on failure), and restarts what was running. The app replaces its own executable
  by renaming the running one aside, putting the new one in place and restarting.
- In "ask" mode the tray shows a notice and the Updates tab an *Install* button; in "automatic" mode it installs by
  itself but waits until no stream is active.

## Where things are in the code

| Path | |
|---|---|
| `app/LANDock/Host/` | supervisor, pairing, layout, Apollo web API client, vddtool wrapper, firewall rule |
| `app/LANDock/Client/` | Moonlight copies, pairing, starting, window placement |
| `app/LANDock/Update/` | release check, manifest, planning, download and apply |
| `app/LANDock/Ui/` | tray icon and settings window |
| `app/LANDock.Tests/` | unit tests (the updater runs against a fake GitHub) |
| `tools/vddtool.cpp` | the small helper that talks to the SudoVDA driver |
| `patches/` | our changes to Apollo (exports of the commits in our fork) |
| `installer/LANDock.iss` | the Inno Setup script |
| `tests/` | end-to-end tests that drive the real desktop |
