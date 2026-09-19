<#
    Measures a horizontal band of a dock screenshot and reports the spans of coloured content in it.

    The point is to separate things a reviewer previously conflated into one "icon" number: the interaction
    tile (a light rounded surface), the app glyph inside it (saturated colour, or dark line art), and the
    label text below. Each is measured as its own span, at its own row, so a width in one band is never
    compared with a height in another.

    Usage: ./tools/p4d-span-measure.ps1 -File <png> [-GlyphRows 40,45] [-TileRows 30] [-LabelRows 90]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $File,
    [int[]] $Rows = @(30, 45, 60),
    [int] $Threshold = 246,
    [int] $MinRun = 3
)

Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Bitmap]::FromFile((Resolve-Path $File))
Write-Host "=== $([IO.Path]::GetFileName($File))  $($img.Width)x$($img.Height) ===" -ForegroundColor Cyan

function Classify($p) {
    # White-ish page/dock background
    if ($p.R -ge 246 -and $p.G -ge 246 -and $p.B -ge 246) { return 'bg' }
    # Saturated warm colour = a folder/app glyph
    if (($p.R - $p.B) -gt 60 -and $p.R -gt 180) { return 'glyph' }
    # Dark ink = line art or text
    if ($p.R -lt 140 -and $p.G -lt 140 -and $p.B -lt 150) { return 'ink' }
    return 'tile'
}

foreach ($y in $Rows) {
    if ($y -ge $img.Height) { continue }
    $groups = @{ bg = 0; glyph = 0; ink = 0; tile = 0 }
    $spans = New-Object System.Collections.ArrayList
    $cur = $null; $start = 0
    for ($x = 0; $x -lt $img.Width; $x++) {
        $c = Classify $img.GetPixel($x, $y)
        $groups[$c]++
        if ($c -eq 'bg') {
            if ($null -ne $cur -and ($x - $start) -ge $MinRun) {
                [void]$spans.Add([pscustomobject]@{ Kind = $cur; X = $start; W = $x - $start })
            }
            $cur = $null
        }
        elseif ($null -eq $cur) { $cur = $c; $start = $x }
        elseif ($c -ne $cur) {
            if (($x - $start) -ge $MinRun) { [void]$spans.Add([pscustomobject]@{ Kind = $cur; X = $start; W = $x - $start }) }
            $cur = $c; $start = $x
        }
    }
    if ($null -ne $cur -and ($img.Width - $start) -ge $MinRun) {
        [void]$spans.Add([pscustomobject]@{ Kind = $cur; X = $start; W = $img.Width - $start })
    }

    "  y=$y  pixels: " + (($groups.Keys | Sort-Object | ForEach-Object { "$_=$($groups[$_])" }) -join '  ')
    # Only report the two kinds that identify an app: the glyph colour and the dark line art.
    $glyphSpans = @($spans | Where-Object { $_.Kind -eq 'glyph' })
    $inkSpans   = @($spans | Where-Object { $_.Kind -eq 'ink' })
    if ($glyphSpans.Count) { "        glyph spans: " + (($glyphSpans | ForEach-Object { "$($_.X)+$($_.W)" }) -join '  ') }
    if ($inkSpans.Count)   { "        ink   spans: " + (($inkSpans   | ForEach-Object { "$($_.X)+$($_.W)" }) -join '  ') }
}
$img.Dispose()
