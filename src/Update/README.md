# Portable updates

General settings checks the latest stable release in `guijianchou/Kit`. The runner launches
`Updater/Kit.Updater.exe`; it selects exactly `kit_<version>_Portable.zip` and its `.sha256`
asset. Downloading requires a separate user action. Installing requires **Restart to update**.
There is no background installation or automatic elevation.

The helper is a self-contained .NET single-file executable, published as part of normal
solution/essential builds. Its runtime does not depend on the files it replaces. Release
and Debug staging generate `portable-manifest.json`, which lists all managed files and
their SHA-256 hashes. Releases before this feature lack that manifest and cannot be used
as new update targets. The first upgrade test therefore keeps a local 3.5.2 build containing
the updater and upgrades it to the newly published 3.5.3 release.

Downloads stream into `%LOCALAPPDATA%/Kit/Updates/<installation-id>/package.zip.part`.
Length, checksum, GitHub asset digest (when present), archive paths, manifest inventory,
individual hashes, and runtime versions are checked before the package becomes installable.
Cancellation discards partial downloads. Reopening Settings reconciles interrupted tasks.
Repeated checks preserve an already verified download of the same version.

On restart the installed helper copies itself to the installation-specific update cache.
The external helper verifies the package again, requests normal runner shutdown, and waits
up to 60 seconds for processes in that installation to exit. It never force-kills them.
All existing destination files are backed up and checked for exclusive access before
replacement. A flushed journal precedes every mutation. Obsolete files are removed only
when listed by the old manifest and still matching their original hash. Unlisted files
and locally modified obsolete files are preserved. Settings and module data remain in
`%LOCALAPPDATA%/Kit` and are never included in the replacement operation.

Failures during replacement or process launch roll back from the backup. A failed rollback
retains the journal and backup, and Settings offers **Restore and restart**. If interruption
prevents Kit itself from starting, close all processes from that installation, then run:

```powershell
& "$env:LOCALAPPDATA\Kit\Updates\<installation-id>\worker\Kit.Updater.exe" recover 'C:\path\to\Kit' 0
```

The worker validates its cache location and the journal's installation root before recovery.
One completed backup is retained until the next installation attempt. Diagnostics are in
the same cache directory's `updater.log`. A folder without write access or containing
filesystem links is rejected; move the Portable installation to a normal writable folder.

Tests in `Kit.Updater.Tests` exercise release selection, checksums, cancellation, malicious
archives, package inventory/version checks, locked files, partial-write rollback, durable
recovery, retained user files, stale task state, and the full download/verification service.
Windows CI runs these alongside the existing module and Settings tests. A real release
upgrade must additionally verify the old UI's check/download/restart flow, new process
paths and versions, and retained user configuration.
