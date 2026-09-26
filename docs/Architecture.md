# Architecture

`WardogsRadio.Core` owns configuration, Stations, Macros, non-destructive song ranges, transition math, and the independently testable virtual playback timeline. `Playback` owns provider contracts, MPV JSON IPC, endpoint meters and YouTube URL normalization. `Voicemeeter` is the only project that loads the Remote API DLL. `Input` owns controller discovery, including HID capabilities and XInput paths. `Diagnostics` consumes those abstractions and produces exportable observations. The WPF app is the composition root and presentation layer.

The virtual timeline uses an injected clock, a stored deterministic sequence, per-video duration cache, and a cursor. It projects elapsed time across cached tracks, wraps only when loop is set, flags unknown metadata instead of inventing a position, and can be paused/resumed/navigated while inactive.
