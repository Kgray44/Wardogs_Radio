# WARDOGS Radio v0.4.2

Hotfix for YouTube playlists blocked by a stale audio-route recovery record.

## Fixed

- YouTube playback can reconnect after its previous WebView audio process exits
  or restarts. WARDOGS no longer repeatedly sends policy restoration requests to
  an exited process, which Windows rejects with "Value does not fall within the
  expected range."
- Recovery checks the process start time as well as its ID. An exited process or
  reused ID releases the stale lease without changing another process, and the
  recovery record is retained for diagnostics.
- An empty Windows policy is no longer mistaken for an endpoint owned by an
  earlier output switch. Live-process restoration still verifies ownership and
  readback; outside changes remain untouched.

The owner confirmed restored playlist playback with the local hotfix on
October 10, 2026 and authorized this official release. Automated recovery tests
and a silent YouTube audio-session startup check passed before publication.

The Windows installer is unsigned.
