# WARDOGS Radio v0.2.0

A major feature update focused on making WARDOGS Radio feel like a complete, persistent airborne radio and audio-control system.

## Highlights

- A persistent Now Playing control surface with compact and expanded modes, transport, seeking, station switching, and live activity feedback.
- A reusable Music Library built around sources and named song cues, so stations can share media without copying it.
- Safer, clearer audio routing through expanded Voicemeeter diagnostics, output-health monitoring, and Clip Guard protections.
- More dependable local and YouTube playback, including consistent requested gain and recovery-aware station transitions.
- Backup and restore for WARDOGS Radio configuration and library data.

## What's New

### Persistent Now Playing control surface

Now Playing is available throughout the app as a compact military-radio strip. It keeps the tuned station, track information, playback progress, transport controls, station chooser, and live visualizer in reach without returning to the Dashboard. Open it into an expanded drawer for a larger, focused playback surface; it preserves the same live state, timeline, and controls.

### Reusable Music Library and song cues

Music Library now separates original media sources from named songs. A song is a reusable, non-destructive cue range into a local file or compatible YouTube source, and can belong to more than one station. Edit a source or song once and its station references stay synchronized. The library includes source inspection, station-membership management, compatible-source selection, and safe cue creation from the source timeline.

### Backup and restore

Create a portable WARDOGS Radio backup for configuration and library data, review what is included, and restore it through a validated import path. Restore planning protects relationships between stations, sources, songs, and macros before changes are applied.

## Music Library & Station Improvements

- Added reusable local and online media sources, shared song definitions, and named source segments.
- Added non-destructive start/end cue ranges with logical playback timelines and correct translation to the underlying source position.
- Improved station/library integration, including compatibility-aware song selection, source inspection, and membership management.
- Extended playlist behavior for repeat modes, direct song selection, ordered or shuffled playback, and source-aware seeking.

## Now Playing & Playback

- Added Previous, Play/Pause, Next, repeat, station selection, progress seeking, and live visualizer behavior to the persistent player.
- Added compact and expanded layouts that protect essential controls at narrower window sizes and avoid exposing raw source paths or URLs as playback metadata.
- Improved synchronization between local mpv and YouTube presentation states during loading, recovery, transitions, and station changes.
- Calibrated local mpv volume requests to its cubic control curve so the requested local station level behaves linearly and more closely matches the rest of WARDOGS Radio.
- Hardened YouTube player initialization, route recovery, and listening-gain behavior so temporary routing and visible playback stay coordinated.

## Audio & Routing

- Added Output Health with live music-strip, microphone, and final B1/game-mix telemetry, peak holds, headroom, clip events, and confidence reporting.
- Added Clip Guard modes that can monitor or protect the dedicated game-music feed without changing the listener, microphone, or station sliders.
- Added a broadcast-level observation workflow and optional, verified Voicemeeter strip limiter lease.
- Expanded diagnostics with raw Voicemeeter data, exportable forensic detail, selected source/strip reporting, and explicit output-telemetry mismatch reporting.

## UI / UX Improvements

- Refined the persistent dock with floating-surface treatment, protected control zones, keyboard access, compact metadata, and a bottom-left operational information card.
- Added more informative loading, route, and signal states so unavailable or recovering audio is not presented as ready.
- Updated application and installer artwork.

## Fixes & Reliability

- Fixed several YouTube playback presentation and routing recovery cases, including clean transitions between local and online stations.
- Improved setup and playback teardown so paused stations are not handled as actively playing.
- Added explicit protection boundaries when Voicemeeter and Windows output telemetry disagree.
- Improved route-aware local playback handling and stable timing around player initialization.

## Under the Hood

- Added tested domain models for source/library relationships, playback presentation, logical timelines, compact layout geometry, output-health calculations, and backup package validation.
- The release pipeline now selects the release-notes document from the version being published, preventing a future release from reusing v0.1.0 notes.

## Upgrade Notes

No manual migration is required. Existing WARDOGS Radio settings remain in `%LOCALAPPDATA%\WARDOGS Radio`; the installer upgrades the application in place. Creating a backup before any update is still recommended.

## Known Limitations

SoundCloud and Apple Music remain setup-dependent until owner-managed official authorization is configured. Live Voicemeeter meters confirm WARDOGS Radio's local routing path, but cannot prove that a downstream game accepted the B1 mix.

## Full Changes

Compare: [v0.1.0 → v0.2.0](https://github.com/Kgray44/Wardogs_Radio/compare/v0.1.0...v0.2.0)
