# Technical reference

## Architecture

The solution is divided into a WPF desktop client (`SacramentalRecords.App`), application services and validation, domain records (`SacramentalRecords.Domain`), repository and use-case services (`SacramentalRecords.Application`), SQLite persistence (`SacramentalRecords.Infrastructure`), and the legacy import utility. Certificate generation is an application concern and uses Microsoft Word only when a certificate is requested.

The application opens a selected package through `SqlitePackageStore`. At startup it verifies the package, acquires a sibling lock, extracts `MemberRecords.sqlite` into a unique local application-data session directory, initializes the schema, and performs reads/writes against that local database. WAL files, journals, and temporary SQLite files therefore remain local.

## Package contract

An `.srdb` file is a ZIP archive intended to contain exactly one entry: `MemberRecords.sqlite`. The supported tables are `People`, `Sacraments`, and `AuditEvents`. Dates are `YYYY-MM-DD`; timestamps are round-trip ISO-8601 text; record identifiers are application-generated GUIDs.

`People` includes `RecordId`, names, birth details, `ChurchDbId`, optional person-level baptism details, status, PDS/Flocknote identifiers, and `UpdatedUtc`. `Sacraments` includes `RecordId`, `PersonRecordId`, `SacramentType`, date, name/place/minister, parents, sponsors, proxies, register volume/page/item, details, canonical notations, and `UpdatedUtc`. `AuditEvents` includes `EventId`, `RecordId`, user, operation, occurrence time, and summary. The store upgrades older databases with the sponsor-witness and proxy columns when a session initializes.

Baptism data in a baptism sacrament is authoritative. Person-level baptism fields are reserved for baptism performed elsewhere and transmitted to the home parish. Record identity must never depend on row position.

## Flocknote exchange

`FlocknoteExchange` (Infrastructure) reads a Flocknote Sacrament spreadsheet with a streaming OpenXML reader, locating columns by row-1 header text. Rows are keyed by `flocknoteID` and matched to `People.FlocknoteId`; the sheet's `memberID` is a Flocknote-internal number, not a PDS ID. No schema is added; snapshot data is not stored. Field mapping per sacrament prefix (`Baptism`, `First Communion`, `Confirmation`, `Marriage`, `Holy Orders`):

| Flocknote header | Registry column |
|---|---|
| `<Prefix> Date` | `SacramentDate` |
| `<Prefix> Location` | `Place` |
| `<Prefix> Celebrant` | `Minister` |
| `<Prefix> Record Book` / `Record Book Page` / `Record Book Entry` | `Volume` / `Page` / `Item` |
| `<Prefix> Notes` | `Details` |
| `Godparents`, `Confirmation Sponsor`, `Marriage Witnesses` (pipe-separated) | `SponsorOne` / `SponsorTwo` |
| `Confirmation Name`, `Marriage Spouse` | `Name` |

First Reconciliation and Death Register columns are not mapped. Placeholder values such as `NOT NOTED` are treated as blank, and comparisons tolerate case, punctuation, titles, and word subsets. Imports are applied by `RegistryService.SaveBatchAsync` in a single transaction with one audit event, using `UpdatedUtc` stale-record checks. Update files are written as new workbooks with the snapshot's header row; writing to the snapshot path is refused.

## Reliability and concurrency

Writes use SQLite transactions and are serialized by a per-session semaphore. Save operations compare `UpdatedUtc` with the editor’s loaded value to reject stale writes. Publish creates and verifies a temporary ZIP in the package directory, makes a timestamped backup, and replaces the selected package. A publish failure leaves the local session intact. Closing warns about unpublished work rather than silently publishing it.

These protections apply to clients following the package protocol. They do not protect against independent file replacement, direct SQLite edits, or multiple external writers.

## Release process

The application version is the `AssemblyVersion` in `src/SacramentalRecords.App/SacramentalRecords.App.csproj`. `scripts/Publish-Release.ps1` publishes a Windows x64 ZIP, refuses to include parish data, computes its SHA-256 hash, and writes `releases/latest.json`. Unless `-NoPush` is supplied, it stages only `releases/` and the application project file, commits them, pushes the commit, and creates a GitHub Release tagged `v<version>` with the ZIP attached. A token with repository contents/release write permission is read from `GITHUB_TOKEN` or `-GitHubToken`. The manifest remains in the repository because the application uses its raw GitHub URL for update checks; its `notes` field and the GitHub Release body contain the release notes. By default, the body includes `-Notes` followed by commit subjects since the latest release tag; `-IgnoreCommits` omits the generated commit list. The release history document is a pointer to GitHub Releases rather than a second manually maintained copy.

Build locally with `dotnet build .\SacramentalRecords.sln`. The desktop application and Word integration require Windows. For release details, see [Release history](release-history.md).
