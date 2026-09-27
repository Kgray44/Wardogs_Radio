# Audio routing

WARDOGS Audio Bridge owns the supported Voicemeeter Banana connection and route changes. Banana mixes the microphone and music sent to game voice; WARDOGS does not combine microphone PCM with music itself. The bridge attempts to start Banana automatically and reports a wrong running edition instead of switching it behind the owner's back.

The paths are intentionally separate:

- Microphone → Voicemeeter physical input strip → B1 game-voice bus.
- Local file → MPV headset player → selected Windows headphones, plus a second synchronized MPV game player → selected Voicemeeter virtual input → B1.
- Visible YouTube player → temporary Voicemeeter VAIO/A1 headset path, plus a process-specific audio capture copy → selected Voicemeeter music input → B1. The Windows-default playback device is temporarily changed during this route and restored on exit or recovery; other apps using the default can be affected.

The five-step Setup & Repair flow chooses microphone and headphones, connects the supported path, verifies live signals, and records the owner's listening and game-reception confirmations. Route writes are read back before success; temporary bridge leases are recorded before a change, and recovery restores only a value that still matches what WARDOGS applied. Setup offers Keep Changes or Undo Changes when leaving with uncommitted wizard changes. Audio & Routing keeps the detailed device and route view. Its B1 hold-to-test previews the actual B1 mix in the selected headphones while the direct station listening leg is temporarily muted. Keep headphone volume comfortable before testing.

Dashboard signal bars distinguish Voicemeeter strip/bus signal from Windows headset/B1 endpoint signal. A moving B1 bar confirms signal at that measured point, not that a game or voice-chat app has selected Voicemeeter Out B1 as its microphone. That receiver must still be checked separately. The app does not install/configure Voicemeeter's audio driver automatically or claim route verification based only on saved device names. A saved Setup verification survives restart only while its component identities and current routes still match; a live meter conflict blocks Ready.

The temporary YouTube listening route still changes the Windows default Console and Multimedia render outputs because the existing WebView playback path depends on it. It stores a recovery record before switching and checks the current Windows and mixer values before restoring. If another program changes those values, WARDOGS leaves them alone and reports attention. The process-specific YouTube game feed remains separate from the direct headset playback path. See [candidate acceptance](ReadinessAudioBridgeCandidate.md) for hardware checks and current limits.
