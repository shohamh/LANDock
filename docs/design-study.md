# LANDock: architecture study and fork-vs-recompose plan

Goal (unchanged): a Windows laptop (host) gets N extra virtual monitors, streamed over a LAN with no internet to a Windows desktop (client) whose monitors show them. The desktop's keyboard and mouse control the whole laptop as one extended workspace.

Legend used below: **[V]** = found in a source I read (links at the bottom). **[?]** = my inference or something I could not verify; test it before relying on it. Sizes (S/M/L/XL) are relative effort, not calendar estimates.

---

## 0. Short answer

1. **Do not start with a fork or a rewrite.** The one thing that decides the whole design is cheap to test: can two stock Apollo instances create two virtual displays at once, and can two stock Moonlight windows drive them with one keyboard and mouse? (Phase 0, section 6.)
2. **The blocker is neither the virtual display driver nor the video codec.** The driver already supports many monitors, and the video pipeline is fine. The blocker is that each piece of the stack assumes **one stream = one session = one display**, at both ends (section 3).
3. **If Phase 0 fails in a way that needs source changes, I'd choose a lean Windows-only recompose over refactoring Apollo's host.** Apollo's maintainer says the inherited session logic is too tangled to give native multi-display support. A narrow rewrite that reuses the driver and OS APIs and uses Sunshine as a reference is the better bet than fighting that code (judgment, section 5).

---

## 1. What the stack is made of

### 1.1 Host: Apollo (a fork of Sunshine) [V]

| Module (file) | Job |
|---|---|
| `nvhttp.cpp` | GameStream-compatible HTTP server: pairing, app list, launch/resume |
| `rtsp.cpp` | RTSP session negotiation (supports encrypted RTSP, AES-GCM); creates a `launch_session_t` holding resolution, fps, keys |
| `stream.cpp` | UDP transport: video, control, audio. Ports are offsets from a base port (video +9, control +10, audio +11); FEC; AES-GCM video, AES-CBC audio |
| `video.cpp`, `platform/windows/display_vram.cpp`, `display_ram.cpp`, `nvenc/` | Capture of one display, encode (NVENC, AMF, QuickSync, or software through FFmpeg) |
| `audio.cpp` | Audio capture + Opus |
| `input.cpp`, `platform/windows/input.cpp` | Keyboard/mouse via `SendInput`; gamepads via ViGEm; touch/pen via synthetic pointer API |
| `process.cpp` / `process.h` (`proc::proc_t`) | Launches the streamed app and owns the virtual display lifecycle (creates it at launch, destroys it at terminate) |
| `display_device.cpp` + `libdisplaydevice` | Applies and reverts display settings (resolution, HDR, layout) |
| `platform/windows/virtual_display.cpp` | Talks to the SudoVDA driver by IOCTL |
| `confighttp.cpp` + Vue web UI | Config, client pairing, permissions |
| `system_tray.cpp`, `config.cpp` | Tray, configuration |

Apollo's own extras over Sunshine: virtual display with per-client identity, per-client permissions, clipboard sync (control packet `0x3001`), server commands, input-only mode. Clipboard and the client-side virtual-display options are documented as requiring its own Android client (Artemis) [V]. A later check found that a desktop fork, **Artemis Qt** (wjbeckett/artemis; Windows x64/ARM64, macOS, Linux), also lists clipboard sync and Apollo support, and a lesj0610/moonlight-qt fork merged two-way text and image clipboard on 2026-09-30. Neither mentions multi-window or multi-monitor support [from a third-party research report, spot-checked on the repo pages only].

**Third-party dependencies [V]:** moonlight-common-c (a ClassicOldSong fork, with its bundled ENet), FFmpeg (prebuilt static libs, plus x264/x265/SVT-AV1), OpenSSL, Boost (Asio, Log, Locale), nanors (Reed-Solomon FEC), libdisplaydevice, ViGEmClient, NVAPI SDK, nv-codec-headers, Intel VPL, Simple-Web-Server (a fork), tray, miniupnpc, nlohmann_json, googletest. Linux/macOS-only: inputtino, Wayland protocol headers, NvFBC, glad, TPCircularBuffer.

### 1.2 Client: moonlight-qt [V]

