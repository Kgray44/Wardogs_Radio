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
