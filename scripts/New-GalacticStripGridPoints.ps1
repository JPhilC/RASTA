<#
.SYNOPSIS
    Generates a grid of Galactic (l,b) points along a strip of the Milky Way plane,
    for use as synthetic test data when refining RASTA's Mosaic 2D/3D visualisation
    code. Companion to New-SweepGridPoints.ps1 (which does the equivalent for an
    equatorial RA/Dec box) - same row-spacing math, just in Galactic coordinates,
    which RASTA's own SweepPlanner doesn't natively support as a plan type.

.DESCRIPTION
    Steps Galactic latitude b from -BHalfWidthDeg to +BHalfWidthDeg in rows spaced
    AngularSeparationDeg apart, and within each row steps longitude l across
    [LStartDeg, LEndDeg] with the step corrected by cos(b) so points stay the same
    true angular distance apart everywhere in the strip (matters more for a wide
    b range; negligible very close to b=0, included anyway for consistency and so
    a wider strip stays correct if you use one).

    l naturally wraps 0-360 - LStartDeg/LEndDeg can cross the 0/360 boundary
    (e.g. -20 to 40) since Step-Range is start/end-agnostic; just keep the values
    numerically continuous (e.g. use -20 rather than 340) and normalise afterwards
    if you need strict 0-360 output.

.EXAMPLE
    # A modest first slice of the plane, +/-5 deg either side of b=0, 3 deg spacing
    ./New-GalacticStripGridPoints.ps1 -LStartDeg 40 -LEndDeg 100 -BHalfWidthDeg 5 -AngularSeparationDeg 3 -OutCsv C:\Raw\RASTA\LabSurveyTestData\plane_l40to100.csv
#>
param(
    [Parameter(Mandatory = $true)][double]$LStartDeg,
    [Parameter(Mandatory = $true)][double]$LEndDeg,
    [double]$BHalfWidthDeg = 5,
    [Parameter(Mandatory = $true)][double]$AngularSeparationDeg,
    [Parameter(Mandatory = $true)][string]$OutCsv
)

function Step-Range {
    # Mirrors SweepPlanner.StepRange - see New-SweepGridPoints.ps1 for the same helper.
    param([double]$Start, [double]$End, [double]$Step)

    $result = @()
    if ($Step -le 0) { return $result }

    $span = $End - $Start
    $sign = if ($span -ge 0) { 1 } else { -1 }
    $count = [math]::Round([math]::Abs($span) / $Step, [MidpointRounding]::AwayFromZero)

    for ($i = 0; $i -le $count; $i++) {
        $result += ($Start + $sign * $i * $Step)
    }
    return $result
}

function Get-RowStepDeg {
    # Mirrors SweepPlanner.RowStepDeg - see New-SweepGridPoints.ps1 for the same helper.
    param([double]$SeparationDeg, [double]$RowAngleDeg)

    $minCos = 0.01
    $cosRow = [math]::Max([math]::Abs([math]::Cos($RowAngleDeg * [math]::PI / 180.0)), $minCos)
    return $SeparationDeg / $cosRow
}

$separationDeg = [math]::Abs($AngularSeparationDeg)
$points = @()

foreach ($b in (Step-Range -Start (-$BHalfWidthDeg) -End $BHalfWidthDeg -Step $separationDeg)) {
    $lStepDeg = Get-RowStepDeg -SeparationDeg $separationDeg -RowAngleDeg $b

    foreach ($l in (Step-Range -Start $LStartDeg -End $LEndDeg -Step $lStepDeg)) {
        $lNorm = (($l % 360) + 360) % 360
        $points += [PSCustomObject]@{
            LDeg = [math]::Round($lNorm, 6)
            BDeg = [math]::Round($b, 6)
        }
    }
}

$points | Export-Csv -Path $OutCsv -NoTypeInformation

Write-Output "Generated $($points.Count) grid points (angular separation ${separationDeg} deg, b in [-$BHalfWidthDeg, $BHalfWidthDeg]) -> $OutCsv"