| Layer | Contents |
|---|---|
| UI | Qt Quick (QML): host list, settings |
| Backend | `ComputerManager` (mDNS discovery via qmdnsengine), `NvPairingManager`, `IdentityManager` (per-install client certificate) |
| Streaming core | `Session` (one active session, held in a static pointer `s_ActiveSession`), FFmpeg decoders, Windows renderers (D3D11VA, DXVA2, libplacebo), SDL audio, `SdlInputHandler` |
| CLI | `moonlight stream <host> <app>` starts a session directly without the GUI |

Submodules: moonlight-common-c, qmdnsengine, SDL_GameControllerDB. Other deps: Qt, SDL2, FFmpeg, libplacebo, OpenSSL, Opus.

### 1.3 Protocol library: moonlight-common-c [V]

- Core GameStream client implementation shared by every Moonlight client; GPL-3.0.
- Requires its own bundled version of ENet (breaking changes; do not link another libenet).
- Keeps **process-global state** (variables in `Limelight-internal.h`; one connection API: `LiStartConnection` / `LiStopConnection`).

### 1.4 Virtual display driver: SudoVDA [V]

- User-mode indirect display driver (UMDF/IddCx), based on Microsoft's sample; license MIT / CC0 / public domain.
- IOCTLs: `ADD_VIRTUAL_DISPLAY` (width, height, refresh), `REMOVE_VIRTUAL_DISPLAY` (by GUID), `SET_RENDER_ADAPTER`, `DRIVER_PING` (watchdog).
- Registry setting `maxMonitors`, **default 10**. Resolutions 640x480 to 8K; refresh up to 500 Hz.
- Apollo ships it as `.inf` + `.cat` + `.cer` files, which suggests a self-signed certificate that its installer trusts. [?] check how it is signed before depending on it.

---

## 2. Dependency map (what each block needs)

```
SudoVDA (MIT)  <--IOCTL--  host process
                              |
DXGI Desktop Duplication -----+--> encoder (NVENC/AMF/QSV via FFmpeg or Media Foundation)
(OS API, per output)          |        |
                              |     packetize + FEC (nanors) + AES-GCM (OpenSSL)
                              |        |
                              |      UDP video / control (ENet) / audio
                              |        |
SendInput (OS API) <----------+--- input packets          ---->  client process
                                                                   |
                         moonlight-common-c (GPL, global state) ---+
                         FFmpeg decode + D3D11VA render + SDL input
```

Everything above the line "OS API" is reusable in principle. The parts that are not cleanly reusable are the **singletons**: `proc_t`, the single `launch_session_t` promoted from the RTSP handshake, global config state, and moonlight-common-c's process-global connection.

---

## 3. Findings that decide the design

| # | Finding | Status | Consequence |
|---|---|---|---|
| F1 | The driver is not the limit: `maxMonitors` defaults to 10 and the IOCTL creates a monitor per call. | [V] | Multi-monitor is a host-session problem, not a driver problem. |
| F2 | In Apollo, the virtual display is created during app launch and destroyed during terminate, inside the single `proc_t`. | [V] | One virtual display per host process. |
| F3 | The maintainer says native multi-display is blocked by messy inherited Sunshine logic and recommends multiple instances for now. A user with two Android clients reported only one virtual display appearing at a time even with separate instances; no fix in that thread. | [V] | Multi-instance is the supported workaround, but not guaranteed to work. Test it first. |
| F4 | A GameStream session carries one video stream (+ audio + control). | [V] (from the RTSP/stream design) | N monitors = N sessions, unless we change the protocol. |
| F5 | Moonlight-common-c holds global state, so one process can run one stream. A third-party project (MoonlightWeb) states it lifted this limit by running **one worker child process per session**. | [V] | Multi-window in one process needs de-globalizing the C library. One process per monitor works with no changes. |
| F6 | Relative mouse = `SendInput` with `MOUSEEVENTF_MOVE`; absolute mouse = `MOUSEEVENTF_ABSOLUTE`, "scaled against the host resolution." How absolute coordinates map when several instances each stream a different display is not documented in what I read. | [?] | **This is the make-or-break unknown for "one workspace".** See E3 in Phase 0. |
| F7 | Permissions: the first paired client gets full permissions; later clients get View Streams and List Apps only. Each instance has its own client list, so one Moonlight install paired to three instances is the "first client" on all three. | [V] | All instances accept input by default; view-only needs explicit revoking. |
| F8 | Apollo says it and its Android client "will no longer be compatible with OG Sunshine and OG Moonlight eventually." Apollo's last tagged release I found was v0.4.6 (Jul 2025), with commits through May 2026. | [V] (earlier research) | A long-lived fork diverges from two upstreams; patch queues must be small. |
| F9 | Licenses: Apollo, Sunshine, moonlight-qt, moonlight-common-c are GPL-3.0. SudoVDA is MIT/CC0. | [V] for Apollo, common-c, SudoVDA. [?] for Sunshine, moonlight-qt (from my knowledge) | Copying their code makes our code GPL-3.0 (fine for personal use; source must be shared if distributed). Driver use is unrestricted. |
| F10 | The Moonlight Windows installer may need internet for the VC++ runtime; the portable build avoids that. | [V] | Use portable Moonlight on the offline LAN. |

