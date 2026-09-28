# WARDOGS Radio v0.3.0

## Highlights

- Reworked Setup & Repair into a wider, simpler path for choosing your microphone and listening output, connecting game voice, and checking the result. Technical controls remain under Advanced.
- Added a Banana Audio Bridge that checks mixer connection, routes, and live output meters, reports recoverable faults, and verifies changes before treating them as ready.
- Improved local and YouTube playback routing. YouTube now follows the selected listening output during an active song without changing the Windows default device, while its game copy continues to B1.
- Added a distinct game-output check tone and a four-second Live Protection Check in Audio & Routing.
- Updated Clip Guard Protect to lower both listening music and game music when the trusted B1 mix reaches the selected safety ceiling. Its temporary gain does not change saved volume sliders or microphone level.
- Reduced false readiness warnings from momentary disagreement between the Voicemeeter and Windows B1 meters. Clip Guard still pauses immediately while those readings disagree.

## Upgrade and verification

The installer preserves settings in `%LOCALAPPDATA%\WARDOGS Radio`. Automatic game-voice routing requires Voicemeeter Banana. In your game or voice app, choose **Voicemeeter Out B1** as the microphone/input device to receive the WARDOGS mix. WARDOGS' local meters and test tone cannot confirm that another app selected or accepted that input.

Clip Guard intervenes only when its live B1 reading reaches the configured safety ceiling and both meter sources agree. The Live Protection Check temporarily lowers the trigger for four seconds to make a reduction easier to hear, then restores the usual trigger. The optional Voicemeeter limiter remains separate and off by default.

On the owner machine, the revised check reported and read back a 100% to 50% listening gain change and a 50% to 25% effective game-path change, but the owner did not hear a clear headphone dip. The audible effect and downstream game reception therefore remain unverified; Clip Guard can be revisited if a clipping issue is observed after release.

## Full changes

Compare: [v0.2.4 → v0.3.0](https://github.com/Kgray44/Wardogs_Radio/compare/v0.2.4...v0.3.0)
