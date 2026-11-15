# Changelog

All notable changes to Legacy Game Launcher. Versions match the GitHub release tags (`vX.Y.Z`).
Earlier versions (0.1–0.4.3) were built before the project was on GitHub; their changes are listed here
and included in the first GitHub release.

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
