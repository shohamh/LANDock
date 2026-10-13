# Credits and third-party notices

LANDock is mostly glue. It would not exist without the projects below, and most of the hard work (capturing,
encoding, streaming, input, the virtual display driver) is theirs. Licences are as stated by each project on GitHub
or in its own README when this file was written.

## What LANDock uses

| Project | By | Licence | How LANDock uses it |
|---|---|---|---|
| [**Apollo**](https://github.com/ClassicOldSong/Apollo) | ClassicOldSong | GPL-3.0 | The host. We build it from source (tag `v0.4.8`) with our own patch (`patches/`) and run **three instances**, one per virtual display. Apollo's virtual-display handling, per-client display identity and input injection are what make the whole thing possible. Its [multi-instance guide](https://github.com/ClassicOldSong/Apollo/wiki/How-to-start-multiple-instances-of-Apollo) is the technique LANDock is built on. |
| [**Sunshine**](https://github.com/LizardByte/Sunshine) | LizardByte and contributors | GPL-3.0 | The streaming host Apollo is a fork of: capture, hardware encoding, the GameStream-compatible protocol, input, the web API. |
| [**Moonlight** (moonlight-qt)](https://github.com/moonlight-stream/moonlight-qt) | the Moonlight Game Streaming Project | GPL-3.0 | The client. Releases carry Moonlight built from our fork, which currently has **no changes** from the upstream release (when it does, they are listed in the release's `MOONLIGHT-SOURCE.txt`). The LANDock app only starts it with command-line options and moves its windows. |
| [**moonlight-common-c**](https://github.com/moonlight-stream/moonlight-common-c) | the Moonlight Game Streaming Project | GPL-3.0 | The GameStream protocol library inside Moonlight and Apollo. |
| [**SudoVDA**](https://github.com/SudoMaker/SudoVDA) | SudoMaker | MIT and CC0 / public domain ("choose the least restrictive"), based on Microsoft's Indirect Display Driver sample | The virtual display driver Apollo talks to. Our `vddtool` uses its public IOCTL header to list, lay out and remove virtual displays. SudoVDA is itself a rewrite that acknowledges MTT's Virtual Display Driver. LANDock **redistributes** the driver package in `driver/sudovda` (`SudoVDA.dll`, `SudoVDA.inf`, `sudovda.cat`, `sudovda.cer`; driver version 1.10.9.289): the installer trusts its certificate and installs it, so the virtual displays work without a separate download. The DLL is SudoMaker's signed build (signed by `sudovda@su.mk`). The MIT and CC0 licences apply to it. |
| [**.NET**](https://github.com/dotnet/runtime) and Windows Forms | Microsoft and the .NET Foundation | MIT | The LANDock app is a self-contained .NET application. |
| [**Inno Setup**](https://jrsoftware.org/isinfo.php) | Jordan Russell and Martijn Laan | Inno Setup licence (free, permits use for any purpose including building installers) | Builds the Windows installer in CI. |
| [**Boost**](https://github.com/boostorg/boost) | the Boost authors | Boost Software License 1.0 | Fetched (hash-checked) while building Apollo. |
| [**MSYS2**](https://www.msys2.org/) | the MSYS2 project | various; installer BSD-3-Clause | The toolchain used to build Apollo on Windows. |
| Apollo's own dependencies | various | various | FFmpeg, OpenSSL, libdisplaydevice, ViGEmClient, nanors, ENet and others are built into Apollo; see Apollo's [`NOTICE`](https://github.com/ClassicOldSong/Apollo/blob/master/NOTICE) and `third-party/`. The prebuilt FFmpeg libraries come from [LizardByte/build-deps](https://github.com/LizardByte/build-deps) (MIT). |

## Ideas and research this project learned from

- [**MoonlightWeb**](https://github.com/linckosz/moonlight-web) (linckosz): showed that Moonlight's single-stream
  limit can be worked around with one worker process per session, which is how LANDock thinks about "one stream per
  display".
- [**My-Sunshine-setup**](https://github.com/drajabr/My-Sunshine-setup) (drajabr): a multi-monitor Apollo setup with
  several instances.
- [**MultiSeat**](https://github.com/vibesoftwarecoder/MultiSeat) (MIT): an example of Sunshine instances on widely
  spaced ports.
- The [DeepWiki](https://deepwiki.com/ClassicOldSong/Apollo) architecture pages for Apollo, moonlight-qt and
  moonlight-common-c were used to find where the single-session assumptions live. `docs/design-study.md` lists
  every source used.
- NVIDIA's GameStream protocol is what Sunshine/Apollo and Moonlight reimplement; LANDock uses neither NVIDIA
  software nor NVIDIA's service.

## Who wrote what

- **LANDock itself** (the app, `vddtool`, the patch, the installer, the tests, the docs): the LANDock authors,
  with the implementation and testing done pair-programming style with **Claude Code** (Anthropic).
- Everything else is the work of the projects above and is not changed by LANDock, except Apollo, where our single
  patch is the only difference.

## Licence of this repository

LANDock is released under the **GNU General Public Licence v3.0** (see [`LICENSE`](LICENSE)). That is the licence of
Apollo, Sunshine and Moonlight, and `patches/` and `tools/vddtool.cpp` are derived from or compiled against Apollo
source, so GPL-3.0 is the safe and consistent choice for the whole repository. The installer and the release zips contain GPL-licensed
programs; the host part includes `SOURCE.md` and the patches, which together with the upstream tag it names are the
corresponding source. The client part carries `MOONLIGHT-SOURCE.txt` with the exact source it was built from.

If you believe something here is credited incorrectly or missing, please open an issue.
