<#
    Stage B acceptance: performance and remaining evidence, with the geometry self-checked.

    The previous attempt derived its screen transform from the rail bounds the engine reports, and those bounds
    are clamped to the window. On a dock whose rail is wider than its window that yields a rail six pixels wide
    and a transform that is nonsense — every reading taken through it was wrong while looking plausible. So the
    transform is now checked before it is used, and the run stops rather than reporting numbers built on it.

    The pointer is also injected at a rate the dock can consume. Injecting thousands of positions in a few
    seconds leaves the dock's pointer queue holding positions it has not applied yet, so "applied" under-reports
    and the latency figures describe a backlog rather than the work.

    Usage: ./tools/phase4d-perf-verify.ps1
#>
[CmdletBinding()]
param(
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\stageB\repair',
    [int] $DwellMs = 40
)

$ErrorActionPreference = 'Stop'
trap {
    Write-Host ''
    Write-Host '!!! FAILED !!!' -ForegroundColor Red
    Write-Host ("  " + $_.Exception.Message)
    Write-Host ("  line " + $_.InvocationInfo.ScriptLineNumber + " : " + $_.InvocationInfo.Line.Trim())
    break
}
Add-Type -AssemblyName System.Windows.Forms
. (Join-Path $PSScriptRoot 'phase4d-motion-lib.ps1')

$native = Join-Path $env:USERPROFILE '.dsh\profiles\web-computer-test\node_modules\dsh-computer-use-windows\native\publish\DshComputerUse.Native.exe'
$dockHwnd = (Get-Content "$OutDir\dock-hwnd.txt" -Raw).Trim()

function Dock-Rect {
    $o = ((& $native list 2>&1 | Out-String | ConvertFrom-Json) | Where-Object { $_.title -eq 'Muralis Dock' } | Select-Object -First 1)
    [pscustomobject]@{ X=$o.bounds.x; Y=$o.bounds.y; W=$o.bounds.width; H=$o.bounds.height; Bottom=($o.bounds.y+$o.bounds.height); Right=($o.bounds.x+$o.bounds.width) }
}
function Transitions { @($script:Lines | Select-String 'motion\.bounds\.transition' | ForEach-Object { $_.Line | ConvertFrom-Json }) }
function LastMotion {
    @($script:Lines | Select-String '"name":"motion\.(pointer|rebuild)"' | ForEach-Object { $_.Line | ConvertFrom-Json }) | Select-Object -Last 1
}
function Settle([int] $timeoutMs = 6000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs); $stable = 0; $last = -2
    while ((Get-Date) -lt $deadline) {
        Pump-MotionLog 50
        $m = LastMotion; $raw = if ($m) { $m.raw } else { -1 }
        if ($raw -eq $last) { $stable++ } else { $stable = 0; $last = $raw }
        if ($stable -ge 5) { Start-Sleep -Milliseconds 300; Pump-MotionLog 0; return $m }
    }
    return (LastMotion)
}
function Force-Resting {
    # Leave, then nudge until the dock's own trace says it is resting. A nudge is needed because the state only
    # changes on a pointer report, and a pointer that has already stopped sends none.
    for ($i = 0; $i -lt 6; $i++) {
        Move-Pointer 300 (400 + $i)
        Start-Sleep -Milliseconds 700
        Pump-MotionLog 0
        $last = @(Transitions) | Select-Object -Last 1
        if ($last -and $last.state -eq 'resting') { Settle 5000 | Out-Null; return $last }
        Move-Pointer 900 (400 + $i)
        Start-Sleep -Milliseconds 500
        Pump-MotionLog 0
    }
    return (@(Transitions) | Select-Object -Last 1)
}

Write-Host '=== settle and self-check the geometry ===' -ForegroundColor Cyan
Reset-MotionLog | Out-Null
$tRest = Force-Resting
$restWin = Dock-Rect
$r = Get-LastRecord '"name":"motion\.rebuild"'
$railL = [double]$r.dockLeft; $railR = [double]$r.dockRight
$railT = [double]$r.dockTop;  $railB = [double]$r.dockBottom
$centres = @($r.centres -split ',' | ForEach-Object { [double]$_ })
$widths  = @($r.widths  -split ',' | ForEach-Object { [double]$_ })
Write-Host ("  resting window   : ({0},{1}) {2}x{3} bottom={4}" -f $restWin.X,$restWin.Y,$restWin.W,$restWin.H,$restWin.Bottom)
Write-Host ("  transition state : {0} via {1}" -f $tRest.state,$tRest.reason)
Write-Host ("  engine rail      : x {0}..{1} (width {2}), y {3}..{4}, {5} icons" -f [Math]::Round($railL,1),[Math]::Round($railR,1),[Math]::Round($railR-$railL,1),[Math]::Round($railT,1),[Math]::Round($railB,1),$centres.Count)
Write-Host ("  resting rect     : x {0}..{1} (width {2})" -f [Math]::Round($tRest.restingLeft,1),[Math]::Round($tRest.restingRight,1),[Math]::Round($tRest.restingRight-$tRest.restingLeft,1))

