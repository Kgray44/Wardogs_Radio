# Readiness and Audio Bridge owner review

This is an unreleased feature-branch candidate. The first PR #1 build received owner-live testing and exposed the regressions below. Any follow-up fixes require renewed source, build, simulated, and owner-live review before acceptance.

## Owner-live findings from the first PR #1 candidate

The owner ran the first candidate on the intended Windows audio setup. These are physical observations, distinct from the simulated tests below:

- Setup and Diagnostics reached 9/10 core checks, while the quiet microphone left overall System Health at Needs Verification.
- YouTube playback changed Windows default output from the Razer Barracuda Pro to Voicemeeter Input. The owner heard worse treble and somewhat worse bass through that path.
- When the owner manually restored the real headphones as Windows default, WARDOGS repeatedly reclaimed Voicemeeter Input. The route eventually stopped reclaiming it and Audio & Routing reported Needs Attention.
- Audio & Routing accepted laptop speakers as the listening choice while active YouTube audio kept playing through the Razer headset.
- Clip Guard Protect made no perceptible or functional difference from Off. Source inspection confirmed that actuation was disabled in that candidate.
- Meters visibly lagged audio. Several important buttons lacked nearby working/result feedback.
- Settings cards were noticeably narrower than other pages. Setup & Repair and Audio & Routing still exposed too much mixer terminology; Audio & Routing remained mostly textual.

The later self-contained preview at `9b15590` received another owner-live check. The owner reported that local playback worked, and that the simplified Setup page was much better but should be a little wider. During YouTube playback, the visible player advanced while WARDOGS produced no listening or game-feed sound; the UI said "YouTube audio session did not appear" and paused the player. A read-only enumeration of the preview's WebView2 child processes found an audio-service utility process but no Windows audio session for any of those PIDs after the failure. This contradicts the earlier synthetic oscillator probe as evidence for the actual YouTube iframe startup path. The follow-up now creates a silent WebView audio session before play and attempts endpoint routing before raising the YouTube source volume. A hidden WebView2 probe loaded the real `youtube-player.html`, called its new silent-session bootstrap, confirmed per-app endpoint-policy readback, and observed unchanged Windows global default. That correction remains unverified with an actual YouTube station in the owner's app.

The next self-contained preview at `7e50df6` restored audible YouTube listening in the owner's headphones. The owner reported that the held B1 audition button played the game mix at half level for about one second, then skipped and fell silent. During normal YouTube playback, read-only live checks found continuing signal in the WebView audio session, the WARDOGS game-output session on Voicemeeter AUX, Voicemeeter's music strip and B1 bus, and the Windows Voicemeeter Out B1 capture endpoint. Those checks establish signal up to B1; actual game/Discord reception is still unconfirmed. The audition code muted the WebView's Windows session, which also attenuated the process-loopback game copy: an isolated tone probe measured capture peak falling from 0.0708 to 0.0151 at quarter session volume.

The following preview at `d5f5f82` tried WebView's `IsMuted` during the B1 hold. A synthetic WebView oscillator kept a 0.0150 process-loopback peak while muted, but the owner-live YouTube iframe did not: the owner again heard the preview stop early while the button still read "RELEASE TO STOP B1 TEST". During that silent hold, a read-only measurement found the Windows B1 endpoint near 0.001 peak and an open but silent preview output. A separate synthetic Windows session-mute probe also drove the game-copy capture to zero. The next correction therefore leaves YouTube direct listening active during the B1 preview and adds the actual B1 mix at a quieter 25% level; the UI states that YouTube remains audible. Local playback retains its separate headphone-player mute. This revised behavior needs owner-live retest and is an explicit limitation of the single-source YouTube architecture.

In the `71479ef` preview, the owner heard the B1 preview continuously, but could not distinguish it from normal YouTube listening because the direct source remained audible. The button now adds a short, quiet test tone to the configured game output while the B1 audition is held, labels YouTube's additive preview honestly, and names Voicemeeter Out B1 as the game/Discord input to select. Hearing that tone locally tests the output-to-B1-to-headphone path; actual game reception still requires checking the game's selected input and hearing it there.

The owner heard that distinct tone clearly in the `4871e43` preview. This confirms the configured game output, B1 path, and local headphone audition for the tone on the owner's machine. The owner has not yet confirmed that a game or Discord receives YouTube music from Voicemeeter Out B1. The current PR checks passed on GitHub, but that CI result does not substitute for this last external-app check.

The owner also changed the listening device from Razer headphones to laptop speakers and back during the same active YouTube station. The song moved both ways without retuning, and Windows' default remained the Razer headset; a separate read-only default-device check agreed. This is owner-live acceptance of the YouTube device-switch path on this machine. The owner could not yet test game/Discord reception. The owner also noted that the held B1 control did not sound like an isolated output preview because YouTube direct listening continued. The UI now calls it a game-output check with a distinct tone and explicitly says YouTube stays audible; it no longer promises an isolated YouTube mix.

