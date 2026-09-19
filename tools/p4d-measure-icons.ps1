<#
    Measures the rendered icon pills in a dock screenshot by scanning rows of pixels.

    Used to turn "the icons look compressed" into numbers: a screenshot is not evidence of a size unless the size
    is actually measured from it. Background is white-ish in the light theme; anything meaningfully darker starts a
    run. Runs wider than a few pixels are the icon pills.

    Usage: ./tools/p4d-measure-icons.ps1 -File <png> -Rows 50,60,70
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $File,
    [int[]] $Rows = @(50),
    [int] $Threshold = 246,
    [int] $MinRun = 5
)

Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Bitmap]::FromFile((Resolve-Path $File))
Write-Host "=== $([IO.Path]::GetFileName($File))  $($img.Width)x$($img.Height) ===" -ForegroundColor Cyan
"  threshold=$Threshold  minRun=$MinRun"

foreach ($y in $Rows) {
    if ($y -ge $img.Height) { continue }
    $spans = New-Object System.Collections.ArrayList
    $start = -1
    for ($x = 0; $x -lt $img.Width; $x++) {
        $p = $img.GetPixel($x, $y)
        $isBg = ($p.R -ge $Threshold -and $p.G -ge $Threshold -and $p.B -ge $Threshold)
        if (-not $isBg) {
            if ($start -lt 0) { $start = $x }
        }
        elseif ($start -ge 0) {
            $w = $x - $start
            if ($w -ge $MinRun) { [void]$spans.Add([pscustomobject]@{ X = $start; W = $w }) }
            $start = -1
        }
    }
    if ($start -ge 0) {
        $w = $img.Width - $start
        if ($w -ge $MinRun) { [void]$spans.Add([pscustomobject]@{ X = $start; W = $w }) }
    }

    "  y=$y : $($spans.Count) span(s)"
    $line = ($spans | ForEach-Object { "$($_.X)+$($_.W)" }) -join '  '
    "        $line"
    if ($spans.Count -gt 1) {
        $ws = ($spans | ForEach-Object { $_.W }) | Sort-Object
        "        widths min=$(($ws)[0]) max=$(($ws)[-1]) median=$(($ws)[[int]($ws.Count/2)])"
    }
}

$img.Dispose()
