<#
.SYNOPSIS
    Builds the RASTA release installer (RASTA-Setup.exe) and copies it into
    the repo-root Releases folder, named after the version in Directory.Build.props.

.DESCRIPTION
    - Reads <Version> from Directory.Build.props (single source of truth for
      every project's AssemblyVersion and the bundle's own Bundle/@Version).
    - Builds RASTA.Bundle.wixproj (which pulls in RASTA.Setup.wixproj, which
      in turn publishes RASTA.App) in the given configuration.
    - Copies the resulting RASTA-Setup.exe to
      Releases\RASTA-Setup-<version>.exe.
    - Releases\ is already covered by .gitignore's [Rr]eleases/ rule, so
      built installers never get committed.

.PARAMETER Configuration
    Build configuration to use. Defaults to Release.

.PARAMETER Force
    Overwrite an existing Releases\RASTA-Setup-<version>.exe if present.
    Without this, the script refuses to overwrite an existing release build
    (bump <Version> in Directory.Build.props for a new build instead).

.PARAMETER SkipBuild
    Skip the dotnet build step and just (re)copy whatever's already in
    RASTA.Bundle\bin\x64\<Configuration>\RASTA-Setup.exe. Useful for
    re-running the copy step after a manual build.

.EXAMPLE
    .\scripts\Build-Release.ps1
    Builds Release and produces Releases\RASTA-Setup-0.2.0.exe

.EXAMPLE
    .\scripts\Build-Release.ps1 -Force
    Same, but overwrites an existing file for that version.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$Force,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

# Script lives in <repoRoot>\scripts, so its parent is always the repo root
# regardless of the caller's own working directory.
$repoRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$bundleProj = Join-Path $repoRoot "RASTA.Bundle\RASTA.Bundle.wixproj"
$releasesDir = Join-Path $repoRoot "Releases"

if (-not (Test-Path $propsPath)) {
    throw "Could not find Directory.Build.props at $propsPath"
}

# Directory.Build.props is the single <Version> every project (and the
# bundle's own Bundle/@Version, via RASTA.Bundle.wixproj's
# DefineConstants=RastaVersion=$(Version)) picks up - read it here instead
# of taking a -Version parameter so the built installer and its filename
# can never disagree.
$propsContent = Get-Content -Raw $propsPath
if ($propsContent -notmatch '<Version>\s*([^<\s]+)\s*</Version>') {
    throw "Could not find <Version> in $propsPath"
}
$version = $Matches[1]
Write-Host "Release version (from Directory.Build.props): $version" -ForegroundColor Cyan

$destName = "RASTA-Setup-$version.exe"
$destPath = Join-Path $releasesDir $destName

# Fail fast, before spending minutes on a build, if this version's file is
# already there - avoids silently clobbering a previous release build.
if ((Test-Path $destPath) -and -not $Force) {
    throw "$destPath already exists. Bump <Version> in Directory.Build.props for a new release, or pass -Force to overwrite."
}

if (-not $SkipBuild) {
    Write-Host "Building $bundleProj ($Configuration)..." -ForegroundColor Cyan
    # Building RASTA.Bundle.wixproj transitively builds RASTA.Setup.wixproj
    # (ProjectReference), which publishes RASTA.App via its own
    # PrepareForBuild hook - one command produces the whole chained bootstrapper.
    & dotnet build $bundleProj -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        # See CLAUDE.md: WIX0350 (stale local MSI validation engine) has been a
        # machine-specific quirk here, not an authoring bug - worth one retry
        # with validation suppressed before giving up.
        Write-Warning "Build failed - retrying with -p:SuppressValidation=true (known WIX0350 machine-specific quirk, see CLAUDE.md)."
        & dotnet build $bundleProj -c $Configuration -p:SuppressValidation=true
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed for $bundleProj (exit code $LASTEXITCODE)."
        }
    }
}

# RASTA.Bundle.wixproj hardcodes OutputName=RASTA-Setup and Platform=x64, so
# the bin path below is fixed regardless of $Configuration.
$builtExe = Join-Path $repoRoot "RASTA.Bundle\bin\x64\$Configuration\RASTA-Setup.exe"
if (-not (Test-Path $builtExe)) {
    throw "Expected build output not found at $builtExe"
}

if (-not (Test-Path $releasesDir)) {
    New-Item -ItemType Directory -Path $releasesDir | Out-Null
}

# Releases\ is matched by .gitignore's [Rr]eleases/ rule, so these
# version-named installers are kept out of source control on purpose.
Copy-Item -Path $builtExe -Destination $destPath -Force
Write-Host "Release installer written to $destPath" -ForegroundColor Green
