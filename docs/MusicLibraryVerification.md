# Music Library verification matrix

This matrix distinguishes automated coverage from live owner verification. Passing tests or a green build do not prove a browser video, headset path, game route, or visual layout.

| Scenario | Automated evidence | Live verification still required |
|---|---|---|
| Create a single-video YouTube station | Migration/service tests create a whole-source cue and playlist projection | Tune it, confirm the visible player, timeline, and non-empty Dashboard playlist |
| Split a compilation | Library split tests preserve source identity and boundaries | Split through the Dashboard and Source Archive; confirm no media file appears |
| Reuse a cue | Shared-song and membership tests cover references | Add it to two stations and listen through each boundary |
| Edit a cue | Projection tests cover canonical timing/name updates | Confirm both stations use the new boundary after retuning |
| Remove from station | Service tests preserve the Library song | Remove from Dashboard and ensure another assigned station remains unchanged |
| Delete song/source | Dependency-cleanup tests cover references | Verify confirmation wording and active-player behavior |
| Source Archive | Release build covers WPF integration | Preview a local source and a permitted embedded YouTube video; seek and create a cue |
| Library-first workflow | Source/cue service tests cover no-station ownership | Add a source, create several cues, then build stations with Add from Library |
| Existing v0.1.0 configuration | Migration tests cover names, source identity, ranges, and order | Open a backed-up real v0.1.0 configuration and confirm its stations play |
| Repeat, next, previous, reorder, shuffle | Existing playlist/timeline tests and resolver tests | Exercise both local and single-video YouTube playlists at cue boundaries |

Before any release candidate review, record the date, source type, output device, and observed result for every live row. No release is published automatically.
