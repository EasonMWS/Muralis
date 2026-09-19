<#
    Analyses the product's `motion.transform` diagnostic records.

    Prints the scale progression for each pinned app, and the two verdicts this proof exists to produce:
    whether the composition scale is uniform across every sample, and whether the drawn icon overflows the
    dock's client rectangle. The numbers come from the live elements, not from a screenshot.

    Usage: ./tools/p4d-transform-analyse.ps1 [-File <jsonl>] [-App Calculator]
#>
[CmdletBinding()]
param(
    [string] $File = (Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'),
    [string] $App = 'Calculator',
    [double] $Tolerance = 0.0001
)

$records = @(Get-Content $File | Select-String '"name":"motion\.transform"' | ForEach-Object { $_.Line | ConvertFrom-Json })
if ($records.Count -eq 0) { Write-Host 'no motion.transform records' -ForegroundColor Yellow; return }

$rows = New-Object System.Collections.ArrayList
foreach ($r in $records) {
    foreach ($p in $r.pins) {
        [void]$rows.Add([pscustomobject]@{
            T          = [Math]::Round($r.t / 1000, 2)
            Pointer    = $r.pointerAlong
            Inside     = $r.inside
            ClientW    = $r.clientW
            ClientH    = $r.clientH
            Part       = $r.participants
            App        = $p.label
            Idx        = $p.index
            Engine     = $p.engineScale
            SX         = $p.nexusScaleX
            SY         = $p.nexusScaleY
            Delta      = [Math]::Round([Math]::Abs($p.nexusScaleX - $p.nexusScaleY), 6)
            TransY     = $p.nexusTransY
            NexusW     = $p.nexusW
            NexusH     = $p.nexusH
            IconW      = $p.iconW
            IconH      = $p.iconH
            Stretch    = $p.stretch
            VisL       = [Math]::Round($p.visualL, 2)
            VisT       = [Math]::Round($p.visualT, 2)
            VisR       = [Math]::Round($p.visualR, 2)
            VisB       = [Math]::Round($p.visualB, 2)
            TopOver    = [Math]::Round($p.topOverflow, 2)
            BotOver    = [Math]::Round($p.bottomOverflow, 2)
        })
    }
}

"=== records: $($records.Count)   per-app rows: $($rows.Count) ==="
""
"### SCALE PROGRESSION — $App (sorted by engine scale)"
$sub = @($rows | Where-Object { $_.App -eq $App } | Sort-Object Engine)
"  {0,7} {1,8} {2,8} {3,8} {4,9} {5,8} {6,7} {7,7} {8,7} {9,7} {10,7}" -f 'engine','SX','SY','|SX-SY|','icon','nexusW','visT','visB','clientH','topOv','botOv'
foreach ($r in $sub) {
    "  {0,7} {1,8} {2,8} {3,8} {4,9} {5,8} {6,7} {7,7} {8,7} {9,7} {10,7}" -f `
        ([Math]::Round($r.Engine,4)), ([Math]::Round($r.SX,4)), ([Math]::Round($r.SY,4)), $r.Delta, `
        "$($r.IconW)x$($r.IconH)", $r.NexusW, $r.VisT, $r.VisB, $r.ClientH, $r.TopOver, $r.BotOver
}
""
"### VERDICT: UNIFORM SCALE"
$maxDelta = ($rows | Measure-Object Delta -Maximum).Maximum
$nonUniform = @($rows | Where-Object { $_.Delta -gt $Tolerance })
"  samples                        : $($rows.Count)"
"  max abs(Scale.X - Scale.Y)     : $maxDelta"
"  tolerance                      : $Tolerance"
"  samples exceeding tolerance    : $($nonUniform.Count)"
if ($nonUniform.Count -eq 0) {
    "  => UNIFORM COMPOSITION SCALE CONFIRMED"
} else {
    "  => NON-UNIFORM SCALE DETECTED"
    $nonUniform | Select-Object -First 10 | Format-Table T,App,Engine,SX,SY,Delta -AutoSize
}
""
"### VERDICT: CLIPPING"
$topClip = @($rows | Where-Object { $_.VisT -lt 0 })
$botClip = @($rows | Where-Object { $_.VisB -gt $_.ClientH })
"  samples with visualTop < 0        : $($topClip.Count)"
"  samples with visualBottom > clientH: $($botClip.Count)"
if ($topClip.Count -gt 0) {
    $worst = ($topClip | Sort-Object VisT | Select-Object -First 1)
    "  worst visualTop                   : $($worst.VisT) DIP  (app=$($worst.App) engineScale=$([Math]::Round($worst.Engine,4)))"
    "  => TOP CLIPPING CONFIRMED"
} else {
    "  min visualTop observed            : $(($rows | Measure-Object VisT -Minimum).Minimum)"
    "  => NO CLIPPING: every drawn rect is inside the client area"
}
""
"### ICON IMAGE (ShellIconVisual / Image element)"
$iconSizes = @($rows | ForEach-Object { "$($_.IconW)x$($_.IconH)" } | Sort-Object -Unique)
"  distinct icon sizes : $($iconSizes -join ', ')"
"  stretch values      : $((@($rows | ForEach-Object { $_.Stretch }) | Sort-Object -Unique) -join ', ')"
""
"### BETWEEN-PINS (latest sample with two pins)"
$last = $records[-1]
if (@($last.pins).Count -ge 2) {
    $a = $last.pins[0]; $b = $last.pins[1]
    "  pointer along      : $($last.pointerAlong)"
    "  A $($a.label) : engineScale=$([Math]::Round($a.engineScale,4)) SX=$([Math]::Round($a.nexusScaleX,4)) SY=$([Math]::Round($a.nexusScaleY,4)) rect L=$([Math]::Round($a.visualL,1)) T=$([Math]::Round($a.visualT,1)) R=$([Math]::Round($a.visualR,1)) B=$([Math]::Round($a.visualB,1))"
    "  B $($b.label) : engineScale=$([Math]::Round($b.engineScale,4)) SX=$([Math]::Round($b.nexusScaleX,4)) SY=$([Math]::Round($b.nexusScaleY,4)) rect L=$([Math]::Round($b.visualL,1)) T=$([Math]::Round($b.visualT,1)) R=$([Math]::Round($b.visualR,1)) B=$([Math]::Round($b.visualB,1))"
    "  abs(scaleA - scaleB) : $([Math]::Round([Math]::Abs($a.engineScale - $b.engineScale),4))"
}
