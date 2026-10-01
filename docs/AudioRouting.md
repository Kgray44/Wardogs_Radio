# Audio routing

WARDOGS Audio Bridge owns the supported Voicemeeter Banana connection and route changes. Banana mixes the microphone and music sent to game voice; WARDOGS does not combine microphone PCM with music itself. The bridge attempts to start Banana automatically and reports a wrong running edition instead of switching it behind the owner's back.

The paths are intentionally separate:

- Microphone → Voicemeeter physical input strip → B1 game-voice bus.
- Local file → MPV headset player → selected Windows headphones, plus a second synchronized MPV game player → selected Voicemeeter virtual input → B1.
- Visible YouTube player → selected physical listening endpoint through a Windows per-app audio-session route, plus a process-specific audio capture copy → selected Voicemeeter music input → B1. The app records the prior per-app policy and session volume before changing them. This candidate does not set the Windows global default for new YouTube sessions.

## Canonical listening output

`AppConfiguration.MonitorDeviceId` is the one saved listening selection: a Windows render-endpoint ID. `MonitorDeviceName` preserves its label while unplugged. `MpvAudioDeviceName` is only the last resolved MPV output ID, paired with `MpvAudioDeviceEndpointId` so it cannot be reused for a different Windows selection. `GameMpvAudioDeviceName` is the separate Voicemeeter game-feed output; it must not equal the listening player output.

Setup & Repair and Audio & Routing project their selected item from `MonitorDeviceId`. Selecting either control saves the requested endpoint, resolves its MPV WASAPI ID, switches an active player, reads back the selected player device, and refreshes readiness. A failed mapping or switch leaves the Windows choice saved and visible, pauses playback that might otherwise continue on an old device, and reports a repair state. YouTube's per-app route consumes that same Windows endpoint ID. A disconnected selection remains visible until it returns or is replaced.

`ListeningOutputResolver` tries a saved player ID only when it was paired with the selected endpoint, then a matching endpoint GUID, then a unique normalized description among Windows render endpoints and WASAPI player devices. Ambiguous descriptions fail closed. Refresh Device Lists enumerates both Windows endpoints and MPV devices and reruns resolution. Advanced Diagnostics records the selected endpoint, every player ID and description, resolution strategy, switch result, and signal verification.

Setup's Play Test Sound plays a generated WAV through the same `MpvProvider` class and resolved player ID used by local stations. It checks MPV's selected device and active WASAPI output, then observes signal at the selected Windows endpoint. The owner still confirms hearing it. Setup cannot be marked verified from a Windows-only test tone.

The five-step Setup & Repair flow chooses microphone and headphones, connects the supported path, verifies live signals, and records the owner's listening and game-reception confirmations. Route writes are read back before success; temporary bridge leases are recorded before a change, and recovery restores only a value that still matches what WARDOGS applied. Setup offers Keep Changes or Undo Changes when leaving with uncommitted wizard changes. Audio & Routing keeps the detailed device and route view. Its B1 hold-to-test previews the actual B1 mix in the selected headphones while the direct station listening leg is temporarily muted. Keep headphone volume comfortable before testing.

Dashboard signal bars distinguish Voicemeeter strip/bus signal from Windows headset/B1 endpoint signal. A moving B1 bar confirms signal at that measured point, not that a game or voice-chat app has selected Voicemeeter Out B1 as its microphone. That receiver must still be checked separately. The app does not install/configure Voicemeeter's audio driver automatically or claim route verification based only on saved device names. A saved Setup verification survives restart only while its component identities and current routes still match; a live meter conflict blocks Ready.

The current YouTube listening route identifies the WebView2 audio session, applies the selected endpoint for that process, and verifies that the session moved. Its separate process-loopback game feed is controlled independently. The older global-default route is retained only to restore an interrupted legacy candidate session at startup. Windows per-app endpoint policy uses an undocumented interface and still requires owner-live validation for sound quality, active device switching, restart, and recovery. See [candidate acceptance](ReadinessAudioBridgeCandidate.md) for those checks.

## v0.4.1 device identity and startup preference

Physical microphone names use one complete-name resolver for known WDM/MME/KS/ASIO
presentation prefixes, whitespace and case. A driver prefix constrains the match;
zero or multiple candidates cannot authorize a write. New automatic assignments
prefer WDM. Recovery can restore positively identified WDM, MME or physical KS
assignments through their actual driver. ASIO prior-strip recovery and KS output
selection are outside this contract. The legacy shared-MME output selector's
specific truncated-name rule remains separate from physical microphone identity.

Before reusing a saved automatic strip, WARDOGS checks whether its current device
matches the recorded applied or prior device. An outside change is left untouched.
Device, route and gain leases persist before writes; readback and conservative
rollback remain required. Failed dropdown changes restore the saved selection and
report the failure. Advanced Diagnostics includes identity resolution evidence.

Settings offers Last WARDOGS Devices (the default for existing configurations) or
Current Windows Default Devices. The latter reads Core Audio Multimedia capture
and render defaults once at startup, maps their stable IDs to the Windows inventory,
then uses the existing microphone and listening reconciliation paths. It never
continuously follows default changes. Virtual capture defaults are refused to avoid
feeding game output back into a physical input. Unavailable or unsafe changes
retain previous selections with a targeted repair state. Unchanged devices avoid
new assignments, and only evidence for a changed device becomes stale.

The compact Now Playing visualizer reads the actual selected listening endpoint
for direct local/YouTube playback, rather than prioritizing its separate game-feed
strip. Pause, loading and unavailable trusted meters return its target to zero;
attack/decay and Reduce Motion presentation remain intact. An endpoint meter can
also see other applications sharing that output; it is not isolated station PCM.

See [v0.4.1 candidate audit and acceptance](V0.4.1-CandidateAudit.md).
