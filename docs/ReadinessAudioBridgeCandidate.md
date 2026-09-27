# Readiness and Audio Bridge candidate

This document describes the unreleased owner-review candidate. It does not record a completed hardware acceptance run.

## Supported path

WARDOGS supports Voicemeeter Banana with physical microphone strips 0–2, VAIO strip 3, AUX strip 4, A1 listening, and B1 game voice. Potato and Standard are not accepted for automatic routing. The installer checks for Banana; the runtime attempts to start Banana through the Remote API when no engine is running. It will not replace another running edition.

The Audio Bridge owns connection state, route/device mutations, readback, fault records, and mixer signal sampling. Setup, macros, and Diagnostics request behavior or consume evidence from the bridge. Setup's recommended path uses a microphone physical input to B1, direct MPV headphone playback, and a separate MPV output to AUX/B1. The temporary YouTube listening path still switches Windows default Console and Multimedia render outputs to VAIO while WebView2 is playing. Its process-loopback copy feeds AUX/B1 separately.

Route and microphone-device recovery records are stored under `%LOCALAPPDATA%\WARDOGS Radio`. A write is preceded by a recovery record, then verified by reading the mixer. Rollback restores only when the current value still matches the value WARDOGS applied. An unreadable or ambiguous state retains the recovery record and reports Needs Attention. Permanent setup commits the temporary lease after configuration is saved. The YouTube route has its own `youtube-route-recovery.json` for the cross-system Windows/mixer change.

During full exit, WARDOGS stops audition and temporary feeds, releases macro and limiter state, verifies restoration of the temporary YouTube route, saves configuration, and then disposes audio resources. A failed safety-critical restore leaves the app open with a specific repair message.

## Owner-live acceptance procedure

Use a test machine with the intended microphone, headphones, game or voice app, Banana virtual drivers, and a known local song and YouTube station. Record the date, WARDOGS build commit, Banana version, device names/IDs, game app, and result for each row. Begin with the Windows default playback device and Banana A1 assignment noted down. Do not run these hardware-mutating steps in generic CI.

| Check | Owner-observable pass evidence |
| --- | --- |
| Banana auto-start | With Banana stopped, launch WARDOGS; Audio Bridge reaches Connected without a manual Connect action. |
| Wrong edition | With Standard or Potato running, WARDOGS reports Needs Attention and does not launch Banana over it. |
| Microphone assignment | Choose mic and headphones, Connect, and confirm the selected physical input reads back the chosen mic. An occupied/live physical strip is not taken over. |
| Microphone to B1 | Speak; mic strip and B1 meters move, A1 mic self-monitoring is off. |
| AUX to B1 | Run the built-in game-feed tone; AUX input, Remote B1, and Windows B1 endpoint meters observe it. |
| AUX to A1 | For direct local headphone playback, AUX A1 is off so headphones do not receive duplicate music. |
| Local headset playback | Play a local station and hear it at the selected headphones; headset level works independently of game level. |
| Local game feed | Hear or record the same local station through the selected game/voice input; pause, seek, transition, and level changes stay synchronized. |
| YouTube headset | Play a YouTube station; audio reaches headphones and the temporary Windows default output is visible. |
| YouTube game feed | The process capture, AUX input, B1 Remote meter, and Windows B1 meter show the YouTube program audio. |
| B1 hold audition | Hold the audition control; hear the actual B1 mix at the reduced preview level. Release; normal direct listening returns. |
| Game reception | Select Voicemeeter Out B1 as the game or voice-app microphone and confirm actual reception in that app. |
| Engine restart | Restart Banana during a session; bridge shows Recovering then Connected, or a bounded Needs Attention result. Recheck both local and YouTube feeds. |
| Endpoint disconnect | Disconnect and reconnect headphones; WARDOGS reports the missing endpoint, then re-enumerates and recovers or gives a clear repair action. |
| App restart | Finish verified Setup, close fully, relaunch; completed component verification remains while current identities/routes match. |
| Clean shutdown | Exit the tray app; the temporary YouTube Windows defaults, A1 device, VAIO A1/B1/gain, B1 audition, and temporary bridge leases restore. |
| Interrupted shutdown | Terminate the app while a temporary route is active, then relaunch; recovery restores only WARDOGS-owned values and reports any external changes. |
| Save failure | Deny configuration write in a controlled test; the persistent save indicator shows failure and Setup does not claim saved verification. |

## Evidence boundary and known limits

The automated tests exercise pure readiness decisions, simulated Remote API lifecycle, route and device recovery, external mutation, device identity, stoppable telemetry, diagnostic snapshot export, and configuration save events. They do not prove native WPF rendering, a real Banana driver, physical microphone/headphone behavior, game reception, YouTube capture, or Windows endpoint recovery. The owner-live checklist above remains pending until someone records it.

The YouTube listening path continues to use Windows default-render switching. Other apps that use the default device can be affected during the temporary session. The Windows PolicyConfig setter is an undocumented integration point, so a Windows change may require repair. Automatic limiter writes are paused in this candidate; a legacy in-memory limiter lease can still be restored on clean exit, but it has no crash-recovery record. If a mixer or device state cannot be read back, WARDOGS reports Needs Attention rather than claiming Ready.
