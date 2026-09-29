# WARDOGS Radio v0.4.0

## YouTube discovery

Search YouTube for videos or playlists inside WARDOGS, preview results, and add a selected video to the Library or station. Search includes a built-in shared YouTube Data API key. Settings → Music Services shows its status and lets you test the connection, optionally use a masked custom key, or restore the built-in default. Search results are cached for the session. Paste-link entry remains available if Search encounters a quota, authorization, or network error.

## Listening History

The Listening page shows audible radio time, favorite stations, most-played and most-listened songs, and recent plays over selectable periods. History stays local and can be paused or cleared. Silent progression, paused playback, and previews do not count toward listening time.

## Scrolling

Library lists, menus, and dropdowns now use native wheel handling with smaller pixel-based movement. The list remains virtualized for large libraries. Automated WPF checks cover wheel and trackpad-like input in a Library-style list, page, dropdown, and context menu.

## Installing

The installer preserves settings in `%LOCALAPPDATA%\WARDOGS Radio`. Install v0.4.0 manually once when upgrading from v0.3.0 or earlier, because those older launchers can time out before downloading the installer.

## Verification

The Windows release workflow builds and tests the app and installer, runs startup and uninstall smoke checks, and verifies published updater assets. Physical trackpad behavior and audible playback on the owner's devices require owner review.

## Full changes

Compare: [v0.3.3 → v0.4.0](https://github.com/Kgray44/Wardogs_Radio/compare/v0.3.3...v0.4.0)
