# Sacramental Records

Sacramental Records is a Windows desktop app for parish offices. It gives staff one focused place to find parishioners, maintain sacramental and register entries, and produce baptismal certificates without requiring a server or hosted service.

The app is designed for local-first use: the parish data file can live in a controlled shared folder, while each work session uses a protected local copy. This keeps everyday work responsive and helps prevent two people from editing the same file at once.

## Latest release: 1.2.1

The current release is **1.2.1**. Since 1.1.16, the project has:

- Published releases 1.2.0 and 1.2.1.
- Improved certificate generation by adding a setting for the church address.
- Completed the PDS import and added maiden-name and deceased-date fields.
- Added the first draft of the PDS import.
- Corrected person reconciliation for legacy sacraments.
- Improved the lock-file mechanism to report the machine name.
- Improved the documentation.

See the [full release history](docs/release-history.md), [GitHub releases](https://github.com/jeff-woehler/sacramental-records/releases), or the [latest release manifest](releases/latest.json) for the package name, checksum, and release notes.


## Everyday use

1. Open the app and choose **Open** to select the parish `MemberRecords.srdb` file.
2. Search for a person by given or family name.
3. Open the person record to review details and register entries.
4. Choose **Add**, **Change**, or **Add register entry** to maintain the record.
5. Choose **Certificate** to create a baptismal certificate when Microsoft Word is installed.
6. Choose **Save** on the main window to publish your changes to the shared package.
7. Publish before closing if you have made changes since the last publish.

Use the [user guide](docs/user-guide.md) for the complete daily workflow and keyboard shortcuts.

## Important handling rules

- Only one application session should edit a parish package at a time.
- Do not open, edit, or replace the `.srdb` file manually while the app is using it.
- Keep the package in a restricted, backed-up location.
- The app creates backups when publishing; retain them according to parish records policy.
- The release download contains the program, not parish data.

Administrators can read [data management and recovery guidance](docs/administration.md). For moving historical spreadsheets into the app, see the [legacy import guide](docs/import-guide.md).

## Documentation

- [User guide](docs/user-guide.md) — installation, searching, editing, certificates, publishing, and shortcuts.
- [Administration](docs/administration.md) — permissions, package management, backups, updates, and recovery.
- [Import guide](docs/import-guide.md) — dry-run and commit procedures for legacy workbooks and wedding CSV files.
- [Technical reference](docs/technical-reference.md) — architecture, storage, concurrency, and release details.
- [Release history](docs/release-history.md) — historical change notes.





## Development

Install the .NET 8 SDK. The solution is at the repository root and source projects are under `src`.

```powershell
dotnet build .\SacramentalRecords.sln
```

The WPF application and certificate generator require Windows. The domain, application, infrastructure, and import projects can be built where their dependencies permit. See the [technical reference](docs/technical-reference.md) for the project layout and release process.



