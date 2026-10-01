# WARDOGS Radio v0.4.1

Owner-review candidate. This version has not been tagged or published.

## Fixed

- Physical microphones can be selected safely when Voicemeeter reports decorated WDM or MME device names. Recovery uses the same device identity rules as assignment.
- The bottom Now Playing visualizer follows the selected listening output during local and YouTube playback and returns to idle when playback stops.
- Listening statistics update during the current session without a manual refresh or application restart.

## Added

- **Audio Devices on Startup** lets you choose **Use Last WARDOGS Devices** or **Use Current Windows Default Devices**. Existing configurations keep Last Used. Windows Defaults takes a snapshot each time WARDOGS starts.
- The Listening page refreshes immediately when opened, then every 30 seconds while visible.

## Reliability

Audio Bridge readback, rollback, crash recovery, and protection against outside changes remain in place. Startup microphone changes use the same verified routing pipeline as manual selection. Unavailable, ambiguous, or virtual default microphones retain the previous selection and show a repair message.

Physical microphone selection, visible playback animation, audible output switching, and restart behavior still require the [owner acceptance checks](V0.4.1-CandidateAudit.md).
