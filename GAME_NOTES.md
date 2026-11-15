# Rugby 08 and Cricket 07: research notes (November 2026)

The automated fetches were blocked by PCGamingWiki, the EA forums and Vogons, so these notes rely
on search-result summaries of those pages, GitHub projects and community forums. Each fact below
is labelled with how solid it is. The launcher checks the things it can (Direct3D version, input
API, copy-protection markers) against your actual exe instead of trusting the profile.

## Cricket 07 (HB Studios / EA Canada, 2006)

| Fact | Confidence | Source |
|---|---|---|
| Exe is `Cricket07.exe` in `C:\Program Files (x86)\EA SPORTS\EA SPORTS(TM) Cricket 07\` | **Confirmed** | Your crash report; cricket07-browser project |
| **Direct3D 8** (not 9), shaders vs/ps 1.1 | High | github.com/Dhaiwat10/cricket07-browser |
| Input through **legacy DirectInput (`dinput.dll`)**, audio through DirectSound | High | cricket07-browser; Hybrid Analysis strings; your crash in DINPUT.dll |
| Settings in a **binary** profile: `%USERPROFILE%\Documents\EA SPORTS(TM) Cricket 07\<user-id>.pro` | High | PCGamingWiki; planetcricket/megacricketstudio guides |
| Retail disc uses **SafeDisc 4**, which can't run on Windows 10/11 (secdrv.sys disabled) | High | PCGamingWiki; GameCopyWorld; cricket07-browser |
| Fixes reported to work: Windows 7 compatibility mode, run as admin, dgVoodoo2 (some graphical glitches). dxwrapper crashed for one user. Windows 10 worked but Windows 11 crashed for another. | Anecdotal | megacricketstudio; Vogons; EA Forums |
| Widescreen only through a community mod (retrobone/EACricketWidescreenFix) | High | GitHub; PCGamingWiki |
| No official DRM-free or digital re-release | As far as found | EA Forums / no store listing found |

## Rugby 08 (HB Studios, 2007)

| Fact | Confidence | Source |
|---|---|---|
| Exe is `Rugby08.exe` under `Program Files\EA SPORTS\…` | High | Several install listings |
| **Direct3D 8** | Medium (inferred) | The PCGamingWiki community widescreen fix uses dgVoodoo2's `d3d8.dll` |
| Same engine family as Cricket 07 | Medium | Same developer and era |
| Settings in a **binary** `.pro` profile in `Documents\EA SPORTS(TM) Rugby 08\` (forums write "EA SPORTS™ Rugby 08") | High | therugbyforum.com resolution threads |
| Resolution stored as two **little-endian 16-bit values, width then height** (1024x768 = `00 04 00 03`, 1920x1080 = `80 07 38 04`). The offset differs between profiles (users report 636, 638 and 658). Changing display options in the game can reset it to 1024x768. | High | therugbyforum.com (several users confirmed) |
| Listed as using **SafeDisc** (version not given) | Medium | PCGamingWiki SafeDisc page |
| Freezes at the start-up screen, sometimes caused by antivirus | Anecdotal | therugbyforum.com |
| PC controls are laid out for a **Logitech Rumblepad 2** (DirectInput): works "with no need to remap anything"; an Xbox 360 pad gets mixed-up buttons (sprint, button 8, lands on Start) | Medium (several forum users) | therugbyforum.com "PC Rugby 08 advice" |
| On PC, Start/Back aren't used - pause with **Esc**; special moves are **L1 + a face button** instead of the right stick | Medium (forum review) | therugbyforum.com "2008 impressions" |

## Changes made to the launcher from this research

- **Direct3D 8 wrapper support.** The wrapper now installs `d3d8.dll` for Direct3D 8 games
  (dgVoodoo2 `MS\x86\D3D8.dll`; for DXVK 2.4+, `x32\d3d8.dll` plus `d3d9.dll`). Before, it only
  installed `d3d9.dll`, which these games never load.
- **dinputto8 option** on the Controllers tab, for legacy-DirectInput crashes. You download it yourself; it's copied with backup and restore.
- **Crash advice** taken from the event log's "Faulting module name", for example DINPUT.dll → unplug HID devices, then try dinputto8.
- **`.pro` resolution editor.** It searches the binary profile for a known resolution pair and edits only
  when there's exactly one match. The original is backed up and can be undone.
- Profiles now have the confirmed exe and folder names and list their sources.
  The CPU-core limit is kept as a precaution and is marked as not sourced. Priority is back to Normal.

## Assumed and not found

Registry keys for either game, any official patch for Windows 10/11, and any confirmed CPU-core
or high-refresh-rate problem specific to these games.
