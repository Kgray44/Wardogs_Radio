# Clip Guard / Output Health

Clip Guard consumes the Audio Bridge's sampled Banana microphone, music-input, and B1 game-bus levels. The app separately measures the Windows B1 endpoint and checks that the two observations agree before permitting automatic protection. It displays peaks as dBFS, with an amber peak-hold marker over each live meter, latches a clip indication, and records a small in-memory event trail. A missing or conflicting meter is unavailable for control; it is never treated as digital silence.

## Modes and controls

- **Off** records meter health only and applies no automatic gain.
- **Monitor** records health, peak holds, clip latch, and events without changing playback gain.
- **Protect** can add a temporary, reversible gain reduction only to WARDOGS' separate game-music player or YouTube game-audio feed.

Safe, Balanced, and Loud presets set a safety ceiling and near-clip warning threshold. The ceiling, maximum reduction, attack, recovery delay/rate, peak-hold duration, and clip-latch duration are persistent settings. Runtime reduction, current levels, peaks, latches, and events are session-only and are never saved as a user volume.

The Audio & Routing page includes a **Broadcast Level Test**. Start it while playing a loud station and speaking normally; it records the highest observed music, microphone, and B1 peaks from the real local meter paths. It produces no sound and changes no route. If the observed B1 peak exceeds the safety ceiling, the result provides an explicit, one-click recommendation to lower the requested Game Master level by a calculated dB amount. Applying it changes only that requested setting and leaves the runtime protection layer independent. The test verifies the local Voicemeeter path only; a game can still apply additional downstream voice processing.

## Protection boundary

The live protection path is deliberately narrow:

```
separate WARDOGS game-music player/feed -> temporary Clip Guard gain -> Voicemeeter music input -> B1
microphone strip --------------------------------------------------------------> B1
headset player ----------------------------------------------------------------> headphones
```

Clip Guard never moves the Headset Master, Game Master, station-volume, or microphone sliders. It composes a runtime multiplier after the user-selected game-music level. If a separate game-music player/feed is not active, the app reports monitor-only status and does not reduce headphone or microphone audio to compensate.

The selected Voicemeeter music strip can optionally use its documented `Strip[i].Limit` brickwall limiter. It is deliberately **off by default**: enabling **Use Voicemeeter Limiter** reads the exact selected strip, writes the configured ceiling only after that read succeeds, verifies the write by reading it back, and retains the prior mixer value in memory. Disabling it—or a clean WARDOGS exit—restores that prior value. If the edition, connection, or selected strip cannot expose a readable limiter, WARDOGS leaves the mixer untouched and reports the capability as unavailable. This limiter protects the music strip; B1 protection still relies on the separate runtime game-music gain because microphone and music can sum after the strip.

## Manual verification matrix

| Check | Expected evidence | Owner-live status / boundary |
| --- | --- | --- |
| Music only, low level | `SAFE` or `HEALTHY`; zero runtime reduction | Pending owner-live verification; verify actual B1 meter separately |
| Music near ceiling | `HOT` / `NEARCLIP`, peak hold, event | Pending owner-live verification; does not prove a game received B1 |
| Brief clip | clip latch and event; no large persistent reduction | Pending owner-live verification; requires a transient only |
| Sustained overload with separate game feed | `PROTECTED`; game-music runtime reduction rises then recovers after delay | Pending owner-live verification; headphone and mic controls stay unchanged |
| Music plus microphone | B1 rises and can report combined-mix overload | Pending owner-live verification; music is the only automatic attenuation target |
| Local crossfade | final B1 meter continues and protection stays active | Pending owner-live verification |
| Macro game-gain change | requested gain changes while protection remains downstream | Pending owner-live verification |
| Voicemeeter disconnect/reconnect | `UNAVAILABLE`; runtime gain returns neutral; no crash; metering resumes on reconnect | Pending owner-live verification |
| No separate game feed | monitor-only message, zero automatic gain | Source-tested; prevents accidental headset/mic ducking |
| YouTube/local playback | same B1 health telemetry; protection only if that provider has a separate game feed | Playback success and physical audibility need owner confirmation |

Use Diagnostics to capture the mode, thresholds, B1 state, runtime reduction, protection-path availability, and recent events. Diagnostic reports intentionally contain no credentials, browser data, or audio samples.

The manual rows are a verification plan, not a claim that this machine's physical paths have been exercised. Record the observed result for low music, near-ceiling music, clipping, music plus microphone, a crossfade, macro gain changes, and a Voicemeeter disconnect before making a release/owner-acceptance claim.
