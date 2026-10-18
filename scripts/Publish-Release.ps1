#requires -Version 5.1
<#
.SYNOPSIS
  Builds the app, commits the update manifest, and creates a GitHub Release.
  The GitHub token is read from GITHUB_TOKEN unless supplied explicitly.

.EXAMPLE
  .\Publish-Release.ps1 -Notes "Fixed certificate layout"
.EXAMPLE
  .\Publish-Release.ps1 -Notes "Fixed certificate layout" -IgnoreCommits
#>
param(
    [string] $ReleaseFolder = (Join-Path $PSScriptRoot '..\releases'),
    [string] $Notes = "",
    [string] $GitHubToken = $env:GITHUB_TOKEN,
    [switch] $IgnoreCommits,
    [switch] $NoPush
)

$ErrorActionPreference = 'Stop'
$repoRoot = Join-Path $PSScriptRoot '..' | Resolve-Path
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
$forbidden = Get-ChildItem $publishDir -Recurse -File -Include *.srdb, *.dbpackage, *.bak, *.sqlite, *.csv, *.xlsx, *.lnk
if ($forbidden) { throw "Refusing to publish data files: $($forbidden.FullName -join ', ')" }

$packageName = "SacramentalRecords-$version.zip"
$packagePath = Join-Path $ReleaseFolder $packageName

$commitNotes = @()
if (-not $IgnoreCommits) {
    # Don't pipe into Select-Object -First: it closes the pipe early and git exits non-zero.
    $tags = @(git -C $repoRoot tag --sort=-version:refname)
    if ($LASTEXITCODE -ne 0) { throw "Unable to determine the previous release tag." }
    $previousTag = $tags | Select-Object -First 1
    if ($previousTag) {
        $commitNotes = @(git -C $repoRoot log --format='- %s' "$previousTag..HEAD")
    }
        else {
        # A repository without tags has no release boundary, so include its history.
        $commitNotes = @(git -C $repoRoot log --format='- %s')
    }

    if ($LASTEXITCODE -ne 0) { throw "Unable to collect release commit comments." }
}
$noteSections = @()
if (-not [string]::IsNullOrWhiteSpace($Notes)) { $noteSections += $Notes.Trim() }
if ($commitNotes.Count -gt 0) { $noteSections += ($commitNotes -join "`n") }
$releaseNotes = $noteSections -join "`n`n"

Get-ChildItem $ReleaseFolder -Filter 'SacramentalRecords-*.zip' | Remove-Item -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $packagePath)
Remove-Item $publishDir -Recurse -Force

$manifestJson = [ordered]@{
    version = $version
    package = $packageName
    sha256  = (Get-FileHash $packagePath -Algorithm SHA256).Hash
    notes   = $releaseNotes
} | ConvertTo-Json
# Set-Content -Encoding UTF8 writes a BOM that System.Text.Json refuses to parse; write BOM-less UTF-8 instead.
[IO.File]::WriteAllText($manifestPath, $manifestJson, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Release files for $version written to $ReleaseFolder"
Write-Host "  Package:  $packagePath"
Write-Host "  Manifest: $manifestPath"

if (-not $NoPush) {
    $projectRelativePath = 'src/SacramentalRecords.App/SacramentalRecords.App.csproj'
    $releaseRelativePath = 'releases'

        
    # Keep the manifest in the repository for the updater, while publishing the

    # ZIP as an asset on the corresponding GitHub Release.
    # Stage only release output and the version source; never sweep unrelated
    # worktree changes into an automated release commit.
    git -C $repoRoot add -- $releaseRelativePath $projectRelativePath
    if ($LASTEXITCODE -ne 0) { throw "Unable to stage release files." }

    git -C $repoRoot commit -m "Release $version"
    if ($LASTEXITCODE -ne 0) { throw "Unable to commit release $version." }

    git -C $repoRoot push
    if ($LASTEXITCODE -ne 0) { throw "Unable to push release $version." }

    if ([string]::IsNullOrWhiteSpace($GitHubToken)) {

        throw "A GitHub token is required to create the release. Set GITHUB_TOKEN or use -GitHubToken."
    }

    $remote = git -C $repoRoot remote get-url origin
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($remote)) { throw "Unable to determine the GitHub repository from the origin remote." }
    if ($remote -match 'github\.com[:/]([^/]+)/([^/]+?)(?:\.git)?$') {
        $owner = $Matches[1]
        $repository = $Matches[2]
    }
    else { throw "The origin remote is not a GitHub repository: $remote" }

    $apiHeaders = @{
        Authorization = "Bearer $GitHubToken"
        Accept = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    $releaseBody = [ordered]@{
        tag_name = "v$version"
        name = "v$version"
        body = $releaseNotes
        draft = $false
        prerelease = $false
        generate_release_notes = $false
    } | ConvertTo-Json
    $apiUrl = "https://api.github.com/repos/$owner/$repository/releases"
    try {
        $release = Invoke-RestMethod -Method Post -Uri $apiUrl -Headers $apiHeaders -ContentType 'application/json' -Body $releaseBody
        $uploadUrl = $release.upload_url -replace '\{\?name,label\}$', "?name=$([uri]::EscapeDataString($packageName))"
        Invoke-RestMethod -Method Post -Uri $uploadUrl -Headers $apiHeaders -ContentType 'application/zip' -InFile $packagePath | Out-Null
        }
    catch {

        throw "GitHub Release creation failed: $($_.Exception.Message)"
    }
        Write-Host "GitHub Release v$version created at $($release.html_url)"
}





