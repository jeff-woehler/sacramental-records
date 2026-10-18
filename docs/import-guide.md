# Legacy import guide

`LegacySacramentImport` converts supported Excel workbooks and wedding CSV files into a SQLite database. It is intended for a controlled migration, not routine data entry.

## Safety rules

Work from copies of the source files and import into a new `MemberRecords.sqlite` database. Run a dry run first, review the CSV report, and keep the source workbook unchanged. A commit run creates a backup beside an existing database, but an empty destination is strongly recommended.

The importer recognizes workbook sheets for Baptisms, Confirmations, First Communions, and Marriages/Weddings. It also supports a wedding CSV with the expected bride/groom column pairs. Optional `--lookup` data can canonicalize parish spellings from a `ParishSpellings` worksheet containing `Church Spelling`, `Output Spelling`, and optionally `Occurrences`.

## Commands

From the importer output folder or project directory:

```powershell
# Dry run (default); writes a review report and changes no database
.\LegacySacramentImport.exe --input .\legacy-sacraments.xlsm --database .\MemberRecords.sqlite --report .\import-report.csv

# Dry run with parish spelling lookup
.\LegacySacramentImport.exe --input .\legacy-sacraments.xlsm --database .\MemberRecords.sqlite --lookup .\Lookup.xlsx --report .\import-report.csv

# Commit only after reviewing the dry-run report
.\LegacySacramentImport.exe --input .\legacy-sacraments.xlsm --database .\MemberRecords.sqlite --lookup .\Lookup.xlsx --commit --report .\import-report.csv
```

The same `--input` option accepts a wedding `.csv`, a PDS export `.csv`, or a Flocknote member export `.xlsx` (each detected from its header row). Run `--help` for the complete usage line. A zero exit code means the run completed without errors; exit code `2` indicates validation errors and a rolled-back commit or dry-run findings. Exit code `1` indicates a command or file error.

## Review the report

All imports share one report file (default `import-audit.csv`). The first column, `Source`, is `legacy-sacraments`, `weddings`, `pds`, or `flocknote-members`; each run replaces only the rows for its own source and keeps the others. The remaining columns are source sheet/row, entry type, severity, action, person identifier, field, and message. Resolve errors before committing. Pay particular attention to duplicate-person warnings, ambiguous identity matches, unparsed dates, missing required columns, and unmapped parish spellings.

The importer matches by legacy person ID when available, then uses conservative name/date rules. It never merges possible duplicates automatically. Baptism entries are authoritative for baptism details; redundant person-level baptism fields are cleared when a baptism entry is present. The import also records an audit event when committed.

After a successful commit, open the resulting database through the application workflow, inspect representative people and entries, and publish it as an `.srdb` package. Do not place an importer-created live SQLite file directly in the shared package location.

## Link Flocknote members

A Flocknote member export (`.xlsx` with `Flocknote ID` and `Member Unique ID (PDS)` columns) links existing people to Flocknote. It records only the Flocknote ID; it does not create people or import sacrament data. Run it after the PDS import so registry people carry their PDS IDs.

```powershell
.\LegacySacramentImport.exe --input '.\Flocknote Member.xlsx' --database .\MemberRecords.sqlite --report .\import-audit.csv
```

Each Flocknote row is matched to the registry person with the same PDS ID, and the first name or surname (including maiden name and nickname) must agree. The importer skips and reports rows when:

- the PDS ID is not in the registry, or is held by more than one registry person;
- the names disagree (for example a "Mobile Phone Registrant" profile);
- several Flocknote profiles share the PDS ID and the person's name;
- the person is already linked to a different Flocknote ID, or the Flocknote ID is already linked to someone else.

Rows without a PDS ID cannot be linked here; match them in the application through **Flocknote > Import from Flocknote > All rows in the snapshot**. Sacrament details are then imported through the same window.
