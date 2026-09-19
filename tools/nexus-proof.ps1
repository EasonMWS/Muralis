<#
    NEXUS PROOF — the product's own motion engine, checked over the whole dock span.

    The interaction harness can only observe the peak scale, which says the engine reached 1.8 but nothing about
    the shape of the wave. This drives the engine directly across every pointer position the dock can see — each
    icon centre and each gap between two of them — and checks the properties that make the dock feel like a dock
    rather than a hover effect.

    The arithmetic is recomputed from the candidate's own printed scales, so the check is self-consistent: the
    printed scale is the engine's output, and what must follow from it is the transformation. Everything is
    derived, nothing is assumed.

    Usage:
      ./tools/nexus-proof.ps1 -Exe <path> [-AppArgs '--pins 5 --paths p.txt'] [-Pins 5]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Exe,
    [string[]] $AppArgs = @(),
    [int] $Pins = 5,
    [int] $IconBox = 52,
    [int] $Cell = 56,
    [int] $Pad = 12,
    [double] $MaxScale = 1.8,
    [double] $MaximumLift = 10.0,
    [double] $InfluenceRadius = 134.4,
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\clear-dock\nexus'
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (-not (Test-Path $Exe)) { throw "no executable at $Exe" }

Write-Host "=== NEXUS PROOF ===" -ForegroundColor Cyan
Write-Host "candidate: $Exe"

$stdoutPath = Join-Path $OutDir 'sweep-stdout.txt'
if (Test-Path $stdoutPath) { Remove-Item $stdoutPath -Force }

$runArgs = @($AppArgs) + @('--sweep')
$startArgs = @{ FilePath = $Exe; PassThru = $true; RedirectStandardOutput = $stdoutPath }
if ($runArgs.Count -gt 0) { $startArgs['ArgumentList'] = $runArgs }
$proc = Start-Process @startArgs

$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 300
    if ($proc.HasExited) { break }
    if ((Get-Content $stdoutPath -ErrorAction SilentlyContinue) -match 'SWEEP done') { break }
}
if (-not $proc.HasExited) { $proc.Kill(); Start-Sleep -Milliseconds 300 }

$lines = @(Get-Content $stdoutPath -ErrorAction SilentlyContinue)
$profile = $lines | Where-Object { $_ -like 'SWEEP profile*' } | Select-Object -First 1
if ($profile) { Write-Host "  $profile" }

$sweeps = @($lines | Where-Object { $_ -like 'SWEEP pointer=*' })
if ($sweeps.Count -eq 0) { throw "the candidate produced no sweep output (see $stdoutPath)" }

# The layout the candidate used, read from its own header rather than assumed, so a change of geometry in the
# candidate cannot silently make this harness check the wrong centres.
$geo = $lines | Where-Object { $_ -like 'SWEEP pins=*' } | Select-Object -First 1
$n = $Pins; $box = [double]$IconBox; $cw = [double]$Cell; $pd = [double]$Pad
if ($geo -match 'pins=(\d+)') { $n = [int]$Matches[1] }
if ($geo -match 'iconBox=(\d+)') { $box = [double]$Matches[1] }
if ($geo -match 'cell=(\d+)') { $cw = [double]$Matches[1] }
if ($geo -match 'pad=(\d+)') { $pd = [double]$Matches[1] }
$gap = $cw - $box
$centres = 0..($n - 1) | ForEach-Object { $pd + ($_ * $cw) + ($cw / 2.0) }
Write-Host "  derived: icons=$n iconBox=$box cell=$cw pad=$pd gap=$gap"

# Six printed decimals, and the transformation is recomputed from those same rounded scales, so this tolerance
# covers the arithmetic rather than the printing.
$tol = 1e-4
$failures = New-Object System.Collections.Generic.List[string]
$maxGapError = 0.0
$checked = 0
$peaksSeen = New-Object System.Collections.Generic.List[double]

