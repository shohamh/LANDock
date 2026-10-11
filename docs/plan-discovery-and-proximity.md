# Plan: LAN discovery, favorites, and ultrasound proximity

Status: **partly built.** Done: LAN discovery with mDNS (Makaretu.Dns), the ordered laptop list with auto-connect, ggwave
ultrasound encode/decode (tested with synthetic audio, through noise), the microphone bars, and the `ultrasound test` command.
Not yet shown to work in a room: the acoustic path (see "Lab results" below). Sound testing is paused at the user's request
(a pet is sensitive to the sound). Verified without sound: the LAN advert and browse (the laptop is listed with its Wi-Fi
address), the ordering rules, and the settings. Both sound features are off by default.

Three parts, in the order they should be built:

1. **Discovery and choice** on the desktop PC: find laptops on the LAN, give them nicknames, mark favorites, and set the order
   in which the app connects automatically.
2. **Identification by ultrasound**: each desktop sends a unique ID as inaudible sound; the laptop hears it and knows which
   paired desktop is nearby.
3. **Proximity** from what the laptop hears, and how that feeds the choice in part 1.

Part 1 is needed for part 2 to be useful, and part 2 needs a lab test before anything depends on it.

## 1. Discovery, nicknames, favorites, auto-connect

### What the laptop does
- While the host runs, it advertises itself on the LAN as a DNS-SD service `_landock._tcp` (instance name = the laptop's
  name, port = the control port). TXT record: `hostId`, `name`, `ver`, `pairing=open|closed`, and later `ultra=1` (see part 2).
- Advertising stops when the host stops. Nothing is advertised while the host is off.

### What the desktop does
- Browses for `_landock._tcp` while LANDock runs and keeps a live list of laptops (appear, change, disappear).
- Shows them in the Client tab as **Nearby laptops**: nickname (or name), hostname, status (paired / new / offline),
  favorite star, and the place in the auto-connect order.
- A discovered laptop is only *offered*: connecting still goes through the check and pairing from today (the certificate
  fingerprint is shown, the PIN is entered once). A laptop that is not paired is never connected to automatically.

### Nicknames
- A nickname is set per laptop on the desktop. Keyed by **hostId** (stable), shown with the hostname, so a rename of the
  laptop does not lose it. The user asked for nicknames "for certain hostnames": the dialog shows the hostname and saves
  the nickname against the hostId it belongs to.

### Favorites and auto-connect order
- Each paired laptop has `Favorite` (bool) and `Order` (int). The Client tab lists them in order; drag to reorder.
- **Auto-connect at start** (setting, on by default once a laptop is paired): walk the order, skip laptops that are offline or
  not paired, and connect to the first one that answers. Then start its displays (the handshake runs as today).
- Only one laptop's displays run at a time (monitors are shared). A second laptop can be started by hand, which stops the
  first one's displays after asking.
- Ties and unknown laptops: a paired laptop that is not a favorite comes after the favorites, ordered by last connection.

### Data model (additions)
- `HostProfile`: `Favorite`, `Order`, `Nickname`; `HostId` already exists and is the key.
- `ClientSettings`: `AutoConnect` (bool), `DesktopUltrasoundId` (see part 2).
- Discovered-but-unpaired laptops are kept in memory only (not saved) until they are paired.

