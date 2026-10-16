# Technical reference

## Architecture

The solution is divided into a WPF desktop client (`SacramentalRecords.App`), application services and validation, domain records (`SacramentalRecords.Domain`), repository and use-case services (`SacramentalRecords.Application`), SQLite persistence (`SacramentalRecords.Infrastructure`), and the legacy import utility. Certificate generation is an application concern and uses Microsoft Word only when a certificate is requested.

The application opens a selected package through `SqlitePackageStore`. At startup it verifies the package, acquires a sibling lock, extracts `MemberRecords.sqlite` into a unique local application-data session directory, initializes the schema, and performs reads/writes against that local database. WAL files, journals, and temporary SQLite files therefore remain local.

## Package contract

An `.srdb` file is a ZIP archive intended to contain exactly one entry: `MemberRecords.sqlite`. The supported tables are `People`, `Sacraments`, and `AuditEvents`. Dates are `YYYY-MM-DD`; timestamps are round-trip ISO-8601 text; record identifiers are application-generated GUIDs.

`People` includes `RecordId`, names, birth details, `ChurchDbId`, optional person-level baptism details, status, PDS/Flocknote identifiers, and `UpdatedUtc`. `Sacraments` includes `RecordId`, `PersonRecordId`, `SacramentType`, date, name/place/minister, parents, sponsors, proxies, register volume/page/item, details, canonical notations, and `UpdatedUtc`. `AuditEvents` includes `EventId`, `RecordId`, user, operation, occurrence time, and summary. The store upgrades older databases with the sponsor-witness and proxy columns when a session initializes.

Baptism data in a baptism sacrament is authoritative. Person-level baptism fields are reserved for baptism performed elsewhere and transmitted to the home parish. Record identity must never depend on row position.

## Reliability and concurrency

Writes use SQLite transactions and are serialized by a per-session semaphore. Save operations compare `UpdatedUtc` with the editor’s loaded value to reject stale writes. Publish creates and verifies a temporary ZIP in the package directory, makes a timestamped backup, and replaces the selected package. A publish failure leaves the local session intact. Closing warns about unpublished work rather than silently publishing it.

These protections apply to clients following the package protocol. They do not protect against independent file replacement, direct SQLite edits, or multiple external writers.

## Release process

The application version is the `AssemblyVersion` in `src/SacramentalRecords.App/SacramentalRecords.App.csproj`. `scripts/Publish-Release.ps1` publishes a Windows x64 ZIP, refuses to include parish data, computes its SHA-256 hash, writes `releases/latest.json`, and can commit/push the release. The manifest contains `version`, `package`, `sha256`, and `notes`; the application uses the manifest URL in its settings for update checks.

Build locally with `dotnet build .\SacramentalRecords.sln`. The desktop application and Word integration require Windows. For release details, see [Release history](release-history.md).