**A correction to my earlier advice:** I suggested capturing input in only one Moonlight window and making the others view-only. If F6 turns out well, the better model is **absolute mouse mode in every window with input allowed on every instance**, so the pointer flows from window to window like a normal multi-monitor desk. The capture-in-one-window model is the fallback. Phase 0 tests both.

---

## 4. Options

| Option | What it is | One workspace? | Size | Ongoing burden | Main risk |
|---|---|---|---|---|---|
| **O0** Orchestrate stock | No source changes. A supervisor on the laptop generates N Apollo configs, launches/restarts the instances; a launcher on the desktop starts N portable Moonlight processes and places their windows. | Yes if E3 works | S | Low | F3: displays colliding, launcher flakiness |
| **O1** O0 + small patch queue | Same, plus a few patches on Apollo (and maybe the client launcher): stable per-instance display identity, don't revert the display layout per instance, status/health endpoint, auto-start. | Yes if E3 works | S-M | Medium (rebase on each Apollo release) | Patches touch fast-moving code |
| **O2** Host multi-session core | One Apollo-derived host process owning N sessions and N virtual displays; stock Moonlight on the client. | Same as O0 for input | L | High (long-lived GPL fork) | Maintainer calls the session logic too tangled; depth unknown |
| **O3** Client multi-window | A client that shows N streams. In one process this needs moonlight-common-c de-globalized (L). Via worker child processes (the MoonlightWeb pattern) it is M. | Yes: one input handler owns all windows | M-L | Medium | Does nothing for the host; pairs with O0/O1, not instead of them |
| **O4** Lean recompose | New Windows-only host + client with a purpose-built protocol. Reuse: SudoVDA, DXGI duplication, hardware encoders, D3D11, OS input APIs. Read Sunshine as a reference. | Yes, native: one connection, N displays, one input model | XL | Own all of it, but a small surface | The polish tail: latency, pacing, input edge cases, hybrid GPUs |

**What O4 leaves out (v1):** gamepads, audio, HDR, Linux/macOS, web UI, app launching, UPnP, GameStream compatibility. That is most of Apollo's code.

