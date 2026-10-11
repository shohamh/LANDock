# Plan: the laptop mirrors the desktop's screens

Status: decisions taken; steps 1 to 3 partly built (see Progress). Written 2026-10-11, updated the same day.

## Progress

| Step | State |
|---|---|
| 0. Spikes | Partly run on this PC (one screen, loopback). Spike 3 in part: three virtual displays were placed side by side (x = 1920, 3840, 5760) with no overlap. Not run: spike 1 (portrait screen size; needs a physical portrait monitor), spike 2 (portrait virtual display), spike 4 (how many instances the machine handles), spike 5 (time to add an instance while others stream), spike 6 (removing one display while another streams). |
| 1. Protocol | Built and tested. `SessionRequest.Monitors` (`MonitorOffer`: id, position, size, rotation, primary). Each stream also names its screen (`StreamOffer.ScreenId`). The desktop reads each screen's rotation from `EnumDisplaySettings` and sends the screens its streams use. The laptop stores the latest report (`HostService.ScreensOf`). An old desktop sends none and keeps the fixed row. Runtime check: a desktop with one screen reported `\\.\DISPLAY1 1920x1080 at 0,0 rotation 0` and the laptop agreed all three displays. |
| 2. Laptop plan | Pure logic built and tested: `HostPlanning.PlanDisplays` (up to 6, instance keys c to h, ports 49989 + 1000 × k, a screen keeps its instance), `HostPlanning.Diff` (added, removed, resized). Not yet: the supervisor starts only the fixed three instances; instances for the planned slots 3 to 5 are not started, and removing a screen while connected does not stop its instance (live changes, step 6). |
| 3. Layout | Built and tested. `HostPlanning.PlanMirrorLayout` moves each active display to the place of the desktop screen it shows (using `MirrorPositions`); two displays on one screen go side by side. The layout watcher uses it when a desktop has reported screens, and the fixed row otherwise. The desktop now reports every screen set up for the laptop, not only the ones started. Two-screen runtime test on this PC, with a virtual second monitor: M1 started on the real screen (DISPLAY1), its virtual display became a second screen (DISPLAY6, x = 1920); M2 was set to that screen and started; the desktop reported both screens; M2's display went to x = 3840, the place of the screen it shows, to the right of the real screen's mirror. A vertical arrangement (a screen above another) is not tested: on this PC the second screen is the laptop's own display, so moving it would fight the layout watcher. |
| 2b. Instances on demand | Built and tested on this PC. The laptop has six slots (M1, M2, TV, M4, M5, M6; ports 49989 to 54989). The first three start with the host; the others start when a desktop's handshake asks for them (`HostService.EnsureDisplaysAsync`), and stay up until the host stops. The handshake reports a failed start instead of agreeing to a display that is not running. |
| 4. Policy | Built and tested. The choices are keyed by the desktop's screen (its monitor id); the Host tab lists the screens the desktop reported (kept with the paired desktop, so they show while it is offline) and shows what was agreed for each. Choices saved by older versions (keyed by M1, M2, TV) still apply when no screen entry exists. Not checked on screen; covered by unit tests. |
| 5. Desktop streams | Built and tested on this PC. The laptop offers six displays; the desktop enables one per screen it has (the first three at pairing, more when it gets screens). `client assign` turns a display on when it gives it a screen (`--off` keeps it off). A display's Moonlight copy is made before its pairing check. A display turned on after the pairing needs its own pairing: `client pair --pin` pairs the enabled displays. |
| 6. Live changes | Built and tested on this PC (`tests/run-follow-screens.sh`). The desktop follows its screens (`client follow`, or started with the displays): every 2 seconds it compares the screens. A new screen gets the display it had before, or the first free display, and is started. A screen that is gone has its display stopped after a 30-second grace period, and its display is off until the screen returns. A screen whose size changed has its display restarted. Each change of screens is reported to the laptop at once (the laptop lays out the displays by them). A move of a screen changes no display but is reported at once, so the laptop lays out its displays again. Pairing now covers all six displays, so a new screen needs no PIN. |
| 7. End to end | Not started. |

