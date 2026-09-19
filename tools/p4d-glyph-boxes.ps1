<#
    Finds the bounding box of each app glyph in a dock screenshot, in two dimensions.

    A horizontal span at one row cannot answer "is this icon non-uniform": a width measured at one y and a
    height measured at one x are not the same measurement, and comparing them is how a square icon gets
    reported as 68x27. This finds each connected glyph region and reports its real width AND height, so the
    aspect ratio it prints is a ratio of two like measurements.

    Usage: ./tools/p4d-glyph-boxes.ps1 -File <png> [-MinArea 30] [-XLimit 400]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $File,
    [int] $MinArea = 25,
    [int] $XLimit = 0,
    [int] $Gap = 6
)

Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Bitmap]::FromFile((Resolve-Path $File))
$w = $img.Width; $h = $img.Height
if ($XLimit -le 0) { $XLimit = $w }

# "Content" here means anything that is not the near-white page/dock background. The tile surface is very
# light, so it is excluded too: what is left is the glyph's own pixels.
$mask = New-Object 'bool[,]' $XLimit, $h
for ($y = 0; $y -lt $h; $y++) {
    for ($x = 0; $x -lt $XLimit; $x++) {
        $p = $img.GetPixel($x, $y)
        $isBg = ($p.R -ge 238 -and $p.G -ge 238 -and $p.B -ge 240)
        $mask[$x, $y] = -not $isBg
    }
}

# Column occupancy, then group columns into items separated by gaps wider than the dock's inter-item spacing.
$colCount = New-Object 'int[]' $XLimit
for ($x = 0; $x -lt $XLimit; $x++) {
    $c = 0
    for ($y = 0; $y -lt $h; $y++) { if ($mask[$x, $y]) { $c++ } }
    $colCount[$x] = $c
}

$items = New-Object System.Collections.ArrayList
$start = -1; $empty = 0
for ($x = 0; $x -lt $XLimit; $x++) {
    if ($colCount[$x] -gt 0) {
        if ($start -lt 0) { $start = $x }
        $empty = 0
    }
    elseif ($start -ge 0) {
        $empty++
        if ($empty -ge $Gap) {
            [void]$items.Add([pscustomobject]@{ X0 = $start; X1 = $x - $empty })
            $start = -1; $empty = 0
        }
    }
}
if ($start -ge 0) { [void]$items.Add([pscustomobject]@{ X0 = $start; X1 = $XLimit - 1 }) }

Write-Host "=== $([IO.Path]::GetFileName($File))  $($w)x$($h)  (scanning x<${XLimit}) ===" -ForegroundColor Cyan
"  glyph regions found: $($items.Count)"
"  {0,-6} {1,-16} {2,-16} {3,7} {4,7} {5,8}" -f 'item', 'x-range', 'y-range', 'width', 'height', 'W/H'
foreach ($it in $items) {
    $y0 = $h; $y1 = -1; $area = 0
    for ($x = $it.X0; $x -le $it.X1; $x++) {
        for ($y = 0; $y -lt $h; $y++) {
            if ($mask[$x, $y]) {
                $area++
                if ($y -lt $y0) { $y0 = $y }
                if ($y -gt $y1) { $y1 = $y }
            }
        }
    }
    if ($area -lt $MinArea) { continue }
    $gw = $it.X1 - $it.X0 + 1
    $gh = $y1 - $y0 + 1
    $ratio = if ($gh -gt 0) { [Math]::Round($gw / $gh, 3) } else { 0 }
    "  {0,-6} {1,-16} {2,-16} {3,7} {4,7} {5,8}" -f "x=$($it.X0)", "$($it.X0)..$($it.X1)", "$y0..$y1", $gw, $gh, $ratio
}
$img.Dispose()
