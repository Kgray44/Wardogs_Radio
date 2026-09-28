# WARDOGS Radio v0.3.1

## Updater repair

The launcher now gives the small release manifest an eight-second deadline and the verified installer download a separate 30-minute deadline. Previous launchers applied an eight-second limit to both steps, causing large installer downloads to fail and reopening the old version. Download failures now include the exception detail in `launcher.log`.

Install this release manually once when upgrading from v0.3.0 or earlier. The older launcher cannot update its own timeout before it has downloaded an installer. The installer preserves settings in `%LOCALAPPDATA%\WARDOGS Radio`.

## Game voice investigation

On the owner machine, v0.2.2 and v0.3.0 both produced live, unclipped 48 kHz stereo signal on the exact Voicemeeter Out B1 device selected in the game's audio settings. The game's voice connection was reported as connected. This local check does not establish what other players hear after game voice processing and transmission; the reported static remains unverified downstream. No game audio settings or Voicemeeter routes are changed by this update.

## Full changes

Compare: [v0.3.0 → v0.3.1](https://github.com/Kgray44/Wardogs_Radio/compare/v0.3.0...v0.3.1)
