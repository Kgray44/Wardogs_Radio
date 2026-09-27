# Readiness and Audio Bridge owner review

This is an unreleased feature-branch candidate. The review boundary is source, Release build, and simulated tests. No native WPF or physical audio acceptance run has been recorded.

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
| 10 | YouTube route decision | Retains Windows default Console/Multimedia switching because the current WebView playback path depends on it. Separate process loopback feeds AUX/B1. |
| 11 | Restore bug | Empty prior A1 assignment is cleared through the matching driver interfaces and read back. Ambiguous states keep the recovery record; live regression remains pending. |
| 12 | YouTube feed recovery | Faulted capture/output gets up to three restart attempts; an inactive but recoverable listening route reloads the visible player. Station identity is checked before a feed attaches. |
| 13 | Local feed recovery | Missing second MPV game player gets up to three reload attempts and seeks to the headset player's current position; headset playback is left running. |
| 14 | Telemetry worker | A stoppable 200 ms bridge worker samples mixer state into immutable records. The UI timer consumes them and separately measures Windows endpoints. Raw channel forensics run only in Diagnostics. |
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
| 27 | Reliability limits | Windows default switching uses PolicyConfig; physical endpoint return may still need repair. Wizard visit checkpoint is in memory, while in-flight bridge writes have durable journals. Limiter automation is paused and has no crash journal. Native timing/device naming requires live acceptance. |
| 28 | Manual Voicemeeter interaction | Intended normal path requires none on a supported Banana installation. The real machine path has not yet been demonstrated. |
| 29 | Owner-review readiness | Source is committed on an isolated clean branch, solution tests pass, and live checks are specified. It is ready for owner testing, with the evidence boundary above. |

No merge, tag, public release, or installed-app replacement was performed.