### Libraries (discovery)
| Library | License | Use | Notes |
|---|---|---|---|
| [Tmds.MDns](https://github.com/tmds/Tmds.MDns) | to verify (LICENSE file) | desktop: browse `_landock._tcp` | Self-contained: no Bonjour or Avahi needed; resolves addresses itself; events on the WinForms sync context. |
| [Makaretu.Dns.New](https://www.nuget.org/packages/Makaretu.Dns.New) (with `Makaretu.Dns.Multicast.New`) | MIT (per the NuGet listing) | laptop: advertise the service | Can act as the responder for DNS-SD, targets .NET 8/9. |
| Windows `DnsServiceRegister` (windns.h, via P/Invoke) | part of Windows | laptop: advertise, no dependency | [Windows DNS-SD reference](https://learn.microsoft.com/uwp/api/windows.networking.servicediscovery.dnssd); the WinRT `Dnssd` classes are marked prerelease, so prefer the Win32 call if we go native. |

**Recommendation:** Tmds.MDns on the desktop and Makaretu on the laptop, behind one small interface, so the Win32 route can
replace the laptop side later without touching the rest. Confirm Tmds.MDns's license before adding it.

**Fallback:** a UDP broadcast beacon (JSON, every 2 s, port to choose) that needs no library. Use it only if mDNS is blocked
on a real network. It is simple, but it sends to every host on the subnet, so mDNS stays the primary.

### Security
- Discovery reveals names and ports on the local network only. The token and the PIN are never sent in discovery.
- Anything heard from discovery is untrusted until the fingerprint and PIN check (today's flow) succeed.

## 2. Ultrasound identification

### Idea
- Each **desktop** has a stable random ID (generated once, saved, never shared outside pairing/ultrasound).
- The desktop plays its ID as an **inaudible** sound every ~15 s (with random jitter), through its speakers, at a fixed low
  level set by the app (not the system volume).
- The **laptop** listens on its microphone, decodes IDs, and maps each one to the paired desktop with that ID.
- When a desktop is paired, its ultrasound ID is sent to the laptop over the control port (`/api/pair` gets a new field),
  so the laptop only ever recognises desktops it has paired with.

### Library: ggwave
- [ggwave](https://github.com/ggerganov/ggwave) is a data-over-sound library in C/C++, **MIT** (per the project README; check
  the LICENSE file). It has ultrasound protocols (`UltrasoundNormal/Fast/Fastest`, IDs 3–5), multi-tone FSK modulation,
  Reed-Solomon error correction, and a 48 kHz sample rate.
- Throughput is 8–16 bytes per second, so an 8–16 byte ID takes roughly 1–2 s per message plus overhead.
- Ultrasound tones are about 15–19.5 kHz by my arithmetic from the base frequency and bin spacing the project documents. That
  is **not confirmed** and must be measured with our hardware.
- **Integration:** call its C API (`ggwave_init`, `ggwave_encode`, `ggwave_decode`, to be checked against `ggwave.h`) from C#
  through P/Invoke, with the library built in CI (MSVC or MinGW) and shipped next to `LANDock.exe`. There are C# wrappers on
  NuGet (for example GGWavePlayerLib), but I have not checked their license or maintenance, so they are not the plan.
- Other references: a [Node/Bare binding](https://github.com/holepunchto/bare-ggwave) shows custom protocols can be defined.

### Audio I/O
- **Capture (laptop):** [NAudio](https://github.com/naudio/NAudio), MIT per its NuGet listing: `WasapiCapture` for the
  microphone, mono, 48 kHz, 16-bit or float, read into a ring buffer, decoded on a background thread.
- **Playback (desktop):** NAudio `WasapiOut` (shared mode) with our own amplitude factor, so the system volume stays as the
  user set it.

### Simultaneous desktops (the identification problem)
- ggwave decodes one message at a time; two desktops transmitting at once collide. Options, to test in the lab:
  1. **Time slots and jitter (first choice):** random start times within the 15 s period. With a few desktops, collisions are
     rare and a missed beacon just waits for the next one.
  2. **Separate tone bands:** a custom protocol per desktop (or per group), each with a different base frequency. Our decoder
     then only sees its own band. More work; only if option 1 fails.
  3. **Listen before sending:** the desktop checks the band is quiet before its own transmission (carrier sense). Easy to add
     on top of option 1.
- The laptop reports the ID it heard only after a successful ggwave decode, which already includes Reed-Solomon checking, so
  random noise is not mistaken for a desktop.

### Proximity from the sound
- ggwave does not report signal strength, so the laptop measures it itself: a Goertzel or FFT energy reading at the protocol's
  tone bins, compared with the band's noise floor, per decoded beacon.
- **What we can claim:** "heard in this room" (a successful decode with a clear level above the floor) versus "not heard
  recently" (no decode for a set time). Ultrasound does not pass walls well, so a decode is a good room-level signal.
- **What we cannot claim:** a precise distance. Level depends on the speaker volume, the microphone and its gain/AGC, room
  acoustics and background noise. Air attenuation at 15–20 kHz is frequency- and humidity-dependent, so amplitude gives at
  best a coarse ordering. Timing-based ranging (as in [SonicPACT](https://arxiv.org/abs/2012.04770)) needs synchronised
  clocks and is out of scope for now.
- Literature for the coarse idea: [Sound-based proximity detection with mobile phones](https://www-live.dfki.de/fileadmin/user_upload/import/7857_Sound-based_proximity_detection_with_mobile_phones.pdf)
  (about 18 kHz beacons for fine spatial resolution, combined with Bluetooth for coarse range) and
  [frequency-selective ultrasound attenuation ranging](https://www.ncbi.nlm.nih.gov/pmc/articles/PMC8347942/) (simulation).

### How proximity feeds the choice (part 1)
- The laptop advertises the desktops it heard recently in its TXT record (`heard=<ids>`), and the desktop reads its own ID
  there. A desktop that finds itself in `heard` knows it is in the same room as this laptop.
- **Auto-connect** then prefers laptops the desktop can prove are in the room: a laptop in the order is connected first if
  it is nearby, otherwise the order decides. (Favorites still come first when neither is known.)
- The laptop's tray shows the nearby desktops ("Office PC: nearby, last heard 12 s ago").
- Proximity is a **hint, not authentication**. Anyone nearby can play the same ID. The PIN, the token and the fingerprint
  stay the only trust.

### Privacy and platform
- The microphone is used only when the user turns on *Listen for nearby desktops* on the laptop, and only while the host runs.
  The tray shows when it is listening. Windows' microphone privacy settings must allow desktop apps; the app should detect
  that and say so.
- The desktop plays sound only when the user turns on *Announce this PC by sound*. Nothing else is played.
- Inaudible is not silent for everyone: some people hear 18–20 kHz, and some pets do. Keep the level low and let the user
  turn it off or pick the protocol.

## 3. Lab tests before building the feature on top

Run these on the real laptop and desktop, with real speakers and microphone, before part 2 is trusted:

1. **Band check:** does the laptop mic hear the ultrasound protocol at 1 m, 3 m, 6 m, through a wall? Record the decode rate
   per distance and speaker volume.
2. **Speaker check:** can the desktop speakers play 15–20 kHz at a usable level? Many laptop and desktop speakers roll off
   above 16 kHz.
3. **Two-desktop check:** two desktops beaconing at once; how often does each ID decode within 60 s? Compare time slots and
   carrier sense.
4. **Noise check:** music, fans, keyboard typing, a second laptop nearby.
5. Results go to `docs/results/ultrasound-*.md`, with the hardware listed. Decide the defaults from those numbers.

Go/no-go: if the decode rate at 3 m is not high enough (for example below 80 % within 30 s), part 2 is limited to the room
check at short range, or dropped in favour of the LAN-only proximity (laptop advertises which desktops it sees over Wi-Fi,
which is weaker).

## Lab results so far

- The speakers play the tone: on this laptop the playback device pulled the tone samples (`ultrasound test` prints
  "device asked N times, took M samples"), and the digital loopback of the output shows the tone.
- The microphone bars do not show a clear rise at 15.6, 17.7 or 19.2 kHz through the laptop's own speakers, at the same
  level as the room noise. Either the laptop speakers or the microphone do not carry this band well. Next: test with a
  desktop's speakers and a microphone that is known to record ultrasound, and compare the bars with `ultrasound test`.
- The audible fallback is built (ggwave's audible protocol, 1.9 to 6.4 kHz, announced every 45 to 60 s) and is the default.
- Later check on this laptop: the microphone delivers almost no sound. Its whole-signal level is about -127 dBFS while the
  driver's own peak meter on the same device shows a small level. Neither the bars nor the decoder can work from that.
  The Host tab now says so, with the level. Likely cause: the input level or the enhancements of the microphone in Windows.
  Next: fix the microphone's input level or pick another microphone in General > Sound, then run
  `LANDock ultrasound test 30` again, which prints the chirp's frequencies next to the speaker output.

## Milestones

| # | Milestone | Done when |
|---|---|---|
| M1 | Laptop advertises `_landock._tcp` (Makaretu) | a second PC sees it with `dns-sd`-style browse and the TXT record is correct |
| M2 | Desktop lists nearby laptops (Tmds.MDns) | list updates when the host starts and stops |
| M3 | Nicknames, favorites, order, auto-connect | with two laptops, auto-connect picks the first online favorite; tests cover the order rules |
| M4 | Ultrasound lab tests (section 3) | the results doc exists and the defaults are set from it |
| M5 | ggwave native build in CI, P/Invoke wrapper, encode/decode round trip on one PC | unit test encodes an ID and decodes it back through the speaker and mic loop |
| M6 | Desktop announces its ID; laptop listens and maps IDs to paired desktops | a paired desktop is recognised within one period |
| M7 | Proximity: heard/not-heard, tray and auto-connect preference | moving a desktop out of the room changes the state within two periods |

## Open decisions for you

1. **Auto-connect scope:** connect to one laptop at a time (recommended), or let several laptops share the screens?
2. **Proximity:** room-level (*heard / not heard*) only, as planned, or do you want the distance estimate as well (I would
   not promise it until M4 is done)?
3. **Listening by default:** off (recommended) or on for paired laptops?
4. **Build ggwave from source in CI** (recommended, MIT, no unknown NuGet package), or use a NuGet wrapper after checking its
   license?

## Sources

- ggwave (MIT badge in README, protocols, throughput, ultrasound): https://github.com/ggerganov/ggwave ; discussion of the
  frequency layout and 48 kHz analysis: https://github.com/ggerganov/ggwave/discussions/4 ; a binding with custom protocols:
  https://github.com/holepunchto/bare-ggwave
- Tmds.MDns: https://github.com/tmds/Tmds.MDns (license not verified; check the repository's LICENSE)
- Makaretu.Dns.New (MIT per NuGet): https://www.nuget.org/packages/Makaretu.Dns.New
- Windows DNS-SD API: https://learn.microsoft.com/uwp/api/windows.networking.servicediscovery.dnssd
- NAudio (MIT per NuGet listings, WASAPI capture/loopback): https://github.com/naudio/NAudio
- Sound-based proximity with phones (DFKI): https://www-live.dfki.de/fileadmin/user_upload/import/7857_Sound-based_proximity_detection_with_mobile_phones.pdf
- SonicPACT (near-ultrasound ToF on phones): https://arxiv.org/abs/2012.04770
- Frequency-selective ultrasound ranging (simulation): https://www.ncbi.nlm.nih.gov/pmc/articles/PMC8347942/

Things I could not confirm from the search results: the exact ggwave ultrasound frequency band (my estimate is 15–19.5 kHz),
the Tmds.MDns license, the exact ggwave C function signatures, and any published measurement of ultrasound range on laptop
hardware. These are the first items of section 3.
