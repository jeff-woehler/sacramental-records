#requires -Version 5.1
<#
.SYNOPSIS
  Builds the app and publishes it as the latest release for office PCs to auto-update from.
.EXAMPLE
  .\Publish-Release.ps1 -Notes "Fixed certificate layout"
#>
param(
    [string] $ReleaseFolder = (Join-Path $PSScriptRoot '..\releases'),
    [string] $Notes = "",
    [switch] $NoPush
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $ReleaseFolder)) { New-Item -ItemType Directory -Path $ReleaseFolder | Out-Null }
$project = Join-Path $PSScriptRoot '..\src\SacramentalRecords.App\SacramentalRecords.App.csproj' | Resolve-Path
$version = ([xml](Get-Content $project)).Project.PropertyGroup.AssemblyVersion | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "AssemblyVersion not found in $project" }

$manifestPath = Join-Path $ReleaseFolder 'latest.json'
if (Test-Path $manifestPath) {
    $published = (Get-Content $manifestPath -Raw | ConvertFrom-Json).version
    if ([version]$version -le [version]$published) {
        throw "AssemblyVersion $version is not newer than the published $published. Bump <AssemblyVersion> in the .csproj first."
    }
}

$publishDir = Join-Path ([IO.Path]::GetTempPath()) "SacramentalRecordsPublish-$version"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained false -o $publishDir -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# Each PC keeps its own settings; parish data never ships with the program.
Remove-Item (Join-Path $publishDir 'SacramentalRecords.settings.json') -ErrorAction SilentlyContinue
$forbidden = Get-ChildItem $publishDir -Recurse -File -Include *.dbpackage, *.bak, *.sqlite, *.csv, *.xlsx, *.lnk
if ($forbidden) { throw "Refusing to publish data files: $($forbidden.FullName -join ', ')" }

$packageName = "SacramentalRecords-$version.zip"
$packagePath = Join-Path $ReleaseFolder $packageName
Get-ChildItem $ReleaseFolder -Filter 'SacramentalRecords-*.zip' | Remove-Item -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $packagePath)
Remove-Item $publishDir -Recurse -Force

[ordered]@{
    version = $version
    package = $packageName
    sha256  = (Get-FileHash $packagePath -Algorithm SHA256).Hash
    notes   = $Notes
} | ConvertTo-Json | Set-Content $manifestPath -Encoding UTF8

Write-Host "Release $version written to $ReleaseFolder"

if (-not $NoPush) {
    $repoRoot = Join-Path $PSScriptRoot '..' | Resolve-Path
    # Commit the whole repo so the AssemblyVersion bump ships with its release, not just the zip/manifest.
    git -C $repoRoot add -A
    git -C $repoRoot commit -m "Release $version"
    git -C $repoRoot push
}
