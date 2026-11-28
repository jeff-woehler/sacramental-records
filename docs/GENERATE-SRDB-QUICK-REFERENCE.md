# Quick Reference: Generating Sacramental Records Database

## One-Command Database Generation (Recommended)

```powershell
.\scripts\generate-srdb.ps1 -SourceDirectory "sample-data\20261127" -CommitChanges
```

This runs all five imports in sequence with actual database changes.

## Workflow

1. **Preview First** (always recommended):
   ```powershell
   .\scripts\generate-srdb.ps1 -SourceDirectory "sample-data\20261127"
   ```
   Reviews what would be imported without making changes.

2. **Review Audit Report**:
   - Opens: `sample-data\20261127\import-audit.csv`
   - Check for warnings and errors in the preview

3. **Execute Import** (when ready):
   ```powershell
   .\scripts\generate-srdb.ps1 -SourceDirectory "sample-data\20261127" -CommitChanges
   ```

## What Gets Imported

| Order | Source | Records | Purpose |
|-------|--------|---------|---------|
| 1 | legacy-sacraments.xlsm | 20,376 | Main registry data |
| 2 | PDS-export.csv | 4,511 people | Enrichment data |
| 3 | Weddings.csv | 6,958 | Marriage records |
| 4 | Flocknote Member.xlsx | 5,762 people | Flocknote linking |
| 5 | Flocknote Sacrament.xlsx | 9,086 | Sacrament details |

## Output Files

After running with `-CommitChanges`:

- **Database**: `sample-data\20261127\MemberRecords.sqlite` (~17 MB with full import)
- **Audit Report**: `sample-data\20261127\import-audit.csv` (~53,000 lines)

## Expected Results

```
[1/5] Legacy Sacraments: 20,376 entries written ✓
[2/5] PDS Export: 9,386 entries added ✓
[3/5] Weddings: 6,953 entries written (some validation errors rolled back)
[4/5] Flocknote Member: 4,347 people linked ✓
[5/5] Flocknote Sacrament: 7,576 sacraments merged ✓

Result: Database with ~40,000 people, ~45,000 sacrament records
```

## Troubleshooting

| Issue | Solution |
|-------|----------|
| "Unknown argument" error | Rebuild solution: `dotnet build src\LegacySacramentImport\LegacySacramentImport.csproj -c Release` |
| Script won't run | Allow scripts: `Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser` |
| File not found | Verify all files exist in source directory (legacy-sacraments.xlsm required) |
| Script is slow | Normal - PDS and Flocknote imports take time due to data volume |

## Documentation

- **Full Usage Guide**: [GENERATE-SRDB-USAGE.md](GENERATE-SRDB-USAGE.md)
- **Implementation Details**: [FLOCKNOTE-SACRAMENT-IMPORT.md](FLOCKNOTE-SACRAMENT-IMPORT.md)

## Key Facts

✓ Processes all five sources in one command  
✓ Accumulates data across all imports  
✓ Dry-run mode by default (safe)  
✓ Single audit report for all imports  
✓ Database and audit report in source directory  
✓ Gracefully skips optional imports if missing  
✓ All exit codes and reporting in audit CSV  
