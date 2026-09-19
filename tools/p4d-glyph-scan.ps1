<#
    Finds every dark-glyph region in a dock screenshot and reports its true 2-D bounding box.

    Written because the naive version of this measurement is how a square icon came to be reported as "68x27":
    a width taken from one horizontal scan and a height taken from another are not the same measurement. This
    finds each glyph as a region and reports width and height from a single bounding box, so the ratio it prints
    is a ratio of two alike measurements.

    The scan band is limited to the icon row, so the label text underneath is never folded into the icon height.

    Usage: ./tools/p4d-glyph-scan.ps1 -File <png> [-Band 14,74] [-MaxReport 8]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $File,
    [int] $Y0 = 0,
    [int] $Y1 = 0,
    [int] $MaxReport = 8,
    [int] $Gap = 6,
    [int] $Ink = 150
)

Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Bitmap]::FromFile((Resolve-Path $File))
$w = $img.Width
$h = $img.Height

$y0 = if ($Y0 -gt 0) { $Y0 } else { 12 }
$y1 = if ($Y1 -gt 0) { $Y1 } else { $h - 12 }
if ($y1 -ge $h) { $y1 = $h - 1 }

Write-Host "=== $([IO.Path]::GetFileName($File))  ${w}x${h}  band y=$y0..$y1 ===" -ForegroundColor Cyan

# Per-column ink extent, as three flat arrays. No multidimensional arrays: PowerShell indexes those badly.
$colTop = New-Object 'int[]' $w
$colBot = New-Object 'int[]' $w
$colN = New-Object 'int[]' $w
for ($x = 0; $x -lt $w; $x++) {
    $top = -1; $bot = -1; $n = 0
    for ($y = $y0; $y -le $y1; $y++) {
        $p = $img.GetPixel($x, $y)
        if ($p.R -lt $Ink -and $p.G -lt $Ink -and $p.B -lt ($Ink + 10)) {
            if ($top -lt 0) { $top = $y }
            $bot = $y
            $n = $n + 1
        }
    }
    $colTop[$x] = $top
    $colBot[$x] = $bot
    $colN[$x] = $n
}

$regions = New-Object System.Collections.ArrayList
$s = -1
$gapRun = 0
for ($x = 0; $x -lt $w; $x++) {
    if ($colN[$x] -gt 0) {
        if ($s -lt 0) { $s = $x }
        $gapRun = 0
    }
    elseif ($s -ge 0) {
        $gapRun = $gapRun + 1
        if ($gapRun -ge $Gap) {
            [void]$regions.Add(@($s, ($x - $gapRun)))
            $s = -1
            $gapRun = 0
        }
    }
}
if ($s -ge 0) { [void]$regions.Add(@($s, ($w - 1))) }

"  dark-glyph regions: $($regions.Count)"
"  {0,-4} {1,-16} {2,-16} {3,6} {4,7} {5,8} {6,7}" -f '#', 'x-range', 'y-range', 'W', 'H', 'W/H', 'px'
$i = 0
foreach ($r in $regions) {
    if ($i -ge $MaxReport) { break }
    $rx0 = $r[0]; $rx1 = $r[1]
    $t = 999999; $b = -1; $n = 0
    for ($x = $rx0; $x -le $rx1; $x++) {
        if ($colN[$x] -gt 0) {
            $n = $n + $colN[$x]
            if ($colTop[$x] -lt $t) { $t = $colTop[$x] }
            if ($colBot[$x] -gt $b) { $b = $colBot[$x] }
        }
    }
    if ($n -lt 20) { continue }
    $gw = $rx1 - $rx0 + 1
    $gh = $b - $t + 1
    $ratio = if ($gh -gt 0) { [Math]::Round($gw / $gh, 3) } else { 0 }
    "  {0,-4} {1,-16} {2,-16} {3,6} {4,7} {5,8} {6,7}" -f $i, "$rx0..$rx1", "$t..$b", $gw, $gh, $ratio, $n
    $i = $i + 1
}
$img.Dispose()
