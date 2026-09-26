# WARDOGS Radio

WARDOGS Radio is a Windows 10/11 audio-control desktop application for game voice and music routing. It uses a generic Station and Macro model: Cruise, Combat, Comms, and Emergency are editable first-run templates, not special code paths.

## First launch

Open **Setup Wizard** in the app. It guides device selection, Voicemeeter strip mapping, live signal checks, and a B1 listening test one step at a time. Device and route selections apply immediately and the wizard can restore routes it changed. Live meter observations and your listening/game-receive checks are required before setup is marked verified. The Diagnostics page stays separate and reports installed, connected, configured, and verified states distinctly. WARDOGS Radio does not alter driver-owned Windows endpoint names.

## Music Library, stations, and providers

**Music Library** is the reusable collection behind the radio. A **Source** is the original local media file or single YouTube video; a **Song** is a lightweight named start/end cue into that source. WARDOGS never downloads, copies, or creates a separate audio file for a cue. A song may be assigned to many stations, and editing it changes every station that uses it. Use the Library’s Source Archive to preview a compatible source, seek its source timeline, and split the containing cue without first creating a station. Multi-video YouTube playlists and non-seekable streams intentionally do not advertise source segmentation.

Stations are programmed channels: their playlists contain references to Library songs in station-specific order. **Remove from This Station** leaves the Library song and other stations intact; **Delete from Library** confirms and removes its references everywhere. The station editor’s **Add from Library** picker only shows provider-compatible cues and supports multi-select. A compatible new station/source receives an initial whole-source song so an active station does not look empty.

Stations are editable, ordered presets with a UUID, icon, accent, provider, source, enabled state, and on-air state. The station editor has a two-choice source switch: **Online Link** accepts a provider or direct-stream URL, while **Local Playlist** shows only local file-list controls. Add local files one at a time, in a batch, or from a folder; reorder or remove them, then choose **Play in Order** or **Shuffle / Reshuffle**. M3U/M3U8 files can be added as local playlist items. The Dashboard's far-right Playlist column shows the saved play order; double-click a song to play it, drag it to reorder, or press Shuffle to physically mix the order again. The timeline supports drag-to-seek and a following hover-time preview. Right-click a known-duration local track or single YouTube video to **Create New Song**: name the section before or after that point. Right-click an empty part of Playlist to split at the current playback time, or right-click a listed song to rename it and adjust its start/stop times. These song cues are stored once in the Music Library and reference the original media; no audio/video is copied or downloaded. A multi-video YouTube playlist cannot currently be split into named ranges. The Dashboard repeat button cycles through Off (stop at the end), Station (repeat the full playlist; default), and Track (repeat the current song). Previous restarts the current song if playback is past five seconds; otherwise it skips to the prior song. MPV can also be found in a standard WinGet user installation. The Audio & Routing page and Setup Wizard show live Voicemeeter strip/bus meters when its Remote API is connected. The wizard requires actual signal and owner receive checks before marking setup verified. SoundCloud and Apple Music sources are recognized but remain explicitly **Setup Required** until official API/MusicKit authorization is available; the application does not simulate readiness or embed credentials. yt-dlp is optional and is never the official YouTube path.

The YouTube surface uses one visible WebView2/IFrame player with transport and timeline updates from the official IFrame API. A separate process-specific Windows audio copy feeds the selected Voicemeeter music input for B1. When a YouTube station is active, the app can temporarily route Windows-default playback through Voicemeeter A1 for headset listening; it restores the previous output when leaving YouTube or closing. The B1 hold test temporarily mutes that A1 listening leg so only the B1 preview is heard. Other apps using the Windows default can be affected while that route is active. A recovery record is kept for interrupted sessions; do not mistake live meters for proof that a game accepted the B1 mix.

## Clip Guard / Output Health

**Audio & Routing** includes Output Health: live music-strip, microphone, and final B1/game-mix digital peaks in dBFS, headroom, short peak holds, a clip latch, session counters, and recent output events. **0 dBFS is the digital ceiling**; a 100% music slider is only a requested gain and does not itself prove that the final broadcast is safe. Music and microphone can combine at B1, so each can appear safe while the final mix is too hot.