$railWidth = $railR - $railL
$restWidth = [double]$tRest.restingRight - [double]$tRest.restingLeft
$sane = ($railWidth -gt 100) -and ($restWidth -gt 100)
if (-not $sane) {
    Write-Host ("  GEOMETRY NOT USABLE: rail width {0:F1}, resting rect width {1:F1}" -f $railWidth,$restWidth) -ForegroundColor Red
    Write-Host '  The engine clamps its rail to the window, so a rail narrower than the dock means the window is not at rest.'
    throw 'rail bounds are clamped - cannot derive a transform'
}
# Both spans describe the same rail: one in dock units, one on screen. Their ratio is the transform.
$scale = $restWidth / $railWidth
$originX = [double]$tRest.restingLeft - ($railL * $scale)
$bandY = [int][Math]::Round([double]$tRest.restingTop + (($railB - $railT) / 2.0 * $scale))
Write-Host ("  transform        : scale={0:F4} originX={1:F1} bandY={2}" -f $scale,$originX,$bandY)
Write-Host ("  cross-check      : rail left maps to {0:F1}, resting left is {1:F1}" -f ($originX + $railL*$scale),[double]$tRest.restingLeft)

# Which centres the dock answers to, settled by moving the pointer.
$visible = New-Object System.Collections.Generic.List[object]
for ($i = 0; $i -lt $centres.Count; $i++) {
    $sx = [int][Math]::Round($originX + ($centres[$i] * $scale))
    if ($sx -lt ([double]$tRest.restingLeft + 20) -or $sx -gt ([double]$tRest.restingRight - 20)) { continue }
    Move-Pointer $sx $bandY
    Start-Sleep -Milliseconds 150
    Pump-MotionLog 0
    $m = LastMotion
    if ($m -and $m.inside -eq $true -and [double]$m.peak -gt 1.20) {
        $visible.Add([pscustomobject]@{ Index=$i; Dip=$centres[$i]; ScreenX=$sx; Peak=[double]$m.peak })
    }
}
Write-Host ("  centres answered : {0}" -f $visible.Count)
if ($visible.Count -lt 4) { throw "only $($visible.Count) centres answer - cannot sweep" }
$vs = $visible.ToArray()
Write-Host ("  screen centres   : {0}" -f (($vs | ForEach-Object { $_.ScreenX }) -join ' '))
$leftC = $vs[0].ScreenX; $rightC = $vs[-1].ScreenX
Write-Host ("  dock content     : screen x {0}..{1}" -f $leftC,$rightC)

# ================================================================ performance
Write-Host ''
Write-Host '=== performance: sweep inside the interaction region ===' -ForegroundColor Cyan
Write-Host '  The applied count only moves for samples that land inside the dock: a sweep that starts outside the'
Write-Host '  region and ends outside it contributes many queued samples and few applied ones. To measure the'
Write-Host '  engine rather than the arrival rate, the sweep stays inside the region the dock itself recorded.'
Force-Resting | Out-Null
$tNow = @(Transitions) | Select-Object -Last 1
$regionL = [double]$tNow.restingLeft + 12
$regionR = [double]$tNow.restingRight - 12
$outPath = Build-Path -From ([int]$regionL) -To ([int]$regionR) -Step 4
$backPath = Build-Path -From ([int][Math]::Round($regionR)) -To ([int][Math]::Round($regionL)) -Step 4
$fwd = Show-Path -Path $outPath -Label 'in-region out (step 4)'
$bwd = Show-Path -Path $backPath -Label 'in-region back (step 4)'
Write-Host ("    region swept: {0:F0}..{1:F0} (the dock recorded {2:F0}..{3:F0})" -f $regionL,$regionR,[double]$tNow.restingLeft,[double]$tNow.restingRight)
$passes = 3
$positions = ($fwd.Count + $bwd.Count) * $passes
Write-Host ("    passes={0}  positions={1}  dwell={2} ms  expected duration ~{3:N0} s" -f $passes,$positions,$DwellMs,(($positions*$DwellMs)/1000))
$mStart = LastMotion
$tStart = (Get-Date)
for ($pass = 1; $pass -le $passes; $pass++) {
    foreach ($x in $outPath) { Move-Pointer $x $bandY; Start-Sleep -Milliseconds $DwellMs }
    foreach ($x in $backPath) { Move-Pointer $x $bandY; Start-Sleep -Milliseconds $DwellMs }
}
$wallMs = ((Get-Date) - $tStart).TotalMilliseconds
$mEnd = Settle 10000
Pump-MotionLog 300
$perf = @($script:Lines | Select-String '"name":"motion\.performance"' | ForEach-Object { $_.Line | ConvertFrom-Json }) | Select-Object -Last 1
Write-Host ("  raw reports      : {0} -> {1}  (+{2})" -f $mStart.raw,$mEnd.raw,($mEnd.raw-$mStart.raw))
Write-Host ("  applied updates  : {0} -> {1}  (+{2})" -f $mStart.updates,$mEnd.updates,($mEnd.updates-$mStart.updates))
Write-Host ("  wall             : {0:N0} ms  ({1:N1} injected positions/s, {2:N1} applied updates/s)" -f $wallMs,($positions/($wallMs/1000)),(($mEnd.updates-$mStart.updates)/($wallMs/1000)))
if ($perf) {
    Write-Host ("  instrumentation  : latencySamples={0} queued={1} applied={2} dropped={3} reason={4}" -f $perf.samples,$perf.queued,$perf.applied,$perf.dropped,$perf.reason)
    Write-Host ("  latency us       : p50={0} p95={1} p99={2} avg={3} max={4}" -f $perf.latencyP50Us,$perf.latencyP95Us,$perf.latencyP99Us,$perf.latencyAverageUs,$perf.latencyMaxUs)
    Write-Host ("  arrival rate     : {0:N0} raw reports in {1:N1} s = {2:N1} /s" -f ($mEnd.raw-$mStart.raw),($wallMs/1000),(($mEnd.raw-$mStart.raw)/($wallMs/1000)))
} else { Write-Host '  instrumentation  : no motion.performance mark produced' }

