<#
.SYNOPSIS
    Downloads LAB Survey HI spectra (via the AIfA EU-HOU LABprofile service) for a set
    of grid points, for use as synthetic test data when refining RASTA's Mosaic 2D/3D
    visualisation code (GridBuilder / HeatmapImageBuilder / MosaicSurfaceView). This is
    a dev/test data tool only — not part of the shipped app.

.DESCRIPTION
    Three ways to supply the grid:

    1. -PointsCsv <path> with RaHours/DecDeg columns  (from New-SweepGridPoints.ps1)
       Equatorial points, mirroring SweepPlanner's real cos(dec)-corrected row logic.

    2. -PointsCsv <path> with LDeg/BDeg columns  (from New-GalacticStripGridPoints.ps1)
       Galactic points - e.g. a strip walking along the Milky Way plane. Detected
       automatically from the CSV's column names; the service is queried with
       csys=0 (confirmed by probing: ral/decb are echoed back unchanged as l/b in
       the response header only when csys=0 - csys=-1 is equatorial, ral/decb as
       RA/Dec degrees).

    3. -RaMinDeg/-RaMaxDeg/-RaStepDeg + -DecMinDeg/-DecMaxDeg/-DecStepDeg
       A naive rectangular RA/Dec box, stepped uniformly in degrees on both axes (no
       cos(dec) row correction). Simplest, but doesn't match real sweep spacing -
       prefer a PointsCsv from one of the generator scripts when spacing matters.

    Either way, one LAB profile is downloaded per grid point. Files are named by
    degrees (not hours) to avoid the H/M/S input-vs-degrees-output confusion the web
    form itself is prone to. Already-downloaded files are skipped, so the script is
    safe to re-run/resume - including across multiple regions/strips into the same
    -OutDir, since filenames are unique per position.

    Please be considerate of the AIfA server — keep -DelaySeconds at a sane value and
    avoid firing off very large grids without reason.

.EXAMPLE
    ./Fetch-LabSurveyGrid.ps1 -PointsCsv C:\Raw\RASTA\LabSurveyTestData\cassiopeia_grid.csv -OutDir C:\Raw\RASTA\LabSurveyTestData

.EXAMPLE
    ./Fetch-LabSurveyGrid.ps1 -PointsCsv C:\Raw\RASTA\LabSurveyTestData\plane_l40to100.csv -OutDir C:\Raw\RASTA\LabSurveyTestData

.EXAMPLE
    ./Fetch-LabSurveyGrid.ps1 -RaMinDeg 0 -RaMaxDeg 40 -RaStepDeg 5 -DecMinDeg 30 -DecMaxDeg 70 -DecStepDeg 5 -OutDir C:\Raw\RASTA\LabSurveyTestData
#>
param(
    [string]$PointsCsv,

    [double]$RaMinDeg = 0,
    [double]$RaMaxDeg = 40,
    [double]$RaStepDeg = 5,

    [double]$DecMinDeg = 30,
    [double]$DecMaxDeg = 70,
    [double]$DecStepDeg = 5,

    [double]$BeamDeg = 10.5,

    [string]$OutDir = "C:\Raw\RASTA\LabSurveyTestData",

    [double]$DelaySeconds = 1.0
)

if (-not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
}

$baseUrl = "https://www.astro.uni-bonn.de/hisurvey/euhou/LABprofile/download.php"

# Each entry: @{ System = 'eq'|'gal'; A = <ral value>; B = <decb value> }
# 'eq'  -> A is RA in degrees, B is Dec in degrees, csys=-1
# 'gal' -> A is Galactic l in degrees, B is Galactic b in degrees, csys=0
$gridPoints = @()

if ($PointsCsv) {
    if (-not (Test-Path $PointsCsv)) {
        throw "PointsCsv not found: $PointsCsv"
    }
    $rows = Import-Csv -Path $PointsCsv
    if ($rows.Count -eq 0) {
        throw "PointsCsv has no rows: $PointsCsv"
    }
    $columns = $rows[0].PSObject.Properties.Name

    if ($columns -contains 'RaHours' -and $columns -contains 'DecDeg') {
        foreach ($row in $rows) {
            $gridPoints += @{ System = 'eq'; A = [double]$row.RaHours * 15.0; B = [double]$row.DecDeg }
        }
        Write-Output "Loaded $($gridPoints.Count) equatorial grid points from $PointsCsv"
    }
    elseif ($columns -contains 'LDeg' -and $columns -contains 'BDeg') {
        foreach ($row in $rows) {
            $gridPoints += @{ System = 'gal'; A = [double]$row.LDeg; B = [double]$row.BDeg }
        }
        Write-Output "Loaded $($gridPoints.Count) Galactic grid points from $PointsCsv"
    }
    else {
        throw "PointsCsv columns not recognised (expected RaHours/DecDeg or LDeg/BDeg): $($columns -join ', ')"
    }
}
else {
    $raValues = @()
    for ($ra = $RaMinDeg; $ra -le $RaMaxDeg + 1e-9; $ra += $RaStepDeg) { $raValues += [math]::Round($ra, 3) }

    $decValues = @()
    for ($dec = $DecMinDeg; $dec -le $DecMaxDeg + 1e-9; $dec += $DecStepDeg) { $decValues += [math]::Round($dec, 3) }

    foreach ($ra in $raValues) {
        foreach ($dec in $decValues) {
            $gridPoints += @{ System = 'eq'; A = $ra; B = $dec }
        }
    }
    Write-Output "Built $($gridPoints.Count) grid points ($($raValues.Count) RA x $($decValues.Count) Dec, rectangular box - no cos(dec) correction)"
}

$total = $gridPoints.Count
$done = 0
$downloaded = 0
$skipped = 0
$failed = 0

Write-Output "Fetching $total grid points into $OutDir"

foreach ($point in $gridPoints) {
    $a = [math]::Round($point.A, 3)
    $b = [math]::Round($point.B, 3)
    $done++

    $aStr = ('{0:0.0}' -f $a) -replace '\.', 'p'
    $bStr = ('{0:0.0}' -f $b) -replace '\.', 'p'
    $bSign = if ($b -ge 0) { "+" } else { "" }

    if ($point.System -eq 'gal') {
        $csys = 0
        $fileName = "lab_L${aStr}deg_B${bSign}${bStr}deg.txt"
        $label = "L=$a B=$b"
    }
    else {
        $csys = -1
        $fileName = "lab_RA${aStr}deg_DEC${bSign}${bStr}deg.txt"
        $label = "RA=$a Dec=$b"
    }

    $outPath = Join-Path $OutDir $fileName

    if (Test-Path $outPath) {
        $skipped++
        continue
    }

    $url = "$baseUrl`?ral=$a&decb=$b&csys=$csys&beam=$BeamDeg"

    try {
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 30
        $content = $response.Content

        if ($content -notmatch '%%LAB') {
            Write-Warning "[$done/$total] $label did not look like a LAB profile response - skipping save"
            $failed++
            continue
        }

        Set-Content -Path $outPath -Value $content -NoNewline
        $downloaded++
        Write-Output "[$done/$total] Saved $label -> $fileName"
    }
    catch {
        Write-Warning "[$done/$total] Failed $label : $($_.Exception.Message)"
        $failed++
    }

    Start-Sleep -Seconds $DelaySeconds
}

Write-Output "Done. Downloaded=$downloaded Skipped(existing)=$skipped Failed=$failed Total=$total"
