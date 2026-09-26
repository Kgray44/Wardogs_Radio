# Playback providers

MPV is a native provider started as an owned child process with `--no-config`, `--idle=yes`, `--no-video`, and a unique Windows named-pipe JSON IPC endpoint. Player mode saves the current track and position; Radio mode uses local-file durations to advance the station's virtual playlist clock while it is tuned away. An unsupported stream or playlist source falls back to its saved position with a warning rather than claiming exact Radio positioning. MPV is not kept audibly playing in the background while another station is selected.

YouTube is an official visible WebView2/IFrame path. One player is reused for the active Station. Its capabilities prohibit hidden/background and simultaneous instances. Its virtual Radio Mode is a logical timeline, not audio extraction or unauthorized background playback.
