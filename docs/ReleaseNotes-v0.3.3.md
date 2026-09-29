# WARDOGS Radio v0.3.3

## Listening output selection and verification

Setup & Repair and Audio & Routing now share one saved Windows listening output. Local playback, YouTube routing, diagnostics, and readiness use that selection. The MPV output is resolved from the selected endpoint; if a mapping or switch fails, the chosen Windows device remains saved so it can be retried after reconnecting or refreshing devices. Ambiguous MPV matches are rejected instead of silently choosing an output.

Setup's test sound now plays through MPV on the selected output. Verification checks the MPV device, active WASAPI output, and signal at the chosen Windows endpoint. Readiness and Advanced Diagnostics distinguish a disconnected endpoint from mapping, switch, and playback problems.

The installer preserves settings in `%LOCALAPPDATA%\WARDOGS Radio`. Install v0.3.3 manually once when upgrading from v0.3.0 or earlier, because those older launchers can time out before downloading the installer.

## Verification

The .NET solution build, 317 automated tests, and YouTube behavior test passed before release. The Windows release workflow builds and tests the installer, runs startup and uninstall smoke checks, and verifies the published updater assets. Audible playback, WPF navigation, and unplug/replug behavior on the owner's headset were not independently tested.

## Full changes

Compare: [v0.3.2 → v0.3.3](https://github.com/Kgray44/Wardogs_Radio/compare/v0.3.2...v0.3.3)
