#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Complete legacy sacrament import pipeline - processes all source files in proper order
    
.DESCRIPTION
    Runs a complete import sequence for sacramental records:
    1. Legacy sacraments (xlsm) - Main data source, creates people and sacraments
    2. PDS export (csv) - Enriches existing people with PDS data
    3. Weddings (csv) - Additional marriage sacrament records  
    4. Flocknote Member (xlsx) - Links Flocknote IDs to registry people
    5. Flocknote Sacrament (xlsx) - Imports sacrament details from Flocknote
    
    All imports use the same database with accumulated data, audit reports are appended.
    Final database and audit report are placed in the source directory.
    
.PARAMETER SourceDirectory
    Directory containing all import source files (default: current directory)
    
.PARAMETER DatabaseName
    Name for the output database (default: MemberRecords.sqlite)
    
.PARAMETER LookupFile
    Optional Lookup.xlsx for parish spelling canonicalization
    
.PARAMETER CommitChanges
    If specified, commits changes; otherwise runs dry-run only
    
.EXAMPLE
    .\run-complete-import.ps1 -SourceDirectory "D:\sample-data\20261127" -CommitChanges
    
    Runs all imports in sequence with actual database changes
#>

param(
    [string]$SourceDirectory = (Get-Location),
    [string]$DatabaseName = "MemberRecords.sqlite",
    [string]$LookupFile = $null,
    [switch]$CommitChanges
)

$ErrorActionPreference = "Stop"

# Resolve paths
$sourceDir = Resolve-Path $SourceDirectory -ErrorAction Stop
$dbPath = Join-Path $sourceDir $DatabaseName
$reportPath = Join-Path $sourceDir "import-audit.csv"
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Get-Location }
$repoRoot = Split-Path -Parent $scriptDir
$importerPath = Join-Path $repoRoot "src\LegacySacramentImport\bin\Release\net8.0\LegacySacramentImport.exe"
if (-not (Test-Path $importerPath)) {
    Write-Host "ERROR: Importer executable not found at $importerPath" -ForegroundColor Red
    exit 1
}

Write-Host "=============================================================================" -ForegroundColor Cyan
Write-Host "Complete Sacramental Records Import Pipeline" -ForegroundColor Cyan
Write-Host "=============================================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Source Directory:  $sourceDir"
Write-Host "Output Database:   $dbPath"
Write-Host "Audit Report:      $reportPath"
Write-Host "Mode:              $(if ($CommitChanges) { 'COMMIT' } else { 'DRY-RUN' })"
Write-Host ""

# Verify all required files exist
Write-Host "Checking required source files..." -ForegroundColor Yellow
$requiredFiles = @{
    "legacy-sacraments.xlsm" = "Legacy sacraments (main source)"
    "PDS-export.csv" = "PDS member export (optional)"
    "Weddings.csv" = "Wedding records (optional)"
    "Flocknote Member.xlsx" = "Flocknote member export (optional)"
    "Flocknote Sacrament.xlsx" = "Flocknote sacrament export (optional)"
}

$missingFiles = @()
foreach ($file in $requiredFiles.Keys)
{
    $path = Join-Path $sourceDir $file
    if (Test-Path $path)
    {
        Write-Host "  OK  $file" -ForegroundColor Green
    }
    else
    {
        Write-Host "  XX  $file (MISSING)" -ForegroundColor Red
        if ($requiredFiles[$file] -notmatch "optional")
        {
            $missingFiles += $file
        }
    }
}

if ($missingFiles.Count -gt 0)
{
    Write-Host ""
    Write-Host "ERROR: Required files are missing:" -ForegroundColor Red
    foreach ($file in $missingFiles)
    {
        Write-Host "  - $file" -ForegroundColor Red
    }
    exit 1
}

# Clean up old database if it exists
if (Test-Path $dbPath)
{
    Write-Host ""
    Write-Host "Removing existing database..." -ForegroundColor Yellow
    Remove-Item $dbPath -Force
}

Write-Host ""
Write-Host "Starting import sequence..." -ForegroundColor Cyan
Write-Host ""

$commitFlag = if ($CommitChanges) { @("--commit") } else { @() }
$lookupFlag = if ($LookupFile -and (Test-Path $LookupFile)) { @("--lookup", $LookupFile) } else { @() }
$stepNumber = 1

# Step 1: Legacy sacraments (main source - creates people and sacraments)
Write-Host "[$stepNumber/5] Legacy Sacraments" -ForegroundColor Cyan
Write-Host "File: legacy-sacraments.xlsm" -ForegroundColor Gray
$legacyPath = Join-Path $sourceDir "legacy-sacraments.xlsm"
$cmdArgs = @("--input", "$legacyPath", "--database", "$dbPath", "--report", "$reportPath") + $lookupFlag + $commitFlag
& $importerPath @cmdArgs
if ($LASTEXITCODE -eq 2)
{
    Write-Host "WARNING: Import completed with validation errors" -ForegroundColor Yellow
}
elseif ($LASTEXITCODE -eq 1)
{
    Write-Host "ERROR: Import failed with command/file error" -ForegroundColor Red
    exit 1
}
else
{
    Write-Host "OK: Legacy sacraments import completed" -ForegroundColor Green
}
Write-Host ""
$stepNumber++