Clip Guard has **Off**, **Monitor**, and **Protect** modes. Off and Monitor still show telemetry, warnings, peaks, and clip events. Protect may apply a temporary runtime reduction to WARDOGS' *separate game-music feed* after persistent unsafe B1 readings, then recovers slowly when the mix is safe. It never moves the Headset Master, Game Master, station, or microphone sliders. If no independent game-music feed or valid B1 telemetry is available, it reports monitor-only/protection-paused status rather than reducing the user's headphones or microphone. The optional **Use Voicemeeter Limiter** control safely leases the documented limiter on the selected music strip: it is off by default, requires readback verification, and restores the previous mixer limit when disabled or on clean exit. It does not replace B1 monitoring because music and microphone can still combine downstream.

Use **Broadcast Level Test** in Audio & Routing while playing a loud station and speaking normally. It observes real local Voicemeeter meter peaks without generating audio, rerouting devices, or changing levels. When B1 exceeds the configured ceiling, it offers an explicit one-click recommendation to lower the requested Game Master level; it explains the exact change before applying it. The test verifies WARDOGS' local Voicemeeter path only—the game can still apply downstream voice processing. See [Output Health](docs/OutputHealth.md) for the protection boundary and live-verification matrix.

## Modes, macros, and diagnostics

Enter or Space toggles play/pause while the main app window is focused on any page, except while typing in a text field. Selecting a playable station starts it. Player mode restores its saved track and position; Radio mode advances local-file playlists by elapsed time while tuned away when all track durations can be read. Restart Track mode restarts the last song at its beginning when returning. Sources without reliable durations fall back to the saved position with an on-screen warning. Headset, game, and microphone levels have separate live controls; changes save automatically. Crossfade and fade macros require compatible local/native stations.

Macros are editable named action definitions with multiple keyboard and controller bindings. The defaults are bound to F9–F12. New macros appear on the Dashboard by default; each macro has a visibility checkbox. Cycle Stations tunes the next enabled preset; Play Current Station and Pause Current Station control the already-selected station without requiring a station target. A Toggle macro can use different station actions for its on/off states. Macro health flags unavailable actions and routes instead of claiming they are ready. Controller discovery and binding capture are implemented, but physical HOTAS behavior has not been owner-verified. Diagnostics distinguishes detection from route verification. Exported Markdown reports may contain local file paths and device names; review before sharing. Settings/configuration are stored in `%LOCALAPPDATA%\WARDOGS Radio` with a last-known-good backup. The title-bar close button minimizes to tray; use the tray menu's **Exit** command to fully shut down and restore any temporary YouTube route.

## Install and updates

Current version: **v0.1.0**

Normal Windows users should download [WARDOGS-Radio-Setup-v0.1.0.exe](https://github.com/Kgray44/Wardogs_Radio/releases/download/v0.1.0/WARDOGS-Radio-Setup-v0.1.0.exe) from GitHub Releases. The per-user installer places the app in `%LOCALAPPDATA%\Programs\WARDOGS Radio` and creates a Start Menu shortcut to **WARDOGS Radio Launcher**. The launcher checks only official stable GitHub Releases, verifies the release manifest and installer SHA-256, and always starts the installed app if checking fails. It never requires an internet connection to use an installed copy.

The installer does not bundle or alter Voicemeeter, mpv, WebView2, account credentials, or provider setup. Existing WARDOGS Radio settings remain under `%LOCALAPPDATA%\WARDOGS Radio` and are deliberately separate from the installed program files.

## Development

Install the .NET 10 SDK on Windows, then run from the repository root:

```powershell
dotnet test WardogsRadio.sln -c Release
node tests/youtube-player.behavior.test.cjs
dotnet publish .\src\WardogsRadio.App\WardogsRadio.App.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\win-x64
```

Run `artifacts\win-x64\WARDOGS Radio.exe`. The [Build and test workflow](.github/workflows/build.yml) uploads the same Windows x64 output after automated checks. Voicemeeter, mpv, and WebView2 are detected at runtime; they are not redistributed in this repository. No owner music, credentials, personal settings, or local build artifacts are committed.
