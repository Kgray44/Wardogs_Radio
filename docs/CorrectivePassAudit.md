# Verification status

This is a current development checklist, not a claim that every installation or route is verified. Automated checks, owner listening, Windows endpoint measurements, and game/HOTAS acceptance are different kinds of evidence.

## Verified locally on 2026-09-25

- Release .NET solution tests: 149 passed. The YouTube player behavior test passes for end/repeat and named-song commands.
- A self-contained Windows x64 candidate was published and opened. The owner confirmed timeline seek, hover time, and right-click Create New Song after the hit-testing fix.
- The owner confirmed local and YouTube music playback, YouTube-to-Voicemeeter music/B1 meters, independent headset/game sliders, YouTube resume, and B1 hold-to-test in prior candidates. The owner confirmed the AMD microphone route has live input and restored headset quality.
- Live B1 Windows capture measured signal while YouTube played. The top-right badge then displayed `B1: LIVE OUTPUT`. This proves that endpoint had signal, not that a game had selected it.
- The playlist song-splitting, reordering, persistence and macro action catalog have focused automated tests. The newest dialog/editor and playlist context-menu controls still need owner-visible retesting after deployment.

## Remaining acceptance gates

- Walk every page/dialog at the owner's display scale, including the newly edited Create Song dialog and song context menus; verify mouse, keyboard, scrolling and clipping. Source compilation alone is not visual acceptance.
- Check fresh-install Setup Wizard behavior with real Voicemeeter devices, music and microphone, including route recovery after interruption. The user's current configured machine is not equivalent to a clean setup.
- Select Voicemeeter Out B1 in a real game/voice-chat app and confirm the receiver hears the intended microphone/music mix. A moving B1 meter cannot prove receiver configuration.
- Test physical HOTAS/controller discovery, bindings and macro execution with the intended device connected; none is currently available for that verification.
- Apple Music and SoundCloud official account authorization need owner-managed credentials. They remain visibly setup-required, not simulated integrations.
- Publish the reviewed source to the requested GitHub repository and check the Windows build workflow.
