# Sacramental Records Database Generation Script Usage Guide

## Overview

The `generate-srdb.ps1` PowerShell script orchestrates a complete, single-pass import of sacramental records from all five data sources into a SQLite database. The script runs all imports in the proper sequence, accumulating data across each step.

## Quick Start

### Basic Usage (Dry-Run Mode - Recommended First)

```powershell
.\scripts\generate-srdb.ps1 -SourceDirectory "sample-data\20261127"
```

This performs a dry-run (no database changes) and generates an audit report showing what would be imported.

### With Database Commits

```powershell
.\scripts\generate-srdb.ps1 -SourceDirectory "sample-data\20261127" -CommitChanges
```

This performs the actual import and writes all changes to the database.

## Import Sequence

The script runs five import steps in this order:

| Step | Source File | Purpose | Required |
|------|-------------|---------|----------|
| 1 | legacy-sacraments.xlsm | Main sacrament data source; creates registry people and their sacraments | YES |
| 2 | PDS-export.csv | Enriches existing people with PDS member data | NO* |
| 3 | Weddings.csv | Additional marriage sacrament records | NO* |
| 4 | Flocknote Member.xlsx | Links Flocknote IDs to registry people for future sync | NO* |
| 5 | Flocknote Sacrament.xlsx | Merges Flocknote sacrament details into matched registry people | NO* |

*Optional imports are skipped gracefully if the files are missing.

## Parameters

### `-SourceDirectory`
**Type:** String  
**Default:** Current directory  
**Example:** `"sample-data\20261127"`

The directory containing all import source files. The database and audit report will be written to this same directory.

### `-DatabaseName`
**Type:** String  
**Default:** `MemberRecords.sqlite`  
**Example:** `"registry-import-20261127.sqlite"`

The name for the output SQLite database file. It will be created in the SourceDirectory.

### `-LookupFile`
**Type:** String  
**Default:** None (optional)  
**Example:** `"sample-data\20261127\Lookup.xlsx"`

Path to Lookup.xlsx for parish spelling canonicalization. Only used by legacy sacraments import if provided.

### `-CommitChanges`
**Type:** Switch (flag)  
**Default:** Dry-run mode (no flag)

When specified, commits all changes to the database. Without this flag, the script performs a dry-run showing what would be imported without making any database changes.

## Examples

### Example 1: Dry-Run with Default Settings
```powershell
.\scripts\generate-srdb.ps1 -SourceDirectory "sample-data\20261127"
```

Output: Dry-run audit report without database changes.

### Example 2: Full Import with Lookup File
```powershell
.\scripts\generate-srdb.ps1 -SourceDirectory "sample-data\20261127" `
                            -LookupFile "sample-data\20261127\Lookup.xlsx" `
                            -CommitChanges
```

Output: Creates database with parish spellings canonicalized using Lookup.xlsx.

### Example 3: Custom Database Name
```powershell
.\scripts\generate-srdb.ps1 -SourceDirectory "D:\production-import\20261128" `
                           -DatabaseName "prod-registry.sqlite" `
                           -CommitChanges
```

Output: Creates D:\production-import\20261128\prod-registry.sqlite with all imported data.

## Output Files

After running the script, the following files will be created in the SourceDirectory:

### Database
**File:** `MemberRecords.sqlite` (or custom name)  
**Size:** Typically 15-20 MB with full import  
**Contents:** Complete registry with all people, sacraments, and links

### Audit Report
**File:** `import-audit.csv`  
**Size:** Typically 50,000+ lines with full import  
**Format:** CSV with columns:
- `Source` - Import source (legacy-sacraments, PDS-export, etc.)
- `Sheet` - Source sheet/section name
- `Row` - Row number in source file
- `Type` - Data type (baptism, confirmation, marriage, etc.)
- `Severity` - Level (info, warning, error)
- `Action` - What was done (create-person, insert-sacrament, skip, etc.)
- `PersonId` - Registry person ID if applicable
- `Field` - Specific field being reported on
- `Message` - Detailed message

## Understanding Import Results

After each step, the script displays a summary showing:
- Rows read from source file
- People created/matched/linked
- Entries written/merged
- Warnings and errors
- Exit status

### Exit Codes
- `0` - Success
- `1` - Command/file error (import aborted)
- `2` - Validation errors (import attempted but rolled back due to errors)

The script shows detailed exit status after each step:
- **OK** (green) - Import completed successfully
- **WARNING** (yellow) - Validation errors found (dry-run mode shows what would happen)
- **ERROR** (red) - Import failed completely

## Important Notes

1. **Dry-Run First**: Always run with dry-run mode (no `-CommitChanges`) to preview changes before committing.

2. **Sequence Matters**: Imports must run in this order. The script enforces this automatically.

3. **Accumulation**: Each import step adds to the same database, accumulating data across all five sources.

4. **Legacy Sacraments Required**: The legacy-sacraments.xlsm file must be present and valid. The script will exit if this file is missing.

5. **Optional Files**: PDS, Weddings, Flocknote Member, and Flocknote Sacrament files are skipped gracefully if missing.

6. **Validation Errors**: If validation errors occur during an import step, the transaction is rolled back and that step is skipped (the script continues to the next step).

7. **Audit Trail**: All operations are logged to the audit CSV, including what was skipped and why.

8. **File Names**: The script uses the latest files from the sample-data/20261127 folder. Ensure your source directory has these exact file names.

## Troubleshooting

### Script won't run
Ensure ExecutionPolicy allows script execution:
```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser
```

### File not found errors
Verify all required files exist in the SourceDirectory:
- legacy-sacraments.xlsm (required)
- PDS-export.csv (optional)
- Weddings.csv (optional)
- Flocknote Member.xlsx (optional)
- Flocknote Sacrament.xlsx (optional)

### Database is locked
Ensure no other process is accessing MemberRecords.sqlite. Close any database viewers or other import processes.

### Import seems slow
- PDS import is typically the slowest step (matching thousands of records)
- Flocknote Sacrament import processes thousands of records
- These are normal and expected; allow 2-5 minutes total for complete import

## Support Files

The script requires the following executable to be present:
- `src\LegacySacramentImport\bin\Release\net8.0\LegacySacramentImport.exe`

This is built as part of the solution. If missing, rebuild the solution:
```powershell
dotnet build src\LegacySacramentImport\LegacySacramentImport.csproj -c Release
```

## Example Session

```
PS D:\dev\sacramental-records> .\scripts\generate-srdb.ps1 -SourceDirectory "sample-data\20261127" -CommitChanges
=============================================================================
Complete Sacramental Records Import Pipeline
=============================================================================

Source Directory:  D:\dev\sacramental-records\sample-data\20261127
Output Database:   D:\dev\sacramental-records\sample-data\20261127\MemberRecords.sqlite
Audit Report:      D:\dev\sacramental-records\sample-data\20261127\import-audit.csv
Mode:              COMMIT

Checking required source files...
  OK  legacy-sacraments.xlsm
  OK  PDS-export.csv
  OK  Weddings.csv
  OK  Flocknote Member.xlsx
  OK  Flocknote Sacrament.xlsx

Starting import sequence...

[1/5] Legacy Sacraments
...
OK: Legacy sacraments import completed

[2/5] PDS Member Export
...
OK: PDS export import completed

...

=============================================================================
Import Pipeline Complete
=============================================================================

Database location:  D:\dev\sacramental-records\sample-data\20261127\MemberRecords.sqlite
Audit report:       D:\dev\sacramental-records\sample-data\20261127\import-audit.csv

Database size:      16.78 MB
Audit report lines: 53244
```
