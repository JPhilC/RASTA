<#
.SYNOPSIS
    Generates the equatorial sweep grid points for a rectangular RA/Dec target area,
    faithfully mirroring RASTA.Processing.Planning.SweepPlanner.BuildEquatorialSweep
    (RASTA.Processing/Planning/SweepPlanner.cs) - same row-by-row Dec spacing, same
    cos(dec)-corrected RA step per row, same start/end-agnostic StepRange stepping.

.DESCRIPTION
    Unlike SweepPlanner itself, this does NOT do the greedy elevation-based visit
    ordering (that needs a live site/time context to mean anything) - it just emits
    every grid point, in row order, since for building synthetic LAB-survey test data
    only coverage/positions matter, not capture order.

.EXAMPLE
    ./New-SweepGridPoints.ps1 -RaStartHours 0 -RaEndHours 2 -DecStartDeg 44 -DecEndDeg 66 -AngularSeparationDeg 3 -OutCsv C:\Raw\RASTA\LabSurveyTestData\cassiopeia_grid.csv
#>
param(
    [Parameter(Mandatory = $true)][double]$RaStartHours,
    [Parameter(Mandatory = $true)][double]$RaEndHours,
    [Parameter(Mandatory = $true)][double]$DecStartDeg,
    [Parameter(Mandatory = $true)][double]$DecEndDeg,
    [Parameter(Mandatory = $true)][double]$AngularSeparationDeg,
    [Parameter(Mandatory = $true)][string]$OutCsv
)

function Step-Range {
    # Mirrors SweepPlanner.StepRange: inclusive start->end stepping by a positive
    # magnitude, auto-reversing if end < start, using an integer step count so
    # rounding error can't drop/duplicate the final point.
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
    # Mirrors SweepPlanner.RowStepDeg: corrects the per-row coordinate step so
    # adjacent points stay $SeparationDeg apart in true angle, floored at cos=0.01
    # right at the pole rather than dividing by ~zero.
    param([double]$SeparationDeg, [double]$RowAngleDeg)

    $minCos = 0.01
    $cosRow = [math]::Max([math]::Abs([math]::Cos($RowAngleDeg * [math]::PI / 180.0)), $minCos)
    return $SeparationDeg / $cosRow
}

$separationDeg = [math]::Abs($AngularSeparationDeg)
$points = @()

foreach ($dec in (Step-Range -Start $DecStartDeg -End $DecEndDeg -Step $separationDeg)) {
    $rowStepDeg = Get-RowStepDeg -SeparationDeg $separationDeg -RowAngleDeg $dec
    $raStepHours = $rowStepDeg / 15.0

    foreach ($ra in (Step-Range -Start $RaStartHours -End $RaEndHours -Step $raStepHours)) {
        $points += [PSCustomObject]@{
            RaHours = [math]::Round($ra, 6)
            DecDeg  = [math]::Round($dec, 6)
        }
    }
}

$points | Export-Csv -Path $OutCsv -NoTypeInformation

Write-Output "Generated $($points.Count) grid points (angular separation ${separationDeg} deg) -> $OutCsv"