The follow-up amendment uses these as regression evidence. A later build or simulated test does not erase or supersede them. The owner has also reported that the later Audio & Routing preview is somewhat better while Setup & Repair still contains far too much information. A substantially simpler Setup page is open for renewed owner review. Repeat owner-live acceptance is required before merge readiness can be claimed.

## Follow-up candidate in progress

The branch now routes a WebView2 audio session to the selected physical output with a Windows per-app endpoint policy; a separate process-loopback copy continues to feed AUX/B1. A synthetic WebView2 tone probe observed active switching between two endpoints, independent session listening level, unchanged Windows global default, and a continuing game copy. These observations are process and signal evidence on the development machine, not the owner's listening or game-reception acceptance. The old Windows-default route remains only for recovery of an interrupted earlier candidate session. Active local and YouTube output changes now use the same UI operation and require provider readback before the selection is saved.

Core readiness treats a silent configured microphone as informational. Clip Guard can now apply reduction to the independent local or YouTube game feed only when both B1 telemetry sources agree; it leaves the listening and microphone levels alone. The bridge meter worker and UI signal timer run at 40 ms nominal intervals. Setup's main page now centers on choosing a microphone and listening output, connecting, and confirming game reception, with technical controls collapsed. The owner is reviewing this page. A visit-wide Setup checkpoint is written to disk and offers Continue, Keep, or Undo after an interrupted session. The restart path still needs owner-live testing, as does the full acceptance procedure below. No merge or release is authorized.

The follow-up Release solution build succeeds with zero errors. The current automated suite passes 301/301 tests, including a checkpoint reload across store instances and a concurrent bridge-snapshot stress test. These results do not establish rendered UI or physical audio behavior.

## Original overhaul review

| Request | Candidate result |
| --- | --- |
| Branch, commit, files | `codex/readiness-setup-overhaul`; use the branch tip for the exact commit. The change spans Core readiness/configuration, Voicemeeter bridge, WPF Setup/Diagnostics/Settings, Playback endpoint identity, installer, docs, and tests. |
| Architecture and duplicate state | `SystemReadinessService` evaluates one typed readiness snapshot. Dashboard/sidebar, Setup, Diagnostics, and diagnostic export consume it. `AudioBridgeService` supplies the supported Banana topology and observed mixer state. The old installed-means-setup-needed health assignment and independent report export path were removed. |
| Five-step Setup | System Check → Audio Devices → Connect to Game Voice → Verify Your Radio → Ready. Optional provider, station, and controller panels are outside required progression. Setup opens at the first incomplete area, or Ready after an unchanged verified launch. |
| Setup & Repair | Device choices are staged, Connect previews the proposed path, and leaving with changes asks Keep or Undo. A visit checkpoint restores prior configuration, route switches, microphone device/gain, and player output only where current mixer state still matches WARDOGS's applied value. Advanced controls remain available. |
| Readiness | Configured, observed, and owner-confirmed evidence are distinct. Saved verification persists per device/route identity; optional integrations do not block core Ready. Conflicting B1 telemetry blocks Ready. |
| Diagnostics | Actionable/core/playback/library/optional groups sit above collapsed raw evidence. Connect, device refresh, owned AUX/B1 repair, bundled MPV, and navigation repairs act only when pressed. Screen and export use one `DiagnosticSnapshot`. |
| Settings and save state | Duplicate visible controls were reduced. Bundled MPV status/override and reset remain. Save events show Saving, Saved, or Failed. The in-app update check inspects the official stable manifest and asks before a restart. |
| Compatibility | Automatic routing supports Banana only. Potato and Standard are reported as unsupported; the installer requires Banana. |
| Built-in test | A quiet short signal targets the selected game-feed Windows endpoint without creating a station. The test separately observes music input, Remote B1, and Windows B1. It does not claim game reception. |
| Build and tests | Release solution build succeeds. `dotnet test WardogsRadio.sln -c Release --no-restore --nologo -v:q -p:WarningLevel=0` passes 293/293 simulated tests; `git diff --check` is clean. A normal rebuild emits existing NAudio obsolescence and unrelated nullability warnings; there are no build errors. |
| UI evidence | XAML compiles; native rendering, screenshots, and actual audio behavior remain for owner testing. |

## Audio Bridge amendment review