foreach ($ln in $sweeps) {
    if ($ln -notmatch 'pointer=([-\d.]+|away)') { continue }
    $ptrText = $Matches[1]
    $isAway = $ptrText -eq 'away'
    $ptr = if ($isAway) { 0.0 } else { [double]$ptrText }

    $scales = New-Object 'double[]' $n
    $tx = New-Object 'double[]' $n
    $lift = New-Object 'double[]' $n
    $seen = 0
    foreach ($m in [regex]::Matches($ln, 'i(\d+):s([\d.]+),x(-?[\d.]+),l([\d.]+)')) {
        $i = [int]$m.Groups[1].Value
        if ($i -ge $n) { continue }
        $scales[$i] = [double]$m.Groups[2].Value
        $tx[$i] = [double]$m.Groups[3].Value
        $lift[$i] = [double]$m.Groups[4].Value
        $seen++
    }
    if ($seen -ne $n) { continue }
    $checked++

    $half = New-Object 'double[]' $n
    for ($i = 0; $i -lt $n; $i++) { $half[$i] = ($scales[$i] - 1.0) * $box / 2.0 }

    # The engine's own rule: the first icon with the maximum scale claims the peak. A sort is not stable, and at
    # a pointer parked mid-gap two icons tie — which icon owns the peak decides every expected translation.
    $mx = ($scales | Measure-Object -Maximum).Maximum
    $pk = 0
    for ($i = 0; $i -lt $n; $i++) { if ($scales[$i] -eq $mx) { $pk = $i; break } }
    $peaksSeen.Add($mx)

    # Invariant 1 — translation is exactly the accumulated half-widths walking outwards from the peak, which is
    # what opens the run instead of letting icons burst through their neighbours.
    $wantTx = New-Object 'double[]' $n
    $run = 0.0; for ($i = $pk; $i -lt ($n - 1); $i++) { $run += $half[$i] + $half[$i + 1]; $wantTx[$i + 1] = $run }
    $run = 0.0; for ($i = $pk; $i -gt 0; $i--) { $run += $half[$i] + $half[$i - 1]; $wantTx[$i - 1] = -$run }
    for ($i = 0; $i -lt $n; $i++) {
        if ([Math]::Abs($tx[$i] - $wantTx[$i]) -gt $tol) {
            $failures.Add("pointer=$ptrText icon $i moved $([Math]::Round($tx[$i],6)) but the run demands $([Math]::Round($wantTx[$i],6))")
        }
    }

    # Invariant 2 — the pitch survives magnification: every gap is still the one the dock was laid out with,
    # including the two straddling the peak. This is the property that keeps the run from bursting open.
    $edges = New-Object 'object[]' $n
    for ($i = 0; $i -lt $n; $i++) {
        $c = $centres[$i] + $tx[$i]
        $edges[$i] = [pscustomobject]@{ L = $c - ($box * $scales[$i] / 2.0); R = $c + ($box * $scales[$i] / 2.0) }
    }
    for ($i = 0; $i -lt ($n - 1); $i++) {
        $g = $edges[$i + 1].L - $edges[$i].R
        $maxGapError = [Math]::Max($maxGapError, [Math]::Abs($g - $gap))
        if ([Math]::Abs($g - $gap) -gt $tol) { $failures.Add("pointer=$ptrText gap $i is $([Math]::Round($g,6)) DIP, not the laid-out $gap") }
    }

    if ($isAway) {
        # Invariant 4 — the pointer leaving puts everything exactly back.
        for ($i = 0; $i -lt $n; $i++) {
            if ($scales[$i] -ne 1.0 -or $tx[$i] -ne 0.0 -or $lift[$i] -ne 0.0) {
                $failures.Add("with the pointer away icon $i is not at rest (scale $($scales[$i]), x $($tx[$i]), lift $($lift[$i]))")
            }
        }
        continue
    }

    # Invariant 3 — the icon under the pointer does not move, and reaches exactly the profile's ceilings.
    if ([Math]::Abs($tx[$pk]) -gt 1e-9) { $failures.Add("pointer=$ptrText the peak icon $pk moved by $($tx[$pk])") }
    $onCentre = -1
    for ($i = 0; $i -lt $n; $i++) { if ([Math]::Abs($centres[$i] - $ptr) -lt 1e-6) { $onCentre = $i; break } }
    if ($onCentre -ge 0) {
        if ([Math]::Abs($scales[$onCentre] - $MaxScale) -gt $tol) { $failures.Add("pointer=$ptrText on icon $onCentre the scale is $($scales[$onCentre]), not $MaxScale") }
        if ([Math]::Abs($lift[$onCentre] - $MaximumLift) -gt $tol) { $failures.Add("pointer=$ptrText on icon $onCentre the lift is $($lift[$onCentre]), not $MaximumLift") }
        if ($onCentre -ne $pk) { $failures.Add("pointer=$ptrText is on icon $onCentre but icon $pk is the largest") }
    }

    # Invariant 5 — the lift is the influence squared, so it dies away faster than the size does.
    for ($i = 0; $i -lt $n; $i++) {
        $influence = ($scales[$i] - 1.0) / ($mx - 1.0)
        $wantLift = $MaximumLift * $influence * $influence
        if ([Math]::Abs($lift[$i] - $wantLift) -gt 1e-3) {
            $failures.Add("pointer=$ptrText icon $i lifts $([Math]::Round($lift[$i],6)) but the influence curve demands $([Math]::Round($wantLift,6))")
        }
    }

    # Invariant 6 — beyond the influence radius an icon does not grow: scale is exactly 1 and it does not lift.
    # It is NOT required to be untranslated, and expecting that was wrong: the run keeps its pitch by pushing
    # every icon outwards, and preserving the gaps left of an enlarged icon means the icons beyond it slide too.
    # Scale and lift going to exactly 1 and 0 is the whole of the claim — the far end of a long dock then costs
    # nothing to draw at its own size, which is what the test is for.
    for ($i = 0; $i -lt $n; $i++) {
        if ([Math]::Abs($centres[$i] - $ptr) -ge $InfluenceRadius) {
            if ($scales[$i] -ne 1.0) {
                $failures.Add("pointer=$ptrText icon $i is beyond the influence radius but its scale is $($scales[$i]), not 1")
            }
            if ([Math]::Abs($lift[$i]) -gt 1e-9) {
                $failures.Add("pointer=$ptrText icon $i is beyond the influence radius but it lifts $($lift[$i])")
            }
        }
    }
}

