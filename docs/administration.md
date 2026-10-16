# Administration and data management

## Package placement

The shared parish file is normally named `MemberRecords.srdb`. Keep it in a controlled, backed-up folder such as a parish file share or synchronized drive. The application opens a local working copy and does not use the shared package as a live SQLite database.

The package and update-manifest settings are stored in `SacramentalRecords.settings.json` beside the application executable:

- `SACRAMENTAL_RECORDS_PACKAGE` — full path to the `.srdb` package.
- `SACRAMENTAL_RECORDS_UPDATE_MANIFEST` — URL of the release manifest used for update checks.
- `SACRAMENTAL_RECORDS_BAPTISM_TEMPLATE` — optional path to a custom Word template.

A legacy `.dbpackage` setting is migrated to the matching `.srdb` filename on startup when it is safe to do so. The application does not overwrite an existing `.srdb` file.

## Permissions and single-writer use

Give intended parish staff read/write access to the package folder and restrict access to the parish’s Windows accounts. Do not give users permission to edit the archive contents directly. The application creates a sibling `.lock` file while a session is open; a second session must not be started against that package.

If the application reports that a package is locked, verify that another session is not open. Use the application’s lock-resolution choices rather than deleting a lock file that may belong to an active user.

## Backups and recovery

Publishing writes a temporary archive, verifies its database entry, and retains a timestamped `.bak` copy of the previous package. Keep backups on a separate protected destination according to the parish records-retention policy. Periodically verify that a backup can be copied and opened in a test location.

If publishing fails, the local session remains available. Do not delete the session directory before retrying or choosing to discard the work. If a package is damaged, preserve the original, make a working copy of the newest known-good backup, and contact the application maintainer before attempting repair.

## Updating the application

1. Confirm that all users have published their work and closed the application.
2. Retain the current package and recent backups.
3. Download the release ZIP from the configured update source and extract it to the application folder, or follow the in-app update prompt.
4. Start the new executable and verify the package path and a few representative records.

The release package contains application files only; parish data is intentionally excluded. Do not replace the data package as part of a program update.

## Certificate templates

Use **Admin > Settings** to download, select, clear, or open the Word baptism certificate template. Test a certificate after changing a template. Word must be installed and configured on the workstation; template or Word errors do not alter the registry data.

## Data-handling rules

- Use the application for edits so validation, audit events, and stale-record checks are applied.
- Do not open the `.srdb` archive as if it were an ordinary SQLite database.
- Never infer a record identity from its row order; identifiers are GUIDs.
- Retain generated backups and limit access to sacramental information.

See the [technical reference](technical-reference.md) for the package contract and the [user guide](user-guide.md) for daily operation.
