# Administration and data management

## Package placement

The shared parish file is normally named `MemberRecords.srdb`. Keep it in a controlled, backed-up folder such as a parish file share or synchronized drive. The application opens a local working copy and does not use the shared package as a live SQLite database.

The package and update-manifest settings are stored in `SacramentalRecords.settings.json` beside the application executable:

- `SACRAMENTAL_RECORDS_PACKAGE` — full path to the `.srdb` package.
- `SACRAMENTAL_RECORDS_UPDATE_MANIFEST` — URL of the release manifest used for update checks.
- `SACRAMENTAL_RECORDS_BAPTISM_TEMPLATE` — optional path to a custom Word template.
- `SACRAMENTAL_RECORDS_CHURCH_ADDRESS` — church address inserted into baptism certificate Word templates.
- `SACRAMENTAL_RECORDS_FLOCKNOTE_SACRAMENT_FILE` — optional path to the Flocknote Sacrament snapshot (`.xlsx`) used by Flocknote Exchange.

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

Use **Admin > Settings** to enter the church address and to download, select, clear, or open the Word baptism certificate template. To place the address in a template, add a Word `DOCPROPERTY` field for `Cert.ChurchAddress`. Test a certificate after changing a template. Word must be installed and configured on the workstation; template or Word errors do not alter the registry data.

## Flocknote

Use **Admin > Settings > Flocknote** to choose or clear the Flocknote Sacrament snapshot. Columns are located by the Flocknote header names in row 1, so column order does not matter; the application warns when expected headers are missing. When no file is set, staff locate the file each time they use Flocknote Exchange.

The standard Flocknote member export includes only sacrament dates and certificate flags. Celebrant, register book/page/entry, sponsors, location, and notes are available only in the sacrament export that Flocknote support produces. Request a new snapshot before each exchange, store it in a protected folder, and replace the file rather than editing it. Update files created by the application are new spreadsheets with the same headers; send them to Flocknote support for loading.

Before the first exchange, link existing people to Flocknote with the member import described in the [import guide](import-guide.md#link-flocknote-members).

## Data-handling rules

- Use the application for edits so validation, audit events, and stale-record checks are applied.
- Do not open the `.srdb` archive as if it were an ordinary SQLite database.
- Never infer a record identity from its row order; identifiers are GUIDs.
- Retain generated backups and limit access to sacramental information.

See the [technical reference](technical-reference.md) for the package contract and the [user guide](user-guide.md) for daily operation.
