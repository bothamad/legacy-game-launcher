# Legacy Game Launcher

A Windows 11 app (.NET 8, WPF, single-file, win-x64) that helps Windows 7-era 32-bit DirectX games, such as
EA Sports Rugby 08 and Cricket 07, run on modern Windows.

**It never cracks, patches or bypasses copy protection.** It detects copy-protection schemes and explains them,
and it cooperates with a SafeDisc loader only if you installed one yourself - it never installs or configures one.

## Download

Get `LegacyGameLauncher.exe` from the [latest release](../../releases/latest). No installer and no .NET install
needed. The app checks this repository for new releases and can update itself (**Check for updates** in the sidebar).
Every release lists its changes; see also [CHANGELOG.md](CHANGELOG.md).

## Features

| Tab | What it does |
|---|---|
| **Library** (sidebar) | Add a game's `.exe`; a profile is matched by SHA-256, exe name, version info or folder name. |
| **Overview** | Architecture, Direct3D version, input API, imports, compat flags, copy-protection scan (detection only) and a check for the legacy runtimes the game uses (June 2010 DirectX in SysWOW64, VC++ 2005/2008, OpenAL). Links to Microsoft's downloads; DXSETUP runs only if Microsoft-signed. |
| **Fixes** | One-click *Download & apply* / *Remove* for **dinputto8** (DirectInput crash fix), **dgVoodoo2** and **DXVK**. Downloaded only from each project's own GitHub release, checked against GitHub's SHA-256, installed with backup and undo. Microsoft files are never downloaded. |
| **Compatibility** | Compatibility layers (`HKCU\…\AppCompatFlags\Layers`), CPU core limit and priority (applied before the game's code runs), arguments, SafeDiscLoader2 cooperation (Auto/On/Off) and disc-image (.iso) mounting before launch. |
| **Graphics** | dgVoodoo2 / DXVK from a folder you choose: picks `d3d8.dll` or `d3d9.dll` to match the game and the x86 build, writes the config (forced resolution, refresh cap, windowed/borderless, VSync, FPS limit). |
| **Game config** | Scans for config files and registry values instead of guessing; quick-set resolution/windowed; binary `.pro` resolution editor for HB Studios' EA games. Backups before every save. |
| **Controllers** | XInput / DirectInput devices, what the game expects, a **live tester** (buttons and axes as DirectInput games see them) and dinputto8 / x360ce input wrappers. |
| **Launch log** | Starts the game from its own folder, follows child processes, logs exit code, Windows Error Reporting / event-log entries, crash advice from the faulting module, and keeps a history. *Copy diagnostics* for sharing. |
| **Changes & undo** | Every change the launcher made, with the original state; undo one group or everything. |

The launcher runs without admin rights. It asks for elevation per action (e.g. writing into `Program Files`)
through a short-lived copy of itself that only runs a request whose SHA-256 was fixed before the UAC prompt.

## Build

```
dotnet publish LegacyGameLauncher -c Release -o publish
```

Output: `publish\LegacyGameLauncher.exe` (self-contained, about 65 MB).

## Data locations

`%LocalAppData%\LegacyGameLauncher\` holds `library.json`, `backups\`, `profiles\`, `tools\` (downloaded fixes),
`logs\` and `staging\`.

## Adding games

Put a `*.json` profile in `%LocalAppData%\LegacyGameLauncher\profiles\`. Built-in profiles are exported there as
`example-*.json.txt` to copy from. Research notes for the built-in games are in [GAME_NOTES.md](GAME_NOTES.md).

## Third-party components

- [WPF-UI](https://github.com/lepoco/wpfui) (MIT) - Fluent theme, built into the exe.
- Fixes downloaded on request, not included: [dinputto8](https://github.com/elishacloud/dinputto8) (zlib),
  [dgVoodoo2](https://github.com/dege-diosg/dgVoodoo2) (freeware), [DXVK](https://github.com/doitsujin/dxvk) (zlib).
