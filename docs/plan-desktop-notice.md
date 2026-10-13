# Plan: desktop notice on the laptop

A small window on the laptop that appears when a desktop PC is recognized and can be connected to. It must not steal focus or
interrupt: it sits above the taskbar, in the corner where the clock is, and offers Connect, Dismiss and Snooze (10 min).

## When it appears

A desktop is "recognized" when the laptop learns that a known (paired) desktop is nearby, by either route:

- **Ultrasound**: the laptop's listener (`UltraListener`, `Heard`) hears the id of a paired desktop's announcer
  (`UltraAnnouncer`). Already used for "Heard" in the Host tab.
- **LAN**: a paired desktop reaches the laptop's control port (`ControlServer`), or its advert on `_landock._tcp` shows up
  (`LanAdvertiser`, `Advert.Heard`).

The notice is for a paired desktop that is **not connected yet** (no displays running for it). A desktop that is already
streaming does not get a notice. Unpaired desktops do not get one: pairing stays the explicit step with the PIN.

Open question (decide before building): the first version can show the notice only for paired desktops (the simple,
safe choice, recommended). Showing unpaired desktops would need a "pair now" action too.

## What the notice shows

- The desktop's name (its `Name` from the advert or the paired record).
- A **monitor diagram**: one rectangle per screen of the desktop, from the screens the desktop reports (`ScreenReport`:
  position, size in pixels, rotation, primary). Rules:
  - Scale: one common factor for all screens, chosen so the whole arrangement fits the notice. Sizes come from each screen's
    resolution, so a 1920x1080 screen is drawn larger than a 1280x1024 one by the same ratio Windows uses.
  - Orientation: portrait screens drawn tall.
  - Positions: the diagram keeps the desktop's relative positions (above, below, left, right), the same arrangement the
    laptop will use.
  - The primary screen gets a marker. Each rectangle shows its resolution in small text inside it (for example
    `1920x1080`), so the screen's size is readable without a legend.
- Buttons: **Connect**, **Dismiss**, **Snooze 10 min**.

## Where and how it shows

- Position: bottom edge of the **work area** (`Screen.WorkingArea`, which excludes the taskbar), so the window sits right
  above the taskbar. Horizontal: right side by default; **left side when the Windows display language is right-to-left**
  (`CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft`), because the taskbar's clock is on the left then. The user's
  Windows language is Hebrew, so this matters here.
