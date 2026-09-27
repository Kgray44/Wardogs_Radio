# Third-party notices

WARDOGS Radio is built with the .NET runtime and uses the following distributed or runtime-discovered components:

- Microsoft .NET and Windows Desktop Runtime — distributed as part of the self-contained application under the applicable Microsoft .NET license notices included by the SDK publish output.
- Microsoft WebView2 SDK (`Microsoft.Web.WebView2`) — the application uses the installed Evergreen WebView2 Runtime; it is not silently installed or replaced by WARDOGS Radio.
- NAudio — audio APIs used under its published MIT license.
- MPV — the installer includes the unmodified x64 portable build `mpv-x86_64-20260924-git-2a4eb8067c.7z` from [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20260924), verified with SHA-256 `0d39c18086f9df02fbd087b18c8b30e7e328a79481bce83e57f86ab70d0e53bf`. Source and license information are available from [mpv-player/mpv](https://github.com/mpv-player/mpv); the installed package includes `THIRD_PARTY_MPV.txt` with this provenance.
- Microsoft Edge WebView2 Runtime — the installer bundles Microsoft's official Evergreen x64 standalone installer and runs it only if the runtime is missing and the user leaves the prerequisite selected. The Runtime is required for YouTube and web playback.
- Voicemeeter Banana — the installer bundles VB-Audio's official unmodified Banana setup and runs it only if Banana or Potato is missing and the user leaves the prerequisite selected. Voicemeeter is donationware; its origin is [voicemeeter.com](https://www.voicemeeter.com/) and donations are welcome. Its driver installation requires administrator approval and a Windows restart. It is not removed by the WARDOGS Radio uninstaller.

Voicemeeter, SoundCloud, Apple Music, and other media providers are external integrations. They are not bundled, overwritten, reset, or removed by this installer. Their names and trademarks belong to their respective owners.
