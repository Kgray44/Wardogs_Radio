# WARDOGS Radio v0.3.2

## Clear disconnected-device warnings

If the selected microphone or listening output is unplugged, a separate red warning appears at the top of every page. The warnings gently pulse when Windows animations are enabled, open Setup & Repair when clicked, and disappear when each device returns. A missing mic or listening output now prevents the GAME VOICE header status from saying READY.

The warnings use the same live device inventory and shared readiness result as Setup and Diagnostics. This release does not change audio routing, saved devices, station playback, or mixer levels.

The v0.3.1 updater timeout repair is included. Install v0.3.2 manually once when upgrading from v0.3.0 or earlier, because those older launchers can time out before downloading the installer. The installer preserves settings in `%LOCALAPPDATA%\WARDOGS Radio`.

## Verification

The focused readiness tests and Windows app build passed. A standalone preview was opened on the owner machine. Physical unplug/replug behavior and passenger-side game audio were not independently verified in this release.

## Full changes

Compare: [v0.3.1 → v0.3.2](https://github.com/Kgray44/Wardogs_Radio/compare/v0.3.1...v0.3.2)
