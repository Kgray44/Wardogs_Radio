# YouTube Search packaging

WARDOGS uses the YouTube Data API v3 for native video and playlist search. Normal
release packages require a shared default key. The key is supplied at build time
and embedded into the app assembly; it is never committed to this repository.

## Insert a replacement default key locally

1. In Google Cloud, enable **YouTube Data API v3** for the project that owns the
   replacement key. Restrict the key to that API, monitor quota use, and rotate
   the key when needed.
2. Create `packaging/private/youtube-search-key.txt` in this checkout. Put **only
   the replacement key** on one line, with no quotes or `KEY=` prefix. The entire
   `packaging/private/` directory is ignored by Git. On Windows, for example:

   ```powershell
   New-Item -ItemType Directory -Force packaging/private | Out-Null
   notepad packaging/private/youtube-search-key.txt
   git check-ignore packaging/private/youtube-search-key.txt
   ```

3. Run the normal packaging command from the repository root:

   ```powershell
   .\scripts\stage-package.ps1 -StageDir "$PWD\release\stage"
   ```

   The stage script checks the private file before touching the stage directory,
   passes its **path** to MSBuild, and verifies that the staged app contains a
   usable default key. It never prints the key. You can also pass
   `-YouTubeDefaultKeyFile C:\private\replacement-key.txt` to use another file.
   Packaging fails when the key is absent or malformed.

For a hosted release, set the GitHub Actions secret
`WARDOGS_YOUTUBE_SEARCH_KEY` to the raw key. The release workflow writes it to a
temporary runner file and passes that file to the same packaging step. CI's
`-AllowUnconfiguredYouTubeSearch` mode exists only to test the paste-link
fallback without a release secret.

## In-app behavior

The key order is **custom user override → packaged default → unavailable**.
Settings → Music Services contains status, Test Connection, a masked custom-key
field, and Restore Default Key. A custom key is protected for the current
Windows user at `%LocalAppData%\WARDOGS Radio\Search\youtube-key-v1.dat`; it is
not part of `config.json`, `.wradio` backups, or diagnostics. Restore Default
removes that local override. Search results are cached during the app session.
Search and Test Connection send the key in the `x-goog-api-key` header. Manual
YouTube URL entry and playback continue to work without any search key.

A desktop package distributes its embedded default key to user machines, so a
determined user can extract it. Keep quota limits and monitoring on the Google
Cloud project; build-time injection protects source control and build logs, but
does not make a shared desktop key a secret from recipients. Google recommends
[API restrictions](https://docs.cloud.google.com/api-keys/docs/add-restrictions-api-keys)
and [using the API-key header rather than a URL query parameter](https://docs.cloud.google.com/docs/authentication/api-keys-best-practices).
