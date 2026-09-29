# Listening History and privacy

Listening History is local user data. WARDOGS writes it to
`%LocalAppData%\WARDOGS Radio\Listening\history-v1.jsonl`, separately from
`config.json`. It does not upload history, use an analytics service, or include
song-by-song history in general diagnostic exports. Search previews, Library
source/cue previews, setup tones, B1 auditions, and silent Radio Mode progression
do not enter the station Listening recorder.

The app records an audible interval for the selected station and canonical
Library song/source. Audibility requires a playing active station, a ready
listening route, and effective headset gain above 0.5%. A qualified play needs
15 audible seconds across pauses. A user advance after qualification is a skip;
a natural cue boundary or provider advance is a completion. Station switching,
seeking to another cue, and shutdown close the old interval without calling it
a skip. During a crossfade, the outgoing active station owns listening time
until the handoff; the new station owns it afterward. This avoids counting two
wall-clock seconds during one second of overlap.

The journal records closed intervals and meaningful lifecycle events, not
per-frame counters. A small atomic checkpoint is refreshed about every 30
seconds while playing. After a crash, startup accepts only time up to that
checkpoint and deduplicates it against the journal. Names are stored with
historical entries so deleting a Library song or station does not erase or
break old statistics. Statistics are derived from the journal when opened.

Use **Listening → Store listening history on this computer** to pause future
recording. **Clear Listening History** requires confirmation and removes the
journal and checkpoint while keeping sources, songs, stations, and settings.
Complete `.wradio` backups include history. Selective transfer packages expose
an explicit Listening History checkbox. YouTube Search keys are not included in
history, configuration, backups, or diagnostic exports. The optional user key
is stored separately using Windows user protection; the default key is bundled
in packaged builds.
