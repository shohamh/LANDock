# LANDock

**Use your laptop's virtual monitors on your desktop PC, over your home network.**

LANDock makes a Windows laptop (the *host*) create extra virtual monitors and stream each one over the local network
to a Windows desktop PC (the *client*), where each stream fills one of the desktop's real monitors. Your desktop's
mouse and keyboard then drive the laptop as if the screens were attached to it: one extended workspace, no internet
needed.

![How LANDock fits together](docs/images/architecture.svg)

> **Status: experimental (v0.3).** Tested on one PC with the host and the client on the same machine, with virtual screens
> (arrangements above and below, landscape and portrait, screens added and removed while connected). Not yet tested with a
> second physical monitor or a second PC. Please open an issue with what you find.

## What you get

- Up to **six** virtual displays. They copy the **arrangement of your desktop's screens** (screens above or below, landscape or
  portrait), so the laptop's screens sit where your desktop's do. A screen you plug in or unplug while connected gets its display
  added or removed.
- **Seamless pointer and keyboard**: move the mouse off your desktop's monitor onto a stream and it keeps moving
  across the laptop's displays; typing goes to the window under focus.
- A **tray icon and settings window** for everything, plus a command line.
- **Automatic updates** (checked every few minutes; installed after you agree, or automatically if you choose), where
  only the parts that changed are downloaded.
- A **Windows installer** that is self-contained: it includes the virtual display driver (SudoVDA). It asks for administrator
  permission once, to trust the driver's certificate and install the driver.

## Install

You need two Windows 10/11 PCs on the same network (the laptop, which hosts the displays, and the desktop PC, which shows them).

### 1. Download the installer

Go to the **[releases page](https://github.com/shohamh/LANDock/releases/latest)** and download **`LANDock-Setup-<version>.exe`**
from the latest release (under *Assets*). Each release also lists `SHA256SUMS.txt`, which holds the checksum of the installer
if you want to check the download.

Direct link to the current release's installer: [LANDock-Setup-0.3.0.exe](https://github.com/shohamh/LANDock/releases/download/v0.3.0/LANDock-Setup-0.3.0.exe)

### 2. Install on both PCs

Run the installer on **both** PCs. The programs install for your user only. Windows asks for administrator permission once for
the parts that need it:

| On the laptop (host) | On the desktop PC (client) |
|---|---|
| Choose *Laptop only* (or both). Windows asks for permission once to install the virtual display driver (SudoVDA, included), and once more for the firewall option. | Choose *Desktop PC only* (or both). Tick the firewall option (Windows asks for permission once). |

The installer and the programs are not code-signed yet, so Windows SmartScreen may show a warning the first time
("More info" > "Run anyway").

### 3. Allow the connection through the firewall

The installer's firewall option allows the programs LANDock uses on *private* networks (your home network), with one
administrator prompt from Windows per PC:

- On the laptop, it allows LANDock and the host (Apollo).
- On the desktop PC, it allows Moonlight. All of the desktop's displays share one copy of Moonlight, so there is no
  question per display.

If you skipped the option, run the installer again and tick it. On the laptop, the Host tab's *Allow through Windows
Firewall…* button does the same.

## Use

1. **Laptop:** click the LANDock tray icon > *Start host*. The Host tab shows the **control port** (49900 unless it is taken), the **certificate fingerprint**, and a 4-digit **pairing PIN**.
2. **Desktop PC:** open the LANDock settings > *Client* > *Add host…*. Enter the laptop's address and the control port, press *Check host*, and compare the fingerprint with the one on the laptop's Host tab. Enter the PIN and press *Pair*.
3. Press *Start displays*. Each of your desktop's screens gets a display; the desktop tells the laptop the screens and their
   arrangement, and the laptop and the desktop agree on each display's size first (the size of its screen, at most 4K).
   Then the windows appear. To use a different screen for a display, change it in the Client tab's table.

Next time, just press *Start displays*: the desktop reconnects with the token it saved, so no PIN is needed. A new PIN is only needed if the laptop forgot this PC (remove it from the list on the laptop's side) or the laptop's certificate changed (LANDock refuses to connect and says so).

New desktop PCs can only pair while the pairing window is open (10 minutes after the host starts, or after *New pairing PIN*). Paired desktops are fully trusted: they control the laptop.

Command line (for scripts): `LANDock host up`, `LANDock client check 10.0.0.20`, `LANDock client add-host 10.0.0.20 --pin 1234 --trust`, `LANDock client start --host NAME`, `LANDock client status`, `LANDock update check`. Run `LANDock help` for the list.

## Updates

The app asks GitHub for the newest release every 10 minutes (Updates tab: interval, ask or install automatically,
pre-releases). Each release lists a version and checksum for three parts (the app, the host, the client); only the parts
that changed are downloaded and installed, and host and client parts wait until nothing is streaming when
installing automatically. A release is only offered after its checksum matched.

No access token is needed: the repository and its releases are public. (The Updates tab has an optional token field for a private copy of the repository.)

## How it works

See [docs/how-it-works.md](docs/how-it-works.md): why there is one Apollo per display, how the pointer lines up, how
pairing is kept safe, how the updater and installer work. In short:

- **Host** = [Apollo](https://github.com/ClassicOldSong/Apollo) (a Sunshine fork that creates a virtual display per
  client through the SudoVDA driver). LANDock runs **three instances** (one per display) and supervises, pairs and
  arranges them.
- **Client** = [Moonlight](https://github.com/moonlight-stream/moonlight-qt), one window per stream, each placed on a
  monitor and switched to fullscreen.
- **The LANDock app** glues them together and takes care of installing, configuring and updating.

## Build from source

[docs/building.md](docs/building.md). CI builds everything from source on every release: the app, Apollo and Moonlight
from our **forks** ([shohamh/Apollo](https://github.com/shohamh/Apollo),
[shohamh/moonlight-qt](https://github.com/shohamh/moonlight-qt)), which we keep in step with upstream
([docs/upstream-sync.md](docs/upstream-sync.md)).

## Credits

LANDock is mostly glue around other people's excellent work: **Apollo** and **Sunshine** (host), **Moonlight** (client),
**SudoVDA** (virtual display driver), Boost, MSYS2, .NET, Inno Setup, and the ideas of several community projects.
Full list with licences: [NOTICE.md](NOTICE.md). Implementation and testing were done pair-programming style with
Claude Code.

## Licence

[GPL-3.0](LICENSE), the licence of Apollo, Sunshine and Moonlight, which LANDock builds on and ships.