# Step 2: PDS export (enriches/matches existing people)
$pdsPath = Join-Path $sourceDir "PDS-export.csv"
if (Test-Path $pdsPath)
{
    Write-Host "[$stepNumber/5] PDS Member Export" -ForegroundColor Cyan
    Write-Host "File: PDS-export.csv" -ForegroundColor Gray
    $cmdArgs = @("--input", "$pdsPath", "--database", "$dbPath", "--report", "$reportPath") + $lookupFlag + $commitFlag
    & $importerPath @cmdArgs
    if ($LASTEXITCODE -eq 2)
    {
        Write-Host "WARNING: Import completed with validation errors" -ForegroundColor Yellow
    }
    elseif ($LASTEXITCODE -eq 1)
    {
        Write-Host "ERROR: Import failed with command/file error" -ForegroundColor Red
        exit 1
    }
    else
    {
        Write-Host "OK: PDS export import completed" -ForegroundColor Green
    }
    Write-Host ""
}
else
{
    Write-Host "[$stepNumber/5] PDS Member Export (SKIPPED - file not found)" -ForegroundColor Gray
    Write-Host ""
}
$stepNumber++

# Step 3: Weddings (additional marriage sacrament records)
$weddingPath = Join-Path $sourceDir "Weddings.csv"
if (Test-Path $weddingPath)
{
    Write-Host "[$stepNumber/5] Wedding Records" -ForegroundColor Cyan
    Write-Host "File: Weddings.csv" -ForegroundColor Gray
    $cmdArgs = @("--input", "$weddingPath", "--database", "$dbPath", "--report", "$reportPath") + $commitFlag
    & $importerPath @cmdArgs
    if ($LASTEXITCODE -eq 2)
    {
        Write-Host "WARNING: Import completed with validation errors" -ForegroundColor Yellow
    }
    elseif ($LASTEXITCODE -eq 1)
    {
        Write-Host "ERROR: Import failed with command/file error" -ForegroundColor Red
        exit 1
    }
    else
    {
        Write-Host "OK: Wedding records import completed" -ForegroundColor Green
    }
    Write-Host ""
}
else
{
    Write-Host "[$stepNumber/5] Wedding Records (SKIPPED - file not found)" -ForegroundColor Gray
    Write-Host ""
}
$stepNumber++

# Step 4: Flocknote Member (links Flocknote IDs to registry people)
$flocknoteMemberPath = Join-Path $sourceDir "Flocknote Member.xlsx"
if (Test-Path $flocknoteMemberPath)
{
    Write-Host "[$stepNumber/5] Flocknote Member Linking" -ForegroundColor Cyan
    Write-Host "File: Flocknote Member.xlsx" -ForegroundColor Gray
    $cmdArgs = @("--input", "$flocknoteMemberPath", "--database", "$dbPath", "--report", "$reportPath") + $commitFlag
    & $importerPath @cmdArgs
    if ($LASTEXITCODE -eq 2)
    {
        Write-Host "WARNING: Import completed with validation errors" -ForegroundColor Yellow
    }
    elseif ($LASTEXITCODE -eq 1)
    {
        Write-Host "ERROR: Import failed with command/file error" -ForegroundColor Red
        exit 1
    }
    else
    {
        Write-Host "OK: Flocknote member linking completed" -ForegroundColor Green
    }
    Write-Host ""
}
else
{
    Write-Host "[$stepNumber/5] Flocknote Member Linking (SKIPPED - file not found)" -ForegroundColor Gray
    Write-Host ""
}
$stepNumber++

# Step 5: Flocknote Sacrament (imports sacrament details from Flocknote)
$flocknoteExchangePath = Join-Path $sourceDir "Flocknote Sacrament.xlsx"
if (Test-Path $flocknoteExchangePath)
{
    Write-Host "[$stepNumber/5] Flocknote Sacrament Exchange" -ForegroundColor Cyan
    Write-Host "File: Flocknote Sacrament.xlsx" -ForegroundColor Gray
    $cmdArgs = @("--input", "$flocknoteExchangePath", "--database", "$dbPath", "--report", "$reportPath") + $commitFlag
    & $importerPath @cmdArgs
    if ($LASTEXITCODE -eq 2)
    {
        Write-Host "WARNING: Import completed with validation errors" -ForegroundColor Yellow
    }
    elseif ($LASTEXITCODE -eq 1)
    {
        Write-Host "ERROR: Import failed with command/file error" -ForegroundColor Red
        exit 1
    }
    else
    {
        Write-Host "OK: Flocknote sacrament exchange completed" -ForegroundColor Green
    }
    Write-Host ""
}
else
{
    Write-Host "[$stepNumber/5] Flocknote Sacrament Exchange (SKIPPED - file not found)" -ForegroundColor Gray
    Write-Host ""
}

Write-Host "=============================================================================" -ForegroundColor Cyan
Write-Host "Import Pipeline Complete" -ForegroundColor Cyan
Write-Host "=============================================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Database location:  $dbPath"
Write-Host "Audit report:       $reportPath"
Write-Host ""

if (Test-Path $dbPath)
{
    $dbSize = (Get-Item $dbPath).Length / 1MB
    Write-Host "Database size:      $([math]::Round($dbSize, 2)) MB" -ForegroundColor Green
}
else
{
    Write-Host "WARNING: Database file was not created" -ForegroundColor Yellow
}

if (Test-Path $reportPath)
{
    $reportLines = @(Get-Content $reportPath).Count
    Write-Host "Audit report lines: $reportLines" -ForegroundColor Green
}
else
{
    Write-Host "WARNING: Audit report was not created" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "To review audit report, open:" -ForegroundColor Gray
Write-Host "  $reportPath" -ForegroundColor Cyan
Write-Host ""

exit 0
