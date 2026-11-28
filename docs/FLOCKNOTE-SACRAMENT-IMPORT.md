# Flocknote Sacrament Import Implementation Summary

## Overview

Implemented complete Flocknote Sacrament import capability for the LegacySacramentImport application, enabling single-pass import of sacrament data from Flocknote exports (.xlsx files) with standard reporting, audit trails, and transaction support. Additionally, created a comprehensive PowerShell orchestration script for running all five import sources in proper sequence.

## Implementation Details

### Core Feature: Flocknote Sacrament Exchange

**File:** `src/LegacySacramentImport/FlocknoteExchange.cs` (370 lines)

#### Key Components:

1. **File Detection** (`IsFlocknoteExchangeFile`)
   - Detects Flocknote Sacrament export files (.xlsx)
   - Checks for required columns: Flocknote ID, sacrament type columns
   - Validates file format before processing

2. **Main Orchestration** (`RunFlocknoteExchange`)
   - Coordinates entire import workflow
   - Opens database with proper transaction support
   - Handles --commit and dry-run modes
   - Provides comprehensive reporting to audit CSV
   - Proper exit codes: 0=success, 2=validation errors, 1=command errors

3. **Data Reading** (`ReadFlocknoteExchangeFile`)
   - Parses .xlsx files using ClosedXML
   - Case-insensitive column header matching
   - Flexible sacrament detection (prefix-based: "Baptism Date", "First Communion Location", etc.)
   - Handles all sacrament types: Baptism, First Communion, Confirmation, Marriage, Holy Orders

4. **Person Matching**
   - Primary matching by Flocknote ID (most reliable)
   - Secondary by PDS ID (prepared for future use)
   - Skip unmatchable records with warning