Four-display runtime test on this PC (`tests/run-mirror-displays.sh`): M1, M2, TV and M4 each on its own screen; the screens are virtual monitors that the previous display created (DISPLAY1, 6, 7, 8, 9). The desktop reported four screens and the four displays sit side by side at x = 1920, 3840, 5760 and 7680. M4's instance started on demand.

Resolved after the first runtime test:
- Pairing a display turned on later took about 4 minutes: the pairing waited for a display whose instance was not running. Now pairing runs the handshake first (which starts the displays the PC uses), and a display whose port does not answer is reported at once. The runtime test pairs the new display in about 40 seconds.
- The E2E settings test failed intermittently. Causes found and fixed: the Pair… click went through UI Automation, whose Invoke call stayed open while the dialog was up (the window then could not be read); the PIN box was picked by tree order; the log was read in a mode the app was writing to; the test folder reset failed when a shell was inside it. The test passed three runs in a row after the fixes.
- Arrangements (`tests/run-arrangement.sh`, on this PC, the desktop report sent by hand): a landscape screen above, a landscape screen beside it, and a portrait screen beside that: each display landed where the desktop arrangement puts it, the portrait one included. Continuity across the edges was checked by arithmetic (a pointer crossing an edge on the desktop lands on the adjacent pixel on the laptop); the pointer itself was not driven, because on this PC the laptop and the desktop share one mouse.
- An arrangement with a screen that has no display in it (for example the desktop's primary, when only other screens are used) leaves a gap; Windows closes gaps, so the displays end up touching but not where the desktop has them. Fix: the desktop reports all its screens, so the laptop can see the gap. Not done yet.
- Live follow on this PC: screens are identified by their monitor device id (it stays with the monitor; Windows reuses display names). The laptop identifies its own displays the same way. Fixed: a screen was reported as added at every change, and a display was assigned to the wrong screen. Still open: when a display is removed on this PC, the laptop re-lays the displays a few times (about 15 corrections) before they settle, about 40 seconds. The cause is that the laptop's displays are also the desktop's screens here (a loopback effect; on real screens there is no feedback). A guard that stops the laptop mirroring its own displays would end it.

Tests: `MonitorOfferTests` (protocol, screen ids, rotation), `MirrorPlanTests` (plan, reuse, diff, layout, shared screens, six slots). 94 unit tests pass. E2E settings test: passed on the second attempt; the first attempt failed at the Pair dialog (the same intermittent step as before).

## Goal

When a desktop PC connects and extends its screens to the laptop, the desktop tells the laptop which screens it has and how
they are arranged (count, size, orientation, position, primary). The laptop then creates one virtual display per desktop
screen, at the same size and orientation, and places them with the same relative arrangement. Monitors added or removed on
the desktop while connected are added or removed on the laptop too. Today the laptop always has three displays (M1, M2, TV)
in a fixed row, whatever the desktop has.

## Decisions

| Question | Decision |
|---|---|
| Maximum number of screens | 6 |
| Default behaviour | Mirror the desktop's screens. The fixed M1/M2/TV row becomes the option "Keep the three fixed displays". |
| Which monitors | Only the monitors the user has chosen to extend to on the desktop (not every monitor the desktop has) |
| Monitors added or removed while connected | Wanted: follow them live. Fallback if too hard: the change is picked up at the next connection. See "Live changes". |

## What exists today

- **Handshake** (`SessionRequest` / `StreamOffer` in `app/LANDock/Host/ControlServer.cs`): the desktop sends one offer per
  stream: `Name`, `Width`, `Height`, `Fps`. The width and height are those of the monitor the stream is shown on
  (`app/LANDock/Client/ClientService.cs`, handshake). The laptop replies with an agreed size and port per stream
  (`HostService.Agree`), capped by the per-desktop display policy.
- **Displays on the laptop**: a fixed list, `HostPlanning.Instances` = M1 (port 49989), M2 (50989), TV (51989). Each is one
  Apollo instance, and Apollo creates one virtual display per connected client. `WatchLayout` keeps the displays in the row
  laptop | M1 | M2 | TV, using `vddtool layout <DISPLAYn>=<x>,<y>` (`tools/vddtool.cpp`).
- **Display policy** (`app/LANDock/Host/DisplayPolicy.cs`): per desktop, on/off, largest size and frame rate per display name
  (M1, M2, TV).
- **Desktop side**: each stream has a chosen monitor; Moonlight windows are moved onto it after the video starts.
- **Not handled today**: the desktop's full screen list, its arrangement, orientation other than through the size, more than
  three screens, and any change while connected. The 0.1.0 notes list rotated (portrait) displays as untested.

## What the handshake must carry

A new `Monitors` list, alongside the streams. Old desktops send none and keep today's behaviour.

| Field | Source on the desktop | Why |
|---|---|---|
| `Id` | stable id of the monitor (device name or EDID-based id) | match the monitor's policy and stream across connections |
| `X`, `Y` | `Screen.Bounds` (or `DISPLAYCONFIG` origin) | relative arrangement |
| `Width`, `Height` | `Screen.Bounds` | size; a rotated monitor already reports its swapped width and height |
| `Rotation` | `DISPLAYCONFIG` / `EnumDisplaySettings` `dmDisplayOrientation` | to check orientation explicitly, not only infer it from the size |
| `Primary` | `Screen.Primary` | where the laptop's own screen goes relative to it |

Open point, to verify first (spike 1): whether `Width` and `Height` already come out swapped for a portrait monitor. If they
do, Apollo's virtual display gets the portrait mode from the size alone, as the Apollo FAQ suggests, and the rotation field is
only a check.

## Design

### Laptop: one display per desktop monitor

1. The laptop's display set comes from the session, not from a constant. `HostPlanning.Instances` becomes a per-desktop plan:
   one entry per monitor, with an Apollo instance key, a port and a display name.
2. Ports: a fixed scheme, base 49989 + 1000 × k for instance k, up to 6 displays (k = 0..5). Port use is checked before a plan
   is accepted.
3. Instances are started and stopped as the desktop's plan changes. Today the three instances are started once with the host.
   The supervisor must start them per desktop plan.
4. Layout: `WatchLayout` places each display at the laptop-relative position computed from the desktop's arrangement. The
   laptop's own screen keeps its place. The desktop's arrangement is shifted so that its leftmost screen starts right of the
   laptop's screen, keeping its relative positions.
5. Display policy: keyed by the monitor `Id` (the desktop's own monitor), not M1/M2/TV. Saved M1/M2/TV policies are migrated
   to the monitors streamed to those names, or left at the defaults.

### Desktop: one stream per monitor

6. The desktop's streams are created from its monitor list (one per monitor the user extends to), not from the fixed
   M1/M2/TV names. Each stream keeps its own Moonlight copy folder, named by the monitor `Id`.
7. Placement code (`ClientService`, after the first frame) stays as it is: each window goes to the monitor it was offered for.

### Compatibility

8. The protocol gains an optional field. A desktop without it, or a laptop that does not understand it, behaves as today:
   the three fixed displays. The version in the advert tells the two apart before pairing.
9. "Keep the three fixed displays" stays available as a setting on the Host tab, per desktop.

## Live changes (monitors added or removed while connected)

The wanted behaviour: a monitor plugged into the desktop while connected gets a virtual display on the laptop; one unplugged
or un-extended is removed from the laptop. The parts:

**Detect (desktop).** The desktop watches display changes (`WM_DISPLAYCHANGE`, or a timer that compares the monitor list every
2 seconds, which is simpler and catches the same changes). A change produces a new monitor list.

**Tell the laptop.** Two options:
- *Push*: keep a connection open per desktop and send the new list over it. Needs a long-lived channel (WebSocket or SSE on the
  control port). More work in the control server, lower latency.
- *Pull* (proposed for the first version): the desktop sends the whole monitor list again to `POST /api/session` whenever it
  changes, and the laptop replies with the new plan. Reuses the handshake, no new channel. Latency is the desktop's check
  interval (2 s).

Proposal: pull, with the same request as the handshake. The laptop treats a second session request from the same desktop as an
update, not a new connection.

**Diff and apply (laptop).** The laptop compares the new plan with the running one:
- *added monitor*: start a new Apollo instance (ports and config from the plan), wait until it answers, reply with its port.
  The desktop then starts a Moonlight stream for it.
- *removed monitor*: the desktop stops that stream first, which ends the Apollo client and its virtual display. The laptop
  then stops the instance. Order matters: the stream must end before the virtual display is removed, or the removal is
  left to the orphan cleanup that already exists (`VddTool.Remove`).
- *changed size or position*: resize is a new agreement for that stream; position is a `vddtool layout` for the existing
  displays. No restart if only the position changes.
- The displays that did not change keep streaming. Nothing else restarts.

**Desktop side.** The desktop starts or stops the Moonlight process for each added or removed monitor. The existing per-stream
start (`ClientService.StartAsync(h, only)`) already starts one stream; removing one exists as `StopAsync` for a stream.

**Hard parts** (to test in the spikes):
- A new Apollo instance started while others stream: the time to start is a few seconds (today's start waits up to 10 s for
  each port). The desktop must not start its Moonlight window before the instance answers.
- Removing a virtual display that a stream still uses: the order above avoids it, but a crash during removal leaves an orphan.
  The orphan cleanup handles that already.
- Windows moves windows when a display disappears: the desktop's windows on a removed monitor move to another screen. The
  desktop's Moonlight window for that monitor is closed first, so nothing is moved by surprise.
- The laptop's own screen must not move when displays are added or removed (its place is fixed).
- A monitor that disappears and comes back within a few seconds should not restart its stream: the plan keeps a monitor's
  instance for 30 seconds after it leaves (a grace period), so a flicker does not cost a restart.

**Fallback** if live changes prove too hard in the spike: apply the plan only at connection time, and tell the user that
changes need a reconnect. The design above does not depend on the fallback.

## Work breakdown

| Step | What | Done when |
|---|---|---|
| 0. Spikes (this PC, two monitors, one portrait if available) | 1) Do `Screen.Bounds` sizes come out swapped for a portrait monitor? 2) Does an Apollo instance started with a portrait size (height > width) give a portrait virtual display, without rotation? 3) Can `vddtool layout` place 4 or more displays? 4) How many Apollo instances and virtual displays run at once before the machine struggles? 5) How long does a new instance take to answer while two others stream? 6) Does removing a virtual display while another instance streams disturb the other? | A short note in this file with the answers; the numbers (ports, grace period) updated |
| 1. Protocol | `Monitors` in the session request and reply; compatibility rule; unit tests for both old and new desktops | Unit tests: old and new desktops both handshake with the laptop |
| 2. Laptop plan | Per-desktop plan replaces the fixed list; port allocation; start and stop instances per plan | Unit tests for port allocation and plan diffs; the host starts 2 and 4 instances for a session |
| 3. Layout | Positions from the desktop arrangement; the laptop's screen fixed | Unit tests for placement (portrait, offset, primary on the left) |
| 4. Policy | Keyed by monitor id; migration of M1/M2/TV policies; the Host tab lists the desktop's monitors and their state | Unit tests for migration; the Host tab shows each monitor |
| 5. Desktop streams | One stream per monitor; folder per monitor id | E2E: a desktop with two screens gets two streams |
| 6. Live changes | Desktop detects changes and re-sends the list; laptop diffs and applies (add, remove, resize, move); grace period | E2E: plug a second screen in while connected; the laptop gets a display; remove it; the first stream keeps running |
| 7. End to end | Extended to the desktop's screens; a window opened on each, moved across, typed into (extend `tests/run-display-tests.sh` to the new display set) | The display test passes for 2 and 3 displays, including one portrait screen, and after a live add and remove |

## Risks

- Apollo's per-client virtual display identity (one display per client uuid) must hold for several instances on one laptop.
  Covered by spike 4 and 6.
- Port range and firewall: each new instance needs its ports allowed; the rule must cover the range, or be added per instance.
  This may bring back the firewall prompts (see the project memory on the firewall).
- Moonlight copies are per monitor, and the firewall answer is per program path: the first run of each new copy may prompt.
  A shared copy for all monitors would avoid that; the plan keeps one per monitor for now.
- Live changes add a state machine on both sides. Step 6 is the largest step; if the spike shows it is too hard, the
  fallback (apply at connection time) ships first and live changes follow.