$peakMax = ($peaksSeen | Measure-Object -Maximum).Maximum
Write-Host "`n  pointer positions checked : $checked"
Write-Host "  largest gap error         : $([Math]::Round($maxGapError,9)) DIP"
Write-Host "  largest scale reached     : $peakMax  (profile MaxScale $MaxScale)"

Write-Host "`n=== VERDICT ===" -ForegroundColor Cyan
if ($failures.Count -eq 0) {
    Write-Host "  PASS - the product's own engine drives the wave correctly across the whole dock:" -ForegroundColor Green
    Write-Host "         the run keeps its laid-out pitch, the icon under the pointer never moves and" -ForegroundColor Green
    Write-Host "         reaches exactly the profile's ceilings, the lift follows the influence curve," -ForegroundColor Green
    Write-Host "         icons beyond the influence radius do not grow or lift, and taking the pointer" -ForegroundColor Green
    Write-Host "         away restores every icon exactly." -ForegroundColor Green
    $exit = 0
} else {
    Write-Host "  FAIL" -ForegroundColor Red
    $failures | Select-Object -First 20 | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    if ($failures.Count -gt 20) { Write-Host "    ... and $($failures.Count - 20) more" -ForegroundColor Red }
    $exit = 1
}

Write-Host "  sweep output: $stdoutPath"
exit $exit