# ================================================================ between icons
Write-Host ''
Write-Host '=== between icons ===' -ForegroundColor Cyan
Force-Resting | Out-Null
$mid = [int]($vs.Count / 2)
$a = $vs[$mid]; $b = $vs[$mid + 1]
$midX = [int](($a.ScreenX + $b.ScreenX) / 2)
Write-Host ("  A index={0} screenX={1} dip={2:F1} | B index={3} screenX={4} dip={5:F1}" -f $a.Index,$a.ScreenX,$a.Dip,$b.Index,$b.ScreenX,$b.Dip)
Write-Host ("  midX = {0}" -f $midX)
Move-Pointer $midX $bandY
Settle 5000 | Out-Null
Start-Sleep -Milliseconds 400; Pump-MotionLog 0
$mB = LastMotion
$dists = @($centres | ForEach-Object { [Math]::Abs($_ - [double]$mB.pointer) } | Sort-Object)
Write-Host ("  engine pointer={0:F1} peak={1:F4} reach={2:F2} inside={3}" -f $mB.pointer,$mB.peak,$mB.reach,$mB.inside)
Write-Host ("  distance to the nearest two resting centres: {0:F2} and {1:F2}  (delta {2:F2})" -f $dists[0],$dists[1],[Math]::Abs($dists[1]-$dists[0]))
Write-Host ("  pointer screen x={0} maps back to dock {1:F1}; the two centres are at {2:F1} and {3:F1}" -f $midX,$mB.pointer,$a.Dip,$b.Dip)

# ================================================================ screenshots
Write-Host ''
Write-Host '=== screenshots ===' -ForegroundColor Cyan
function Save-Shot([string] $name) {
    $path = Join-Path $OutDir $name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    & $native observe --hwnd $dockHwnd --maxElements 700 --outputDir $OutDir 2>&1 | Out-Null
    $latest = Get-ChildItem "$OutDir\window-*.png" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $latest) { return $false }
    Copy-Item $latest.FullName $path -Force
    return (Test-Path -LiteralPath $path)
}
Force-Resting | Out-Null
$shots = [ordered]@{}
$shots['stageB-resting.png'] = Save-Shot 'stageB-resting.png'
Move-Pointer $vs[[int]($vs.Count/2)].ScreenX $bandY; Settle 5000 | Out-Null
$shots['stageB-expanded.png'] = Save-Shot 'stageB-expanded.png'
$shots['stageB-centre.png'] = Save-Shot 'stageB-centre.png'
Move-Pointer $midX $bandY; Settle 5000 | Out-Null
$shots['stageB-between-icons.png'] = Save-Shot 'stageB-between-icons.png'
Move-Pointer $vs[0].ScreenX $bandY; Settle 5000 | Out-Null
$shots['stageB-left-edge.png'] = Save-Shot 'stageB-left-edge.png'
Move-Pointer $vs[-1].ScreenX $bandY; Settle 5000 | Out-Null
$shots['stageB-right-edge.png'] = Save-Shot 'stageB-right-edge.png'
Force-Resting | Out-Null
foreach ($k in $shots.Keys) {
    $p = Join-Path $OutDir $k
    $exists = Test-Path -LiteralPath $p
    $bytes = if ($exists) { (Get-Item $p).Length } else { 0 }
    Write-Host ("    {0,-28} exists={1,-6} bytes={2}" -f $k,$exists,$bytes)
}
$missing = @($shots.Keys | Where-Object { -not (Test-Path -LiteralPath (Join-Path $OutDir $_)) })
Write-Host ("  missing: {0}" -f (if ($missing.Count) { $missing -join ', ' } else { 'none' }))

Write-Host ''
Write-Host ("=== XAML exceptions in this run: FTL={0} ===" -f (@(Get-Content $script:AppLog | Select-String '\[FTL\]').Count - 1709))
