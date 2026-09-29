# Music Library Architecture

## Purpose

WARDOGS Radio treats a song as a non-destructive cue into one playable media
source. A cue never creates, downloads, copies, transcodes, or slices media.

```text
Media source → library song → station playlist entry → playback
```

## Schema 10 migration

The v0.1.0 configuration stored `Station.PlaylistSongs` directly on each
station. Schema 10 creates canonical `MusicLibrary.Sources` and
`MusicLibrary.Songs`, then establishes each station's ordered
`PlaylistEntries` as its library membership. The former station song list is
currently retained as a compatibility projection for provider adapters; it is
materialized from canonical records before playback and is not edited as
metadata authority.

The migration is conservative:

- Existing station order, names, source identities, start/end boundaries, and
  playlist order are retained.
- Exact equivalent legacy cues may share one canonical song; different cues
  are never merged just because their names match.
- YouTube identities are deduplicated by video ID; local paths are normalized
  case-insensitively for Windows.
- A compatible station with a single source and no named ranges gains one
  whole-source song (`0 → end of source`) so it never presents an empty
  playlist while it is playing.
- Multi-video YouTube playlists remain unsegmented until there is a reliable
  per-video source model. The app must not present such a playlist as one
  seekable source.

Invalid references are removed during normalization. A missing source never
causes an invented replacement or a crash.

## Playback boundary

`MusicLibraryService.Resolve` produces transient `ResolvedPlaylistSong`
instances. The runtime join contains the station-entry ID, song ID, source ID,
source provider and identity, and cue boundaries. It is not persisted.

The mpv and YouTube paths must consume those resolved items. mpv still loads
one physical source instance per station entry when ranges from the same media
need independent queue positions; YouTube continues to receive its segment
list. In both cases the reusable timing/name metadata is owned only by the
library song.

## User-facing distinction

- **Library** answers “what music exists?” Sources are the original media and
  Songs are reusable named timing references.
- **Station playlist** answers “what does this station play, and in what
  order?” Reordering or removing an entry affects only that station.
- **Delete from Library** removes the canonical song and all of its station
  entries after confirmation. Removing from one station leaves the song and
  every other assignment intact.

## YouTube discovery

`IMediaDiscoveryProvider` returns typed video or playlist candidates. The WPF Search
window calls `YouTubeDiscoveryProvider` only after the user presses Enter or Search.
Search uses the official YouTube Data API (`search.list`, then `videos.list` for
video durations) with an API key supplied for the current app session. The key is
held only in memory and never written to configuration, logs, diagnostics, or
`.wradio` packages. Search results are cached in memory for that app session.

Selection goes through `MediaDiscoveryIngestion` and `MusicLibraryService.EnsureSource`.
Video identities normalize to `youtube.com/watch?v=<id>` even when discovered
through `youtu.be`, `music.youtube.com`, or a watch URL with tracking and playlist
context. Playlist identities normalize to `youtube.com/playlist?list=<id>`.
One video can own many non-destructive song cues; a multi-video playlist is a
source collection and does not gain a fake single-media cue timeline. The active
YouTube station editor accepts either kind. A YouTube station playlist can add a
searched video directly; playlist results guide the user to a station source.

Preview uses an isolated visible WebView2 player while normal station playback is
stopped. Closing Search navigates that preview to `about:blank`. It does not
create Library records or feed the Listening recorder.
