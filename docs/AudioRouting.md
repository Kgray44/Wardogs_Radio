# Audio routing

Voicemeeter owns the microphone/music mix sent to game voice. WARDOGS controls selected devices and route buttons; it does not combine microphone PCM with music itself.

The paths are intentionally separate:

- Microphone → Voicemeeter physical input strip → B1 game-voice bus.
- Local file → MPV headset player → selected Windows headphones, plus a second synchronized MPV game player → selected Voicemeeter virtual input → B1.
- Visible YouTube player → temporary Voicemeeter VAIO/A1 headset path, plus a process-specific audio capture copy → selected Voicemeeter music input → B1. The Windows-default playback device is temporarily changed during this route and restored on exit or recovery; other apps using the default can be affected.

The Audio & Routing page places the actual device selectors at the path points. Device/route changes apply immediately and save automatically. The Setup Wizard presents the same path one step at a time and provides an Undo Wizard Routes control for changes it made. Its B1 hold-to-test previews the actual B1 mix in the selected headphones while the direct station listening leg is temporarily muted. Keep headphone volume comfortable before testing.

Dashboard signal bars distinguish Voicemeeter strip/bus signal from Windows headset/B1 endpoint signal. A moving B1 bar confirms signal at that measured point, not that a game or voice-chat app has selected Voicemeeter Out B1 as its microphone. That receiver must still be checked separately. The app does not install/configure Voicemeeter's audio driver automatically or claim route verification based only on saved device names.