5. **Sacrament Merging**
   - Inserts new sacrament records with all fields:
     - Date, Location, Celebrant
     - Sponsors (comma-separated)
     - Parents (comma-separated)
     - Registry Book info (Volume, Page, Item #)
     - Details and Notes
   - Checks for existing sacraments before inserting (skips if already present)
   - Reports merged vs. skipped counts

6. **Reporting**
   - Follows legacy import convention with info/warning/error severity
   - Each row reports: source, row number, type, severity, action, person ID, field, message
   - Appends to shared audit CSV across all imports
   - Reports: rows processed, sacraments merged, skipped, warnings, errors

#### Column Mapping

Flocknote uses prefix-based headers for each sacrament type:

| Sacrament Type | Date Column | Location Column | Celebrant Column | Sponsors Column | Parents Column |
|---|---|---|---|---|---|
| **Baptism** | Baptism Date | Baptism Location | Baptism Celebrant | Baptism Sponsors | Baptism Parents |
| **First Communion** | First Communion Date | First Communion Location | First Communion Celebrant | First Communion Sponsors | - |
| **Confirmation** | Confirmation Date | Confirmation Location | Confirmation Celebrant | Confirmation Sponsors | - |
| **Marriage** | Marriage Date | Marriage Location | Marriage Celebrant | - | - |
| **Holy Orders** | Holy Orders Date | Holy Orders Location | Holy Orders Celebrant | - | - |

Additional columns matched: `Registry Volume`, `Registry Page`, `Registry Item#`, `Details`, `Notes`

### Program.cs Integration

**Routing Logic:**
```csharp
if (IsFlocknoteExchangeFile(options.Input))
    return RunFlocknoteExchange(options);
```

**Updated Usage Text:**
Includes "Flocknote Sacrament.xlsx" as a recognized file type option.

### Complete Import Orchestration Script

**File:** `run-complete-import.ps1` (280+ lines)

#### Purpose
Runs all five import sources in proper sequence into a single database, accumulating data across each step.

#### Workflow
1. **Legacy Sacraments** → Creates main registry people and sacraments
2. **PDS Export** → Enriches people with PDS member data
3. **Weddings** → Adds marriage sacrament records
4. **Flocknote Member** → Links Flocknote IDs to registry people
5. **Flocknote Sacrament** → Merges sacrament details from Flocknote

#### Features
- **Flexible Source Directory**: All files must be in one directory; specify with `-SourceDirectory`
- **Dry-Run Safe**: Default behavior shows what would be imported without database changes
- **Commit Mode**: Use `-CommitChanges` flag to write to database
- **Optional Files**: PDS, Weddings, Flocknote imports gracefully skip if files missing
- **Legacy Required**: Only legacy-sacraments.xlsm is required; others enhance but don't block
- **Proper Error Handling**: Validates file existence, reports detailed status, respects exit codes
- **Accumulation**: Each import step uses same database, accumulating data
- **Audit Trail**: All imports append to single import-audit.csv report

#### Usage
```powershell
# Dry-run (recommended first)
.\run-complete-import.ps1 -SourceDirectory "sample-data\20261127"

# With database commits
.\run-complete-import.ps1 -SourceDirectory "sample-data\20261127" -CommitChanges

# With Lookup file and custom database name
.\run-complete-import.ps1 -SourceDirectory "data\20261127" `
                         -DatabaseName "prod-registry.sqlite" `
                         -LookupFile "data\20261127\Lookup.xlsx" `
                         -CommitChanges
```

#### Output
- **Database**: `MemberRecords.sqlite` (or custom name) in SourceDirectory
- **Audit Report**: `import-audit.csv` in SourceDirectory
- **Console Output**: Detailed status for each import step with green/yellow/red indicators

## Test Results

### Complete Import Pipeline Test (with --commit)
```
[1/5] Legacy Sacraments: 20,376 rows → 20,376 entries written ✓
[2/5] PDS Export: 4,511 rows → 9,386 entries added ✓
[3/5] Weddings: 6,958 rows → 6,953 entries written (5 errors, rolled back as expected)
[4/5] Flocknote Member: 5,762 rows → 4,347 people linked ✓
[5/5] Flocknote Sacrament: 9,086 rows → 7,576 sacraments merged ✓

Result:
- Database: 16.78 MB with complete import
- Audit Report: 53,244 lines
- Exit Code: 0 (success)
```

## Files Created/Modified

### New Files
- `src/LegacySacramentImport/FlocknoteExchange.cs` - Flocknote sacrament import implementation
- `run-complete-import.ps1` - PowerShell orchestration script
- `IMPORT-SCRIPT-USAGE.md` - Comprehensive usage guide
- `FLOCKNOTE-SACRAMENT-IMPLEMENTATION.md` - This document

### Modified Files
- `src/LegacySacramentImport/Program.cs`
  - Added `IsFlocknoteExchangeFile()` routing check
  - Updated usage text with Flocknote Sacrament option

## Build Status
✅ **Solution builds successfully with 0 errors, 0 warnings**

## Design Decisions

1. **Column Mapping Strategy**: Used case-insensitive prefix matching to handle Flocknote's flexible column naming
2. **Person Matching**: Primary by Flocknote ID (most reliable); secondary by PDS ID prepared for future implementation
3. **Script Flow**: Enforced strict sequence for imports to ensure proper data accumulation
4. **Error Handling**: Followed legacy import convention of rolling back on validation errors but continuing pipeline
5. **Reporting**: Single audit CSV file for entire pipeline, supporting 2000+ lines per import step
6. **Safety**: Dry-run mode by default; requires explicit `--commit` flag for database changes

## Key Features

✅ Single-pass Flocknote Sacrament import  
✅ Comprehensive info/warning/error reporting  
✅ Audit CSV trail of all operations  
✅ Transaction support with rollback on errors  
✅ Proper exit codes for scripting  
✅ Dry-run safe mode (default)  
✅ All five import sources orchestrated in proper order  
✅ Graceful handling of optional imports  
✅ Database and audit report output to source directory  
✅ PowerShell script with colored status indicators  

## Compatibility

- **Framework**: .NET 8.0
- **Database**: SQLite
- **File Formats**: 
  - Excel: .xlsx (Flocknote sacrament export)
  - CSV: .csv (PDS and Weddings imports)
  - Excel: .xlsm (Legacy sacraments - old Excel format)
- **Dependencies**: ClosedXML for Excel parsing (already in project)

## Documentation

Comprehensive usage guide provided in `IMPORT-SCRIPT-USAGE.md` including:
- Quick start examples
- Parameter descriptions
- Import sequence diagram
- Output file format explanation
- Troubleshooting guide
- Example sessions

## Next Steps (Optional Future Work)

1. **PDS ID Matching**: Implement secondary person matching by PDS ID when Flocknote ID not found
2. **Validation Enhancements**: Add specific validation rules for sacrament dates and celebrant names
3. **Flocknote Sync**: Build bidirectional sync to update Flocknote with registry information
4. **Scheduled Import**: Automate scheduled imports of Flocknote data
5. **Duplicate Detection**: Enhanced duplicate detection for sacraments with similar dates/locations/celebrants
