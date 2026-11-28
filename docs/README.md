# Documentation

This folder contains the operating, administration, and development documentation for Sacramental Records.

- [User guide](user-guide.md) — install, open a parish data file, search, maintain records, exchange with Flocknote, and publish changes.
- [Administration and data management](administration.md) — package locations, permissions, backups, updates, Flocknote snapshots, and recovery.
- [Import guide](import-guide.md) — prepare and run the legacy workbook/CSV importer safely, and link Flocknote members.
- [Technical reference](technical-reference.md) — architecture, storage contract, concurrency, validation, and release process.
- [Release history](release-history.md) — version-by-version change notes.

## Database Generation Script

For complete sacramental records database generation from all five data sources:

- [Quick Reference](GENERATE-SRDB-QUICK-REFERENCE.md) — one-page quick start with examples
- [Usage Guide](GENERATE-SRDB-USAGE.md) — complete documentation with parameters and troubleshooting
- [Flocknote Sacrament Import](FLOCKNOTE-SACRAMENT-IMPORT.md) — technical implementation details

The script (`scripts/generate-srdb.ps1`) orchestrates a complete single-pass import of:
1. Legacy Sacraments
2. PDS Export
3. Weddings
4. Flocknote Member linking
5. Flocknote Sacrament exchange

Start with the [root README](../README.md) for a short, end-user overview.
