# Audio routing

WARDOGS Audio Bridge owns the supported Voicemeeter Banana connection and route changes. Banana mixes the microphone and music sent to game voice; WARDOGS does not combine microphone PCM with music itself. The bridge attempts to start Banana automatically and reports a wrong running edition instead of switching it behind the owner's back.

The paths are intentionally separate:

- Microphone → Voicemeeter physical input strip → B1 game-voice bus.
- Local file → MPV headset player → selected Windows headphones, plus a second synchronized MPV game player → selected Voicemeeter virtual input → B1.
- Visible YouTube player → selected physical listening endpoint through a Windows per-app audio-session route, plus a process-specific audio capture copy → selected Voicemeeter music input → B1. The app records the prior per-app policy and session volume before changing them. This candidate does not set the Windows global default for new YouTube sessions.

The five-step Setup & Repair flow chooses microphone and headphones, connects the supported path, verifies live signals, and records the owner's listening and game-reception confirmations. Route writes are read back before success; temporary bridge leases are recorded before a change, and recovery restores only a value that still matches what WARDOGS applied. Setup offers Keep Changes or Undo Changes when leaving with uncommitted wizard changes. Audio & Routing keeps the detailed device and route view. Its B1 hold-to-test previews the actual B1 mix in the selected headphones while the direct station listening leg is temporarily muted. Keep headphone volume comfortable before testing.

Dashboard signal bars distinguish Voicemeeter strip/bus signal from Windows headset/B1 endpoint signal. A moving B1 bar confirms signal at that measured point, not that a game or voice-chat app has selected Voicemeeter Out B1 as its microphone. That receiver must still be checked separately. The app does not install/configure Voicemeeter's audio driver automatically or claim route verification based only on saved device names. A saved Setup verification survives restart only while its component identities and current routes still match; a live meter conflict blocks Ready.

The current YouTube listening route identifies the WebView2 audio session, applies the selected endpoint for that process, and verifies that the session moved. Its separate process-loopback game feed is controlled independently. The older global-default route is retained only to restore an interrupted legacy candidate session at startup. Windows per-app endpoint policy uses an undocumented interface and still requires owner-live validation for sound quality, active device switching, restart, and recovery. See [candidate acceptance](ReadinessAudioBridgeCandidate.md) for those checks.
