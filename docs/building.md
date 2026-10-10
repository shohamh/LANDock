# Building and releasing

Everything in a release is built from source by GitHub Actions. You only need a local build to develop or test.

## What is built from what

| Part | Source | Built by |
|---|---|---|
| App (`LANDock.exe`, self-contained single file) | `app/LANDock` | `dotnet publish` (.NET 10 SDK) |
| Host: Apollo + `vddtool.exe` | our fork [shohamh/Apollo](https://github.com/shohamh/Apollo) at the commit pinned in `scripts/versions.env`; `tools/vddtool.cpp` | `scripts/build-host.sh` in MSYS2 UCRT64 |
| Client: Moonlight | our fork [shohamh/moonlight-qt](https://github.com/shohamh/moonlight-qt) at the pinned commit | `scripts/build-moonlight.ps1` (Qt 6 + MSVC) |
| Update parts + manifest | the three above | `scripts/package-components.ps1` |
| Installer | the update parts | `scripts/build-installer.ps1` (Inno Setup 6) |

## CI

| Workflow | When | What |
|---|---|---|
| `ci.yml` | push to `main`, pull requests | app unit tests; script syntax; builds the installer with stand-in binaries and installs it silently |
| `build.yml` | called by the release; also by hand | builds the app, Apollo and Moonlight from source. Run it by hand with `apollo_ref` / `moonlight_ref` to test a candidate branch of a fork |
| `release.yml` | pushing a tag `vX.Y.Z`; by hand for a dry run | build, package, installer, publish the GitHub release (installer, three part zips, `manifest.json`, `SHA256SUMS.txt`) |
| `upstream-watch.yml` | weekly | opens an issue when Apollo or Moonlight published a release newer than our base ([upstream-sync.md](upstream-sync.md)) |

### Cutting a release

1. Add a `## [X.Y.Z]` section to `CHANGELOG.md` (it becomes the release text).
2. `./scripts/release.sh X.Y.Z` (from `main`, clean tree): bumps `VERSION`, commits, tags `vX.Y.Z`, asks before pushing.
3. The tag push runs `release.yml`. Test first with *Actions > Release > Run workflow* (a dry run publishes nothing).

## Building locally

Requirements: Windows 10/11, the [.NET 10 SDK](https://dotnet.microsoft.com/download), and for the full set Git for
Windows (bash), [MSYS2](https://www.msys2.org/), Qt 6 with MSVC (see `scripts/build-moonlight.ps1`) and
[Inno Setup 6](https://jrsoftware.org/isdl.php) (`winget install JRSoftware.InnoSetup`).

```bash
# the app (unit tests, then a single-file build)
dotnet test app/LANDock.Tests
dotnet publish app/LANDock/LANDock.csproj -c Release -p:Version=0.1.0 -o build/app-publish

# the host (Apollo + vddtool) into host/bin, from MSYS2 UCRT64
./scripts/build-host.sh
```

```powershell
# package the parts (downloads and checks the official Moonlight release unless -MoonlightZip is given),
# put them together like the installer does, and build the installer
.\scripts\package-components.ps1 -AppExe build\app-publish\LANDock.exe -Version 0.1.0
.\scripts\assemble-install.ps1          # -> build\run (LANDock.exe, host\, client\), runnable without installing
.\scripts\build-installer.ps1 -Version 0.1.0   # -> dist\LANDock-Setup-0.1.0.exe
```

Run the app from `build\run\LANDock.exe`. `LANDOCK_DATA_DIR` and `LANDOCK_INSTALL_DIR` move its data and install folders
(handy for testing next to an installed copy).

## End-to-end tests

```bash
./tests/run-display-tests.sh        # needs build\run assembled, the SudoVDA driver, an unlocked desktop
```

It starts the host, pairs and starts three streams over loopback, then opens Notepads on every virtual display, types
into them, sweeps the mouse across all displays and drags windows between displays with the real mouse, checking
positions, text and which streams changed. **It controls your mouse and keyboard for a few minutes.** The first run
may trigger a Windows Firewall prompt for `sunshine.exe`; answer *Allow* (a prompt left open takes the keyboard focus and
makes the run fail).

## Changing Apollo or Moonlight

See [upstream-sync.md](upstream-sync.md). In short: our changes are a few commits on the `landock` branch of each fork;
`scripts/versions.env` pins the commit that is built; `scripts/sync-upstream.sh` rebases them onto a new upstream
release as a candidate branch that you build from CI, test, and then adopt.
