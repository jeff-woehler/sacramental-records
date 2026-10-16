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

The same `--input` option accepts a wedding `.csv`. Run `--help` for the complete usage line. A zero exit code means the run completed without errors; exit code `2` indicates validation errors and a rolled-back commit or dry-run findings. Exit code `1` indicates a command or file error.

## Review the report

The report is CSV and includes source sheet/row, entry type, severity, action, person identifier, field, and message. Resolve errors before committing. Pay particular attention to duplicate-person warnings, ambiguous identity matches, unparsed dates, missing required columns, and unmapped parish spellings.

The importer matches by legacy person ID when available, then uses conservative name/date rules. It never merges possible duplicates automatically. Baptism entries are authoritative for baptism details; redundant person-level baptism fields are cleared when a baptism entry is present. The import also records an audit event when committed.

After a successful commit, open the resulting database through the application workflow, inspect representative people and entries, and publish it as an `.srdb` package. Do not place an importer-created live SQLite file directly in the shared package location.
