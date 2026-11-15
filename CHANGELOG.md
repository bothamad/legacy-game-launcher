# Changelog

All notable changes to Legacy Game Launcher. Versions match the GitHub release tags (`vX.Y.Z`).
Earlier versions (0.1–0.4.3) were built before the project was on GitHub; their changes are listed here
and included in the first GitHub release.

## 0.9.1 - 2026-11-15
- Button layout: **Touchpad left / right click = keyboard ← / →** - on Rugby 08's team-select screen the game moves
  controllers with the keyboard arrows. The touchpad works as buttons (so it no longer moves the mouse) and clicking
  its left or right side sends Left/Right arrow as scan codes, which old DirectInput games read. The D-pad is untouched.

## 0.9.0 - 2026-11-15
- **Fix: controller not detected in-game with the DirectInput / Rumblepad 2 layout.** DS4Windows' Auto Profile switched
  only after the game's window appeared, unplugging the virtual Xbox pad and plugging in the virtual DS4 ~3 seconds
  later - after the game had already looked for controllers. For layouts that change the virtual controller, the
  launcher now switches DS4Windows to the game's profile **before** starting the game, waits until DS4Windows reports
  the virtual pad plugged in, and switches back after the game exits. Stale Auto Profile entries for such games are removed.
- **Game icons**: each game's own icon (from its exe) in the library list and next to the game title.

## 0.8.1 - 2026-11-15
- App icon: the launcher now has its own icon (exe, taskbar, title bar and Alt-Tab) instead of the generic one.

## 0.8.0 - 2026-11-15
- New button layout **DirectInput / Logitech Rumblepad 2** (virtual DS4 output). Research on The Rugby Forum shows the
  PC version of Rugby 08 is laid out for a Logitech Rumblepad 2 - a DirectInput pad with the same numbering as a
  PS4 pad (face buttons 1-4, L2/R2 = buttons 7/8, right stick on Z/Rz, D-pad as a hat). An Xbox 360 layout mixes
  buttons up (e.g. sprint on Start). This layout makes DS4Windows present a virtual DS4 to the game, with no remaps.
- Rugby 08 profile and GAME_NOTES: on PC, pause is Esc and special moves are L1 + a face button.

## 0.7.1 - 2026-11-15
- Button layout: **Touchpad doesn't move the mouse** (on by default) - DS4Windows' default turns the PS4 touchpad
  into a mouse, which moved the keyboard player on EA's controller-select screen and could leave the cursor stuck.
- Button layout: **D-pad acts as the left stick** - for games that ignore the D-pad (Rugby 08 menus and controller select).

## 0.7.0 - 2026-11-15
- **Per-game button layout for DS4Windows** (Controllers tab): choose *Xbox layout*, *PlayStation-direct layout*
  (Square = 1, Cross = 2, Circle = 3, Triangle = 4) or *Swap Cross and Circle*. The launcher creates a DS4Windows
  profile for the game - a copy of your usual profile with only the face buttons changed - plus an Auto Profile,
  so DS4Windows switches while the game runs and back afterwards. DS4Windows is closed and restarted around the
  change (it only reads Auto Profiles at start-up). *Back to default* removes both; other games' entries are untouched.

## 0.6.0 - 2026-11-15
- **Your own files** (Fixes tab): copy files or a whole folder you choose (a mod, a widescreen fix, a config pack)
  into the game folder, optionally into a subfolder. Anything replaced is backed up, every file is journaled with
  its SHA-256, and *Remove all* restores the folder exactly. The game's own `.exe` is never replaced, and nothing
  can be written outside the game folder.

## 0.5.0 - 2026-11-15
- **Fixes tab**: one-click *Download & apply* / *Remove* for dinputto8 (DirectInput crash fix), dgVoodoo2 and DXVK.
  Downloads come only from each project's own GitHub release, are checked against GitHub's published SHA-256,
  kept in `%LocalAppData%\LegacyGameLauncher\tools`, and installed with backup and undo.
- **Check for updates**: the launcher checks its GitHub releases at start-up and from the sidebar button, and can
  download (SHA-256 verified), swap in and restart into the new version.
- Project now on GitHub, with a changelog per release.

## 0.4.3
- A crash inside an input/graphics/audio DLL no longer triggers the misleading "SafeDisc loader may not have run" note.

## 0.4.2
- Live tester labels controllers that HidHide hides from games.

## 0.4.1
- **Live controller tester** (Controllers tab): buttons, axes (X/Y/Z/Rx/Ry/Rz) and D-pad exactly as DirectInput games
  see them, with PS4 / Xbox button names.

## 0.4.0
- **New Windows 11 Fluent UI** (WPF-UI): Mica window, library sidebar, header with an always-visible Launch button,
  single-row tabs, card sections, light/dark following Windows.

## 0.3.1
- **Disc image mounting**: per-game .iso, mount before launch / unmount after (Windows' built-in mounting, read-only, no admin).
- SafeDiscLoader2 log check no longer reports a failure after a clean run.

## 0.3.0
- **SafeDiscLoader2 cooperation** (a loader the user installed): Auto / On / Off per game; no compatibility layers
  (existing ones removed, undoable); CPU limit/priority opt-in; follows child processes such as `~e5.0001.exe`;
  loader-log check replaces the generic DRM message.
- Every game now starts with its exe folder as working directory.

## 0.2.1
- Direct3D version is taken from the game exe first (installer DLLs no longer confuse it).
- Detection of modified (copy-protection-removed) executables - detection only.

## 0.2.0
- Research-based profiles for Rugby 08 and Cricket 07 (sources listed).
- Direct3D 8 wrapper support (dgVoodoo2 `D3D8.dll`, DXVK 2.4+).
- dinputto8 input-wrapper option; crash advice from the event log's faulting module.
- Binary `.pro` resolution editor; text-encoding fix.

## 0.1.0
- First version: game library with profile detection, DirectX/VC++ dependency checker, compatibility layers,
  CPU affinity/priority, graphics wrapper install with backup, config scanner/editor, controller detection,
  launch diagnostics with event-log entries, change journal with undo, copy-protection detection (detection only).
