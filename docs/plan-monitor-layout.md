# Plan: the laptop mirrors the desktop's screens

Status: decisions taken; steps 1 to 3 partly built (see Progress). Written 2026-10-11, updated the same day.

## Progress

| Step | State |
|---|---|
| 0. Spikes | Partly run on this PC (one screen, loopback). Spike 3 in part: three virtual displays were placed side by side (x = 1920, 3840, 5760) with no overlap. Not run: spike 1 (portrait screen size; needs a physical portrait monitor), spike 2 (portrait virtual display), spike 4 (how many instances the machine handles), spike 5 (time to add an instance while others stream), spike 6 (removing one display while another streams). |
| 1. Protocol | Built and tested. `SessionRequest.Monitors` (`MonitorOffer`: id, position, size, rotation, primary). Each stream also names its screen (`StreamOffer.ScreenId`). The desktop reads each screen's rotation from `EnumDisplaySettings` and sends the screens its streams use. The laptop stores the latest report (`HostService.ScreensOf`). An old desktop sends none and keeps the fixed row. Runtime check: a desktop with one screen reported `\\.\DISPLAY1 1920x1080 at 0,0 rotation 0` and the laptop agreed all three displays. |
| 2. Laptop plan | Pure logic built and tested: `HostPlanning.PlanDisplays` (up to 6, instance keys c to h, ports 49989 + 1000 × k, a screen keeps its instance), `HostPlanning.Diff` (added, removed, resized). Not yet: the supervisor starts only the fixed three instances; instances for the planned slots 3 to 5 are not started, and removing a screen while connected does not stop its instance (live changes, step 6). |
| 3. Layout | Built and tested. `HostPlanning.PlanMirrorLayout` moves each active display to the place of the desktop screen it shows (using `MirrorPositions`); two displays on one screen go side by side. The layout watcher uses it when a desktop has reported screens, and the fixed row otherwise. Not tested on two different desktop screens yet (needs a second monitor). |
| 4. Policy | Not started. |
| 5. Desktop streams | Not started. |
| 6. Live changes | Not started. |
| 7. End to end | Not started. |

Tests: `MonitorOfferTests` (protocol, screen ids, rotation) and `MirrorPlanTests` (plan, reuse, diff, layout, shared screens). 93 unit tests pass. E2E settings test: passed on the second attempt; the first attempt failed at the Pair dialog (the same intermittent step as before).

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
