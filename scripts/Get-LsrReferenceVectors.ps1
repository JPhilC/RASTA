<#
.SYNOPSIS
    Fetches the reference observer velocities used by RASTA.Tests' LSR-correction tests
    (AstronomyUtilsTests.ComputeLsrCorrectionKmPerSec_MatchesJplHorizons) from JPL Horizons.

.DESCRIPTION
    For each case below this asks the JPL Horizons API (https://ssd.jpl.nasa.gov/api/horizons.api,
    DE441 ephemeris) for the geometric velocity of a point on the Earth's surface relative to the
    Solar System barycentre, in the ICRF (J2000 equatorial) frame, in km/s. That vector already
    contains both the Earth's orbital motion and the observer's diurnal rotation, so the test's
    expected LSR correction is just

        v_observer . u_target  +  20 km/s * (u_apex . u_target)

    with no astronomy of RASTA's own in it.

    Horizons wants TDB; the cases are in UTC, so 69.184 s (TT - UTC since 2017) is added. Using
    that one offset for every year is wrong by at most a few seconds for the pre-2017 cases, which
    changes the velocity by well under 0.001 km/s.

    Output is one NUnit [TestCase(...)] line per case, ready to paste into AstronomyUtilsTests.cs.
    The sites are arbitrary round numbers, not a real observing site.

.EXAMPLE
    .\scripts\Get-LsrReferenceVectors.ps1
#>
[CmdletBinding()]
param(
    [double]$DelaySeconds = 1.0
)

$ErrorActionPreference = 'Stop'
$inv = [System.Globalization.CultureInfo]::InvariantCulture

# latDeg, lonDeg (east positive), UTC, RA hours (J2000), Dec degrees (J2000)
$cases = @(
    @( 45.0,   10.0, '2026-01-03T22:00:00Z',  5.5,    20.0),
    @( 45.0,   10.0, '2026-02-14T04:30:00Z', 12.5,    60.0),
    @( 45.0,   10.0, '2026-04-04T20:00:00Z',  6.0,   -10.0),
    @(-35.0,  150.0, '2026-05-20T12:00:00Z', 17.76,  -29.0),
    @( 20.0, -155.0, '2026-07-05T08:00:00Z', 20.0,    40.0),
    @( 45.0,   10.0, '2026-08-15T23:15:00Z', 20.65,   45.0),
    @( 65.0,   25.0, '2026-10-05T18:00:00Z',  0.7,    41.0),
    @( 45.0,   10.0, '2026-11-08T15:30:00Z', 21.0,    40.0),
    @(-35.0,  150.0, '2026-12-21T14:00:00Z',  8.0,   -45.0),
    @( 20.0, -155.0, '2027-03-20T10:00:00Z',  3.0,     0.0),
    @( 65.0,   25.0, '2027-06-10T01:00:00Z', 14.0,    75.0),
    @( 45.0,   10.0, '2015-09-23T03:00:00Z', 23.0,     5.0),
    @(-35.0,  150.0, '2040-02-29T09:45:00Z', 10.5,   -60.0),
    @( 45.0,   10.0, '2000-01-01T12:00:00Z', 18.064,  30.0)
)

foreach ($c in $cases) {
    $lat = [double]$c[0]; $lon = [double]$c[1]; $utcText = [string]$c[2]
    $utc = [datetime]::Parse($utcText, $inv, [System.Globalization.DateTimeStyles]::AdjustToUniversal)
    $jdTdb = $utc.ToOADate() + 2415018.5 + 69.184 / 86400.0

    $query = @{
        format     = 'text'
        COMMAND    = "'g: $($lon.ToString($inv)), $($lat.ToString($inv)), 0 @ 399'"
        OBJ_DATA   = "'NO'"
        MAKE_EPHEM = "'YES'"
        EPHEM_TYPE = "'VECTORS'"
        CENTER     = "'500@0'"
        REF_PLANE  = "'FRAME'"
        VEC_TABLE  = "'2'"
        OUT_UNITS  = "'KM-S'"
        CSV_FORMAT = "'YES'"
        TLIST      = "'$($jdTdb.ToString('F8', $inv))'"
    }
    $pairs = $query.GetEnumerator() | ForEach-Object { "$($_.Key)=$([uri]::EscapeDataString($_.Value))" }
    $url = 'https://ssd.jpl.nasa.gov/api/horizons.api?' + ($pairs -join '&')

    $text = (Invoke-WebRequest -Uri $url -UseBasicParsing).Content
    if ($text -notmatch '(?s)\$\$SOE\s*(.*?)\s*\$\$EOE') { throw "No ephemeris returned for $utcText :`n$text" }
    $f = $Matches[1].Split(',') | ForEach-Object { $_.Trim() }
    $vx = [double]::Parse($f[5], $inv); $vy = [double]::Parse($f[6], $inv); $vz = [double]::Parse($f[7], $inv)

    '[TestCase({0}, {1}, "{2}", {3}, {4}, {5}, {6}, {7})]' -f `
        $lat.ToString('0.0', $inv), $lon.ToString('0.0', $inv), $utcText,
        ([double]$c[3]).ToString('0.0##', $inv), ([double]$c[4]).ToString('0.0', $inv),
        $vx.ToString('F5', $inv), $vy.ToString('F5', $inv), $vz.ToString('F5', $inv)

    Start-Sleep -Seconds $DelaySeconds
}