**What O4 must still solve (learned the hard way by Sunshine; read, don't guess):**
- Low-latency encoder settings (Apollo patches SPS/VPS flags in `cbs.cpp`).
- Dual-GPU laptops (capture and encode on the same adapter; Apollo has an `Adapter Name` option).
- Input desktop switching for UAC and the lock screen (Apollo tracks the last input desktop per thread).
- Key layout translation, stuck-key release, display layout restore.
- Network pacing, FEC, QoS tagging.

---

## 5. Recommendation: a ladder with gates

```
Phase 0 experiments  -->  O0 (orchestrate)  -->  O1 (patch queue)  -->  decision memo  -->  O4 (lean recompose)
                          stop here if it       only if a specific       O2 vs O4 using       or O2 if Apollo's session
                          works well            failure needs source     Phase 0 data          code proves shallower than feared
```

Why this order: each rung is cheaper than the next and produces the data that justifies (or kills) the next. We will know exactly **which** assumption broke instead of guessing.

**Why O4 over O2 if we get that far (judgment [?]):** O2 means refactoring a codebase whose maintainer says its session logic is too tangled, and then keeping a fork alive against two diverging upstreams. O4 starts from zero but only for the narrow slice we need, with no compatibility constraints. If a first read of `process.cpp` and `rtsp.cpp` shows the session singletons are shallow, O2 becomes more attractive; section 7 lists what to inspect.

**Where recompose clearly wins:** if we decide we want one connection, one pairing, one clipboard, and a pointer that crosses windows with no mode switching, those are properties of a single-process client talking to one host session. A pile of per-monitor processes gets them only approximately.

---

## 6. Phase 0: experiments (do these before any code)

Setup: laptop with Apollo installed (SudoVDA included) and any other virtual display driver removed; desktop with a **portable** moonlight-qt. Use Gigabit Ethernet for the first runs. Record results in a table.

| ID | Experiment | Pass criteria | If it fails |
|---|---|---|---|
| E1 | Two Apollo instances (separate conf: base port at least 30 apart, name, state file, log; run as SYSTEM). Connect one Moonlight to each. | Windows Display Settings on the laptop shows **two** new monitors at the same time and both stay up | Try two *different* client identities (separate portable Moonlight folders). Still failing = O1 patch (display identity) or O4 |
| E2 | Two moonlight-qt processes on the desktop (CLI `stream` mode), one window per physical monitor, borderless/fullscreen. | Both run at once, correct monitor each, no crash | Worker-process launcher (O3 pattern) or Phase 3 memo |
| E2b | Repeat E2, E3 and E3b using **Artemis Qt** (Windows build) instead of stock moonlight-qt, one process per monitor. Also check clipboard sync against Apollo. | Same pass criteria as E2/E3/E3b, plus clipboard works both ways with Apollo | If stock moonlight-qt passes, ignore. If Artemis Qt behaves better, treat it as a candidate client base for O3; if clipboard fails, port its approach in M7 |
| E3 | Input model A: absolute mouse (remote-desktop mode), input permission on all instances. Move the pointer from window 1 to window 2 and click. | Pointer lands on the **correct display** of the laptop; clicks hit the right window; no jumping | Try input model B: capture input in one window only, others view-only. If neither works, input is an O1/O4 problem |
| E3b | Keyboard focus: type while each window is focused. | Keystrokes reach the laptop's focused window every time | Check input-desktop handling and permissions |
| E4 | Load: 2 then 3 streams, 1080p60. Log CPU, GPU, encoder use, latency (phone slow-mo or timestamp overlay). | Latency near single-stream numbers; no growing lag | Check the GPU's concurrent hardware-encode limit [?]; lower resolution; consider shared-encoder design (O2/O4) |
| E5 | Disconnect/reconnect, sleep/wake, lid close. | Virtual monitors disappear and layout is restored; windows left on them don't get lost | O1 patches for layout handling |
| E6 | Offline install on an isolated LAN (no internet at any step). | Everything pairs and streams | Pre-stage runtimes (VC++) on USB |

### Gate decisions
- E1, E2, E3 pass -> build **O0**, ship it, stop.
- E1 fails only because of display identity -> **O1** (small patch).
- E3 fails -> decide whether input is solved by model B, or becomes a reason for O3/O4.
- E4 fails (N processes too heavy or encoders limited) -> O2 or O4 with a shared pipeline.

---

## 7. Phase 1: O0 build sketch (what the agent would implement)

**Host supervisor (laptop):**
- Config file: list of displays `{name, base_port, width, height, refresh, position}`.
- Generates per-instance `sunshine_N.conf`, state and log files; starts each instance as SYSTEM; restarts on exit; stops the default Apollo service (per the Fleet Launcher author's notes).
- Health checks, one log view, a tray icon.
- Pairing and permissions: via Apollo's web UI REST API (`confighttp.cpp`) [?] read the endpoints before relying on them.

**Client launcher (desktop):**
- Reads the same config; starts one portable Moonlight per display with the right host:port and window placement. [?] verify which Moonlight CLI flags choose the monitor.
- Optionally keeps the windows alive and re-places them after a disconnect.

**Not yet verified, read first:** Apollo's `docs/building.md` (to build from source on Windows), `confighttp.cpp` endpoints, `process.cpp` and `rtsp.cpp` singleton boundaries, `virtual_display.cpp` per-client identity logic, and the absolute-mouse mapping in `input.cpp` / `platform/windows/input.cpp`.

---

## 8. Working with the coding agent on these codebases

1. **Baseline first.** Build Apollo and moonlight-qt **unchanged** and run them. No feature work until both build and stream.
2. **Keep upstream pristine.** Add them as submodules or pinned vendor trees. Our changes live in a patch series (`git format-patch` / rebase-able branch), not in scattered edits.
3. `AGENTS.md` must contain: the goal, the options table, the gate decisions, build commands, "read the code path before changing it", "one patch = one purpose", and "report which source files you touched."
4. The agent runs on a real Windows machine with a GPU. Driver install, DXGI capture, hardware encode, and input injection can't be tested in a cloud sandbox.
5. Agent-friendly tasks: config generation, supervisor/launcher, log parsing, test harnesses, reading and summarizing a module. Supervise closely: anything in `process.cpp`, `rtsp.cpp`, `display_device.cpp`, DXGI/D3D11 resource handling, and input coordinate mapping.

### First prompts
> Read this file. Build Apollo from source unchanged on this Windows laptop, following its `docs/building.md`, and confirm it starts and streams to a stock Moonlight client. Don't modify any source. Report the exact build steps and any problems.

> Read `src/process.cpp`, `src/process.h`, `src/rtsp.cpp`, `src/display_device.cpp` and `src/platform/windows/virtual_display.cpp`. Write a one-page memo: where do the single-session assumptions live, which are global variables, and what would change to support two concurrent sessions in one process? Don't edit anything.

---

## 9. Licensing notes

- Copying or modifying code from Apollo, Sunshine, moonlight-qt or moonlight-common-c makes the result GPL-3.0. For personal use that is no obstacle; if you distribute, you must provide the source.
- A clean-room O4 using only the MIT/CC0 driver, OS APIs, and permissively licensed libraries can use any license. Reading the GPL code for understanding is fine; copying is what triggers the license. [?] Check each library's license before choosing it.
- FFmpeg builds that include x264/x265 are GPL; hardware-encoder-only LGPL builds avoid that. [?] confirm for the build you pick.

---

## 10. What I could not verify

- Apollo's source tree listing (GitHub's API and tree pages were blocked); I relied on DeepWiki's pages for the code layout, indexed at commit `adc5c5a0`, which may lag master.
- Where exactly the single-session assumption lives in the host code, and how deep it goes.
- Absolute-mouse coordinate mapping across instances (F6).
- Whether multiple moonlight-qt processes coexist smoothly on one Windows PC, and which CLI flags place windows on a chosen monitor.
- Licenses of Sunshine, moonlight-qt and libdisplaydevice (assumed GPL; not read).
- GPU concurrent hardware-encode limits on your specific laptop GPU.
- Artemis Qt: release status, licence, whether it works with Apollo's clipboard, and how it behaves with several processes. Only the repo README was read.

---

## Sources

- Apollo README: https://github.com/ClassicOldSong/Apollo
- Apollo multi-instance wiki: https://github.com/ClassicOldSong/Apollo/wiki/How-to-start-multiple-instances-of-Apollo
- Apollo issue #874 (multiple displays): https://github.com/ClassicOldSong/Apollo/issues/874
- Apollo submodules: https://raw.githubusercontent.com/ClassicOldSong/Apollo/master/.gitmodules
- DeepWiki, Apollo: overview https://deepwiki.com/ClassicOldSong/Apollo, streaming pipeline https://deepwiki.com/ClassicOldSong/Apollo/2.1-streaming-pipeline, RTSP/transport https://deepwiki.com/ClassicOldSong/Apollo/8.2-rtsp-and-streaming-transport, virtual display https://deepwiki.com/ClassicOldSong/Apollo/9-virtual-display-and-display-device-management, application management https://deepwiki.com/ClassicOldSong/Apollo/7-application-management, Windows input https://deepwiki.com/ClassicOldSong/Apollo/6.1-windows-input, dependencies https://deepwiki.com/ClassicOldSong/Apollo/11-external-dependencies
- DeepWiki, moonlight-qt: overview https://deepwiki.com/moonlight-stream/moonlight-qt, session management https://deepwiki.com/moonlight-stream/moonlight-qt/4.1-session-management, input handling https://deepwiki.com/moonlight-stream/moonlight-qt/4.7-input-handling
- moonlight-qt submodules: https://raw.githubusercontent.com/moonlight-stream/moonlight-qt/master/.gitmodules
- moonlight-common-c: https://github.com/moonlight-stream/moonlight-common-c ; architecture notes: https://deepwiki.com/moonlight-stream/moonlight-common-c/1.1-architecture-overview
- SudoVDA: https://github.com/SudoMaker/SudoVDA
- MoonlightWeb v0.1.3 (worker-process workaround): https://github.com/linckosz/moonlight-web/releases/tag/v0.1.3
- Multi-monitor Apollo setup (drajabr): https://github.com/drajabr/My-Sunshine-setup
- MultiSeat (example of widely spaced instance ports): https://github.com/vibesoftwarecoder/MultiSeat/wiki
- Sunshine architecture overview: https://www.mintlify.com/LizardByte/Sunshine/development/architecture
- Moonlight v6.2.0 release notes: https://github.com/moonlight-stream/moonlight-qt/releases/tag/v6.2.0
