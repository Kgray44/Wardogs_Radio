# Backup & Transfer

## Current model audit

WARDOGS Radio persists one `AppConfiguration` JSON document in `%LocalAppData%\WARDOGS Radio\config.json` through `ConfigurationStore`. Saves use a temporary `.new` file and replacement, retaining `config.last-good.json`. Configuration schema 11 already uses the canonical relationship model:

```text
MediaSource (Id)
  -> LibrarySong (Id, SourceId)
    -> StationPlaylistEntry (Id, SongId)
      -> Station (Id)
        -> RadioAction (StationId)
```

`RadioMacro` owns keyboard and controller bindings. Playback, audio routing, hardware identities, provider paths, and general state are fields on `AppConfiguration`. The WPF settings page is a single themed scroll view, and custom `RadioDialogWindow` dialogs are the existing confirmation convention.

There is no persisted appearance/theme model or secret/authentication field in the current configuration. `.wradio` never exports secrets.

## Package format

`.wradio` is a ZIP archive with independent package schema version `1`:

```text
manifest.json
configuration/stations.json
configuration/macros.json
configuration/settings.json
library/sources.json
library/songs.json
listening/history.json                (when Listening History is selected)
metadata/export-info.json
assets/media/<source-id>-<safe-name>     (only when selected)
```

The manifest identifies the package, application version, package type, selected content categories, and embedded-media records. `IWrRadioPackageMigration` supplies a chainable, independently testable migration seam; future package schemas must add explicit migrations instead of being guessed.

The export dialog can create either a selective transfer package or a complete restorable backup. A full backup always overrides item selections and carries every supported configuration category; only it is accepted by Restore.

Listening History is a separate private local journal under `%LocalAppData%\WARDOGS Radio\Listening`. It is included by default in complete backups and can be selected or omitted in transfer packages. Import merges history by stable event ID and remaps imported station/song/source IDs when those objects are copied. Restore replaces the local history when the backup includes it. Older full backups that predate Listening History remain restorable and leave the current history in place; they contain no invented past plays. A safety backup includes the current history before import or restore. API keys are never packaged.

## Safety behavior

The service validates ZIP paths, entry counts and sizes, decompression ratio, manifest identity/version, JSON, duplicate IDs, all song/source/station/macro relationships, and embedded-media names before a package can be planned. It never extracts archive paths. Export writes `*.tmp` and moves it only after finalization.

Selected station or macro dependencies are resolved through the canonical graph. An imported conflicting library item becomes a new ID by default and all imported station/macro references are remapped. Name-only station collisions default to a separate ` (Imported)` station. Import adds to the active configuration; restore accepts only a complete backup and replaces it.

Before import or restore, a complete automatic package backup is created in `%LocalAppData%\WARDOGS Radio\Backups`; the ten newest automatic backups are retained. The proposed configuration is built and normalized in memory, local media is staged under `ImportedMedia`, and `ConfigurationStore` performs the atomic configuration commit. Audio/hardware identities are retained without automatic device reassignment and surfaced as warnings in the import plan.

Package activity and failures are recorded in `%LocalAppData%\WARDOGS Radio\Diagnostics\package.log` under `Package.Open`, `Package.Validate`, `Package.Migrate`, `Package.BuildImportPlan`, `Package.Export`, `Package.Backup`, `Package.LocalMedia`, and `Package.Commit` categories. User-facing dialogs remain concise.

Embedded local media is opt-in. Safety backups deliberately preserve source references instead of automatically copying potentially large media collections.

When the Settings host can enumerate current Windows audio endpoints and controllers, the import plan identifies unavailable package hardware explicitly. It preserves those IDs and bindings for later repair and never chooses a replacement device on the user’s behalf.
# Backup and Transfer

## Opening packages

Packages supplied as an application startup argument or dropped onto the main window follow the same safe import path as **Import Package**: the archive is validated, its contents and conflicts are previewed, and the user must confirm before any configuration is changed. Dropping ordinary local media files continues to add library sources without copying the media.