| # | Requested point | Candidate implementation or evidence |
| --- | --- | --- |
| 1 | Architecture | `AudioBridgeService` owns Banana connection, topology, mixer telemetry, verified writes, ownership records, faults, and recovery. |
| 2 | Remote access | `MainWindow` constructs/disposes `VoicemeeterRemote`. Only the Voicemeeter project calls its mutation methods. Diagnostics consumes the bridge for read-only probes. The legacy route controller is used only by tests. |
| 3 | Startup lifecycle | Connects to a running Banana or uses `VBVMR_RunVoicemeeter(2)` after Remote API login; waits up to four seconds. |
| 4 | Edition decision | Banana is supported. Another running edition is left untouched and reported as Needs Attention. |
| 5 | Route transaction | Reads prior value, saves a write-ahead lease, writes, verifies, then commits after configuration save or restores on failure. |
| 6 | Readback | Route, device, gain, and temporary YouTube mixer writes require confirming readback; setter return alone is insufficient. |
| 7 | Ownership | Route/device leases name owner, resource, prior/applied value, and time; releases compare current value before restoration. |
| 8 | Crash recovery | Local route/device journals are validated before use. Startup recovers supported owned values before fresh routing; invalid/unreadable records block new lease writes. |
| 9 | External mutation | A different current value is left untouched and recorded as a fault or Needs Attention. Whole device-name matching avoids substring ownership. |
| 10 | YouTube route decision | The first candidate used Windows default Console/Multimedia switching. The follow-up replaces new playback with a per-app WebView2 session route; process loopback still feeds AUX/B1. |
| 11 | Restore bug | Empty prior A1 assignment is cleared through the matching driver interfaces and read back. Ambiguous states keep the recovery record; live regression remains pending. |
| 12 | YouTube feed recovery | Faulted capture/output gets up to three restart attempts; an inactive but recoverable listening route reloads the visible player. Station identity is checked before a feed attaches. |
| 13 | Local feed recovery | Missing second MPV game player gets up to three reload attempts and seeks to the headset player's current position; headset playback is left running. |
| 14 | Telemetry worker | The first candidate used 200 ms. The follow-up uses a 40 ms nominal bridge meter worker and 40 ms UI signal timer. Raw channel forensics run only in Diagnostics. |
| 15 | Device identity | A complete endpoint GUID maps an MPV WASAPI device to a Windows endpoint; ambiguous/partial names do not match. Banana physical-strip assignment also checks device name and live meter before takeover. |
| 16 | Engine restart | Disconnect marks Recovering and triggers up to three reconnect/recovery attempts. Wrong edition or unresolved journals remain Needs Attention. |
| 17 | Endpoint return | Bounded Windows endpoint discovery runs every 15 seconds; disappearance changes readiness, return refreshes identities and resets game-feed recovery attempts. It cannot guarantee every physical device resumes playback. |
| 18 | Setup integration | Recommended Connect uses bridge device/route operations. Visit-scoped Undo covers saved audio configuration, route/device/gain deltas, and manual route leases. |
| 19 | Diagnostics integration | The same readiness snapshot drives UI/export; advanced report includes bridge connection and recent faults. Safe owned AUX/B1 repair calls the bridge. |
| 20 | Macro integration | Mixer route toggles/holds use bridge leases, restoring exact prior state on release when still owned. Other macro actions retain their existing playback behavior. |
| 21 | Shutdown | Full exit stops audition/game feed, releases macro/limiter state, verifies temporary YouTube route restoration, saves configuration, then disposes resources. A failed critical restoration keeps the app open for repair. |
| 22 | Unit tests | Added readiness, Audio Bridge, device identity, recovery, save state, diagnostic export, and A1 clear tests. |
| 23 | Fault injection | Tests cover failed engine start, wrong edition, setter success without readback, blocked journal writes, invalid journals, and external mutations. |
| 24 | Integration tests | No native hardware integration test was run. Existing opt-in live probe infrastructure remains separate from generic CI. |
| 25 | Live evidence | None obtained for this candidate. Build and simulated tests do not prove audio or rendered UI. |
| 26 | Pending acceptance | Complete the checklist in [ReadinessAudioBridgeCandidate.md](ReadinessAudioBridgeCandidate.md), including Banana auto-start, mic/AUX/B1, local/YouTube, game reception, restart, hotplug, shutdown, and crash restore. |
| 27 | Reliability limits | The follow-up per-app Windows audio policy is undocumented and needs live validation. The wizard visit checkpoint and in-flight bridge writes now have separate durable journals; restart behavior still needs owner-live acceptance. Native timing/device naming also requires live acceptance. |
| 28 | Manual Voicemeeter interaction | Intended normal path requires none on a supported Banana installation. The real machine path has not yet been demonstrated. |
| 29 | Owner-review readiness | This row described the original committed candidate. Follow-up edits are still in progress on the same PR branch and an open preview is awaiting owner review. |

No merge, tag, public release, or installed-app replacement was performed.