- Multi-monitor: on the primary screen (where the taskbar with the clock is by default).
- **Non-intrusive**: a borderless, toolwindow-style form (`ShowInTaskbar = false`, no title bar), shown with
  `ShowWithoutActivation` (`WS_EX_NOACTIVATE`), so it never takes keyboard focus or the foreground. Not modal, no sound
  (sound must stay OFF per the project's rules for tests). It fades in and does not repeat while it is already showing.
  Topmost only among normal windows, not above full-screen apps.
- Auto-hide: if ignored, it fades out after about 30 s and comes back only when the desktop is recognized again (not on a
  timer loop).

## Button behavior

- **Connect**: does both halves of a connection:
  1. On the laptop, starts the virtual monitors for this desktop (the same as the host's ensure step: the displays' Apollo
     instances and their virtual screens).
  2. On the desktop, starts the Moonlight screen-sharing windows, one per display, placed as configured. This needs a new
     control message from the laptop to the desktop (`StartDisplays`), because today only the desktop starts its windows.
     The desktop's tray app must be running and paired; if it is not reachable, the notice says so and stays.
- **Dismiss**: hides the notice for this desktop until it goes away and comes back (it is recognized again after a gap of
  at least 10 minutes out of range).
- **Snooze 10 min**: hides it for 10 minutes, then shows it again if the desktop is still recognized.

## Implementation pieces

1. `Ui/DesktopNotice.cs`: the form (WinForms), with the diagram drawn in `OnPaint` from the desktop's screens.
2. `Ui/NoticePlacement.cs`: pure math (work area, side from language, scale, diagram layout). Unit-testable.
3. `Host/NoticeService.cs`: decides when to show: subscribes to heard-ultrasound and LAN events for paired desktops, tracks
   dismiss/snooze state per desktop, and calls the form on the UI thread.
4. Control message `StartDisplays` (laptop → desktop) on the existing control channel, with a check that the desktop is
   paired. The desktop side handles it like its own "Start displays" button.
5. Settings: a toggle "Show a notice when a desktop is recognized" (default on), and "Snooze length" (default 10 min).

## Tests

- Unit tests: placement (taskbar side for LTR and RTL, DPI), diagram layout (portrait/landscape, above/below/left/right,
  mixed resolutions), dismiss/snooze timing (with a fake clock).
- Runtime: run the notice on this PC with the test desktop (loopback) and take a screenshot to check size, placement and
  that focus stays on the current window (the foreground window is unchanged before and after).
- No sound. Nothing in the test may change firewall rules.

## Steps (in order, each one testable on its own)

1. Placement and diagram math with unit tests (no UI yet).
2. The form, shown from a test button in the Host tab. Screenshot check.
3. Wire the notice to the ultrasound event for paired desktops. Test with `tests` ultrasound scripts.
4. Wire to LAN / control-port recognition. Test on loopback.
5. Dismiss and Snooze logic with the fake clock tests.
6. Connect: the `StartDisplays` control message and the desktop side. Test end to end on loopback.
7. Settings toggle, README and CHANGELOG entry.

## Decisions (confirmed)

1. Only paired desktops get a notice.
2. Connect starts the laptop's virtual monitors and the desktop's Moonlight windows.
3. Each screen in the diagram shows its resolution in small text inside it.

## Status

- Done: placement and diagram math (`Ui/NoticeLayout.cs`, tested), the decision logic with dismiss, snooze and "once per arrival"
  (`Host/NoticeTracker.cs`, tested), the window (`Ui/DesktopNotice.cs`: not activated, tool window, topmost, above the taskbar,
  name, diagram with resolutions inside each screen, Connect / Snooze / Dismiss, goes away after 30 s when ignored), the LAN
  presence of a desktop (`Discovery/Presence.cs`), the laptop's notice service (`Host/NoticeService.cs`), the start request
  (advert `start` entries, `Discovery/Lan.cs`, `HostService.RequestStart`), the desktop's start on request
  (`AppServices.HandleStartRequestsAsync`), the Host tab checkbox, and the setting `Host.ShowNotices` (default on).
- Verified on this PC: the LAN path end to end (`tests/run-notice.sh`: the desktop is recognized over LAN and the notice
  window is created, topmost, not activated, above the taskbar, with the name and the diagram). The window's own picture was
  checked; a full-screen capture did not show topmost windows in this environment, so the on-screen look was not seen directly.
- Connect verified on this PC against the real data folder: the notice's Connect button (pressed without moving the mouse)
  started the laptop's virtual monitors, the desktop received the request, and its Moonlight window showed the laptop's virtual
  display (the stream works).
- Ultrasound: the source check is unit-tested (`NoticeService.SourcesOf`); the sound path itself is not tested (sound stays off).
- Known: Connect starts every virtual monitor the laptop offers this desktop (the Desktops tab), not only the ones the desktop
  has enabled. The laptop does not know the desktop's enabled list yet; a follow-up would send it with the start request.

## Layout of the laptop's screens (later work)

- Done: one virtual display per connected screen of the desktop (automatic on the desktop; Connect on the laptop starts exactly those);
  the laptop's screen placement (left, right, below-centred, below-right, below-left) in the Host tab, and per desktop in the Desktop
  PCs section; the copy of the laptop's screen onto the desktop's primary or largest screen; the display setup restored when the
  displays are gone. Tested live on this PC with one screen (placement override, copy, disconnect and restore).
- Not tested live: a desktop with more than one screen (the arrangement is unit tested with two screens).
- Known: a copy-only connection does not count as connected, so its notice can come back after a minute.
- Known: a copy takes effect at the next connection (the stream of that screen is not changed while it runs).
