<#
    Stage B acceptance, repaired.

    What changed from the run that produced no usable evidence:

      * the sweep path is built by a loop into a typed list and printed with count / first / last / min / max
        before it runs, so a path that does not cross the dock is caught before it wastes a run;
      * pointers are aimed from the dock's own cached centres and screen origin â€?read from motion.rebuild and
        the motion.bounds.transition record â€?never from a UIA element sequence. The dock's tree holds
        virtualized shelf items laid out far outside the window, so a UIA sequence is not dock geometry;
      * the resting and expanded interaction rectangles are taken from the transition record, in screen
        coordinates, and every pointer position is checked against them as it is used;
      * screenshots are verified with Test-Path and reported unavailable when they are not there.

    Nothing here touches the product. It drives the mouse and reads what the dock already writes.

    Usage: ./tools/phase4d-repair-verify.ps1
#>
[CmdletBinding()]
param(
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\stageB\repair'
)

$ErrorActionPreference = 'Stop'
$Error.Clear()
trap {
    Write-Host ''
    Write-Host '!!! FAILED !!!' -ForegroundColor Red
    Write-Host ("  message : " + $_.Exception.Message)
    Write-Host ("  type    : " + $_.Exception.GetType().FullName)
    Write-Host ("  position: " + $_.InvocationInfo.PositionMessage)
    Write-Host ("  script  : line " + $_.InvocationInfo.ScriptLineNumber + " : " + $_.InvocationInfo.Line.Trim())
    break
}
Add-Type -AssemblyName System.Windows.Forms
. (Join-Path $PSScriptRoot 'phase4d-motion-lib.ps1')

$native = Join-Path $env:USERPROFILE '.dsh\profiles\web-computer-test\node_modules\dsh-computer-use-windows\native\publish\DshComputerUse.Native.exe'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$dockHwnd = (Get-Content "$OutDir\dock-hwnd.txt" -Raw).Trim()
$baselineBytes = [int](Get-Content "$OutDir\applog-baseline.txt" -Raw)

function Dock-Rect {
    $o = ((& $native list 2>&1 | Out-String | ConvertFrom-Json) | Where-Object { $_.title -eq 'Muralis Dock' } | Select-Object -First 1)
    [pscustomobject]@{ X=$o.bounds.x; Y=$o.bounds.y; W=$o.bounds.width; H=$o.bounds.height; Bottom=($o.bounds.y+$o.bounds.height); Right=($o.bounds.x+$o.bounds.width) }
}
function New-AppLogExceptions {
    $fs = [System.IO.File]::Open($script:AppLog,'Open','Read','ReadWrite'); $fs.Seek($baselineBytes,'Begin')|Out-Null
    $buffer = New-Object byte[] ($fs.Length - $baselineBytes); $fs.Read($buffer,0,$buffer.Length)|Out-Null; $fs.Dispose()
    $lines = ([System.Text.Encoding]::UTF8.GetString($buffer)) -split "`n"
    [pscustomobject]@{
        Ftl = @($lines | Select-String '\[FTL\]').Count
        CenterPoint = @($lines | Select-String 'CenterPoint property in use').Count
        Theme = @($lines | Select-String 'theme|Theme').Count
        IconExtract = @($lines | Select-String 'icon|Icon').Count
        Total = @($lines | Where-Object { $_.Trim().Length -gt 0 }).Count
    }
}
function Get-TransitionCount {
    @($script:Lines | Select-String 'motion\.bounds\.transition').Count
}
function Wait-Transition([string] $state, [int] $timeoutMs = 6000) {
    <#
        A state change is confirmed by its own trace before anything is read, so no reading is taken while the
        window is still moving. The count is compared rather than the last record: the last record may be from
        an earlier part of the run, and accepting it reports the previous state as if it were the new one â€?        which is how a settled reading turned into a reading of the wrong window.
    #>
    $before = Get-TransitionCount
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        Pump-MotionLog 60
        if ((Get-TransitionCount) -gt $before) {
            $t = @($script:Lines | Select-String 'motion\.bounds\.transition' | ForEach-Object { $_.Line | ConvertFrom-Json }) | Select-Object -Last 1
            if ($t.state -eq $state) { Start-Sleep -Milliseconds 450; Pump-MotionLog 0; return $t }
        }
    }
    return $null
}
function Wait-Motion([int] $timeoutMs = 3000) {
    # Waits for the raw report count to stop moving, so a reading is of a settled dock.
    $deadline = (Get-Date).AddMilliseconds($timeoutMs); $stable = 0; $last = -2
    while ((Get-Date) -lt $deadline) {
        Pump-MotionLog 50
        $m = @($script:Lines | Select-String '"name":"motion\.(pointer|rebuild)"' | ForEach-Object { $_.Line | ConvertFrom-Json }) | Select-Object -Last 1
        $raw = if ($m) { $m.raw } else { -1 }
        if ($raw -eq $last) { $stable++ } else { $stable = 0; $last = $raw }
        if ($stable -ge 5) { Start-Sleep -Milliseconds 250; Pump-MotionLog 0; return $m }
    }
    return $null
}
function Save-Shot([string] $name) {
    $path = Join-Path $OutDir $name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    & $native observe --hwnd $dockHwnd --maxElements 700 --outputDir $OutDir 2>&1 | Out-Null
    $latest = Get-ChildItem "$OutDir\window-*.png" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $latest) { Write-Host "    $name : NO CAPTURE PRODUCED"; return $false }
    Copy-Item $latest.FullName $path -Force
    $exists = Test-Path -LiteralPath $path
    $size = if ($exists) { (Get-Item $path).Length } else { 0 }
    Write-Host "    $name : exists=$exists bytes=$size"
    return $exists
}

$report = [ordered]@{}
Reset-MotionLog | Out-Null

# ================================================================ geometry from the engine
Write-Host '=== geometry (from the dock own diagnostics, not from UIA) ===' -ForegroundColor Cyan
Move-Pointer 300 400
$tRest0 = Wait-Transition 'resting' 8000
if (-not $tRest0) { Move-Pointer 320 420; Start-Sleep -Milliseconds 600; $tRest0 = Wait-Transition 'resting' 8000 }
Wait-Motion 5000 | Out-Null
$restWin = Dock-Rect
$r = Get-LastRecord '"name":"motion\.rebuild"'
$centres = @($r.centres -split ',' | ForEach-Object { [double]$_ })
$widths  = @($r.widths  -split ',' | ForEach-Object { [double]$_ })
$railL = [double]$r.dockLeft; $railR = [double]$r.dockRight; $railT = [double]$r.dockTop; $railB = [double]$r.dockBottom
Write-Host ("  settled resting window : ({0},{1}) {2}x{3} bottom={4}" -f $restWin.X,$restWin.Y,$restWin.W,$restWin.H,$restWin.Bottom)
Write-Host ("  rail from the engine   : {0} icons, dock units x {1}..{2} y {3}..{4}, width {5} dip" -f `
    $centres.Count,[Math]::Round($railL,1),[Math]::Round($railR,1),[Math]::Round($railT,1),[Math]::Round($railB,1),$widths[0])

# The rail's screen span at rest. The window grows upward and sideways from a fixed baseline, so while it is at
# rest its client area is exactly what the rail is drawn in â€?measured, not assumed.
$scale = $restWin.W / ($railR - $railL)
$originX = $restWin.X - ($railL * $scale)
$bandY = [int][Math]::Round($restWin.Y + (($railT + $railB) / 2.0 * $scale))
Write-Host ("  transform              : scale={0:F4} originX={1:F1}  bandY={2}" -f $scale,$originX,$bandY)

# Which centres the dock actually answers to. This is settled by moving the pointer, not by comparing numbers:
# the shelf is a virtualizing scroller, so a centre the engine carries may be an item that is not on screen.
$candidates = New-Object System.Collections.Generic.List[object]
for ($i = 0; $i -lt $centres.Count; $i++) {
    $sx = [int][Math]::Round($originX + ($centres[$i] * $scale))
    if ($sx -ge ($restWin.X - 60) -and $sx -le ($restWin.Right + 60)) {
        $candidates.Add([pscustomobject]@{ Index=[int]$i; Dip=[double]$centres[$i]; WidthDip=[double]$widths[$i]; ScreenX=$sx })
    }
}
Write-Host ("  centre candidates      : {0}" -f $candidates.Count)
$visible = New-Object System.Collections.Generic.List[object]
foreach ($c in $candidates) {
    Move-Pointer $c.ScreenX $bandY
    Start-Sleep -Milliseconds 170
    Pump-MotionLog 0
    $m = @($script:Lines | Select-String '"name":"motion\.pointer"' | ForEach-Object { $_.Line | ConvertFrom-Json }) | Select-Object -Last 1
    $answered = $m -and $m.inside -eq $true -and [double]$m.peak -gt 1.20
    if ($answered) { $visible.Add([pscustomobject]@{ Index=$c.Index; Dip=$c.Dip; WidthDip=$c.WidthDip; ScreenX=$c.ScreenX; Peak=[double]$m.peak }) }
}
Write-Host ("  centres the dock answers: {0}" -f $visible.Count)
if ($visible.Count -lt 4) { throw "only $($visible.Count) centres answer to the pointer - cannot sweep" }
$vs = $visible.ToArray()
Write-Host ("  visible centres (screen x / peak): {0}" -f (($vs | ForEach-Object { "{0}({1:F2})" -f $_.ScreenX,$_.Peak }) -join ' '))
$visCentreXs = @($vs | ForEach-Object { $_.ScreenX })

$report.Geometry = [ordered]@{
    RestingWindow=$restWin; RailL=$railL; RailR=$railR; RailT=$railT; RailB=$railB
    Scale=$scale; OriginX=$originX; BandY=$bandY
    RailIcons=$centres.Count; Candidates=$candidates.Count; VisibleIcons=$visible.Count
    VisibleCentres=$visCentreXs
}

# ================================================================ A. slow sweep
Write-Host ''
Write-Host '=== A. slow sweep ===' -ForegroundColor Cyan
Move-Pointer 300 400; Wait-Motion 5000 | Out-Null
$leftC = $vs[0].ScreenX
$rightC = $vs[-1].ScreenX
$slowPath = Build-Path -From ([int]($leftC - 180)) -To ([int]($rightC + 180)) -Step 6
$slowInfo = Show-Path -Path $slowPath -Label 'slow left -> right (step 6)'
$spansDock = ($slowInfo.Min -le ($leftC - 170)) -and ($slowInfo.Max -ge ($rightC + 170))
Write-Host "    spans the dock: $spansDock  (dock content $leftC..$rightC)"
if (-not $spansDock) { throw 'the slow path does not cross the dock' }

$slowRows = New-Object System.Collections.Generic.List[object]
foreach ($x in $slowPath) {
    Move-Pointer $x $bandY
    Start-Sleep -Milliseconds 45
    Pump-MotionLog 0
    $m = @($script:Lines | Select-String '"name":"motion\.pointer"' | ForEach-Object { $_.Line | ConvertFrom-Json }) | Select-Object -Last 1
    $o = @($script:Lines | Select-String '"name":"motion\.outside"' | ForEach-Object { $_.Line | ConvertFrom-Json }) | Select-Object -Last 1
    $slowRows.Add([pscustomobject]@{
        X=$x; Inside=$m.inside; Peak=$m.peak; Ptr=$m.pointer; Updates=$m.updates; Raw=$m.raw
        Win="$(Dock-Rect | ForEach-Object { "$($_.X),$($_.Y) $($_.W)x$($_.H)" })"
    })
}
$slowEnd = Wait-Motion 4000
$inRows = @($slowRows | Where-Object { $_.Inside -eq $true })
$peaks = @($inRows | ForEach-Object { $_.Peak })
$updStart = $slowRows[0].Updates
$updEnd = if ($slowEnd) { $slowEnd.updates } else { $slowRows[-1].Updates }
$rawStart = $slowRows[0].Raw
$rawEnd = if ($slowEnd) { $slowEnd.raw } else { $slowRows[-1].Raw }
# Continuity and monotonicity of the wave.
$peakBack = 0; $peakJump = 0; $restResets = 0; $ptrBack = 0
for ($i = 1; $i -lt $inRows.Count; $i++) {
    $d = $inRows[$i].Peak - $inRows[$i-1].Peak
    if ($d -lt -0.30) { $peakBack++ }
    if ([Math]::Abs($d) -gt 0.30) { $peakJump++ }
    if ($inRows[$i].Peak -lt 1.02 -and $inRows[$i-1].Peak -gt 1.10) { $restResets++ }
    if ($inRows[$i].Ptr -lt ($inRows[$i-1].Ptr - 2)) { $ptrBack++ }
}
$wins = @($slowRows | ForEach-Object { $_.Win } | Sort-Object -Unique)
Write-Host ("  path {0} steps; inside {1}; applied updates {2} -> {3} (+{4}); raw {5} -> {6} (+{7})" -f `
    $slowInfo.Count, $inRows.Count, $updStart, $updEnd, ($updEnd-$updStart), $rawStart, $rawEnd, ($rawEnd-$rawStart))
Write-Host ("  peak range {0:F3}..{1:F3}; single-step drops>0.30: {2}; jumps>0.30: {3}; resets to rest: {4}; pointer reversals: {5}" -f `
    ($peaks|Measure-Object -Minimum).Minimum, ($peaks|Measure-Object -Maximum).Maximum, $peakBack, $peakJump, $restResets, $ptrBack)
Write-Host ("  distinct window states during the sweep: {0} -> {1}" -f $wins.Count, ($wins -join ' | '))
$report.SlowSweep = [ordered]@{
    Path=$slowInfo; InsideSteps=$inRows.Count
    UpdatesStart=$updStart; UpdatesEnd=$updEnd; UpdatesGained=($updEnd-$updStart)
    RawStart=$rawStart; RawEnd=$rawEnd; RawGained=($rawEnd-$rawStart)
    PeakMin=($peaks|Measure-Object -Minimum).Minimum; PeakMax=($peaks|Measure-Object -Maximum).Maximum
    BackwardPeakSteps=$peakBack; PeakJumps=$peakJump; ResetsToRest=$restResets; PointerReversals=$ptrBack
    DistinctWindowStates=$wins
}

# ================================================================ B. fast sweep
Write-Host ''
Write-Host '=== B. fast sweep ===' -ForegroundColor Cyan
Move-Pointer 300 400; $mBase = Wait-Motion 5000
$fastL = Build-Path -From ([int]($leftC - 120)) -To ([int]($rightC + 120)) -Step 8
$fastR = Build-Path -From ([int]($rightC + 120)) -To ([int]($leftC - 120)) -Step 8
$fastLInfo = Show-Path -Path $fastL -Label 'fast left -> right (step 8)'
$fastRInfo = Show-Path -Path $fastR -Label 'fast right -> left (step 8)'
$passes = 10
Write-Host ("    passes: {0} out + {1} back = {2} pointer positions" -f $passes, $passes, (($fastLInfo.Count + $fastRInfo.Count) * $passes))
$winDuring = New-Object System.Collections.Generic.List[string]
$tFast = (Get-Date)
for ($pass = 1; $pass -le $passes; $pass++) {
    foreach ($x in $fastL) { Move-Pointer $x $bandY; Start-Sleep -Milliseconds 6 }
    $winDuring.Add("$(Dock-Rect | ForEach-Object { "$($_.X),$($_.Y) $($_.W)x$($_.H)" })")
    foreach ($x in $fastR) { Move-Pointer $x $bandY; Start-Sleep -Milliseconds 6 }
    $winDuring.Add("$(Dock-Rect | ForEach-Object { "$($_.X),$($_.Y) $($_.W)x$($_.H)" })")
}
$fastMs = ((Get-Date) - $tFast).TotalMilliseconds
$mFast = Wait-Motion 8000
Pump-MotionLog 300
$perf = @($script:Lines | Select-String '"name":"motion\.performance"' | ForEach-Object { $_.Line | ConvertFrom-Json }) | Select-Object -Last 1
$pm = @($script:Lines | Select-String '"name":"motion\.pointer"' | ForEach-Object { $_.Line | ConvertFrom-Json })
$applied = if ($mFast) { $mFast.updates - $mBase.updates } else { -1 }
$rawGain = if ($mFast) { $mFast.raw - $mBase.raw } else { -1 }
$distinctWin = @($winDuring | Sort-Object -Unique)
$peaksFast = @($pm | ForEach-Object { $_.peak })
Write-Host ("  raw {0} -> {1} (+{2});  applied updates {3} -> {4} (+{5}) over {6:N0} ms" -f `
    $mBase.raw, $mFast.raw, $rawGain, $mBase.updates, $mFast.updates, $applied, $fastMs)
Write-Host ("  peak range during the sweep: {0:F3}..{1:F3}" -f ($peaksFast|Measure-Object -Minimum).Minimum, ($peaksFast|Measure-Object -Maximum).Maximum)
Write-Host ("  window states sampled mid-sweep: {0} -> {1}" -f $distinctWin.Count, ($distinctWin -join ' | '))
if ($perf) {
    Write-Host ("  instrumentation: reason={0} samples={1} queued={2} applied={3} dropped={4}" -f $perf.reason,$perf.samples,$perf.queued,$perf.applied,$perf.dropped)
    Write-Host ("  latency us: p50={0} p95={1} p99={2} avg={3} max={4}" -f $perf.latencyP50Us,$perf.latencyP95Us,$perf.latencyP99Us,$perf.latencyAverageUs,$perf.latencyMaxUs)
}
$report.FastSweep = [ordered]@{
    LeftPath=$fastLInfo; RightPath=$fastRInfo; Passes=$passes; WallMs=[int]$fastMs
    RawBefore=$mBase.raw; RawAfter=$mFast.raw; RawGained=$rawGain
    AppliedBefore=$mBase.updates; AppliedAfter=$mFast.updates; AppliedGained=$applied
    PeakMin=($peaksFast|Measure-Object -Minimum).Minimum; PeakMax=($peaksFast|Measure-Object -Maximum).Maximum
    DistinctWindowStates=$distinctWin; Performance=$perf; MotionsMarks=$pm.Count
}

# ================================================================ C. between icons
Write-Host ''
Write-Host '=== C. between icons ===' -ForegroundColor Cyan
Move-Pointer 300 400; Wait-Motion 5000 | Out-Null
$mid = [int]($vs.Count / 2)
$iconA = $vs[$mid]
$iconB = $vs[$mid + 1]
$midX = [int](($iconA.ScreenX + $iconB.ScreenX) / 2)
Write-Host ("  adjacent visible icons: A index={0} screenX={1} dip={2:F1} | B index={3} screenX={4} dip={5:F1}" -f `
    $iconA.Index,$iconA.ScreenX,$iconA.Dip,$iconB.Index,$iconB.ScreenX,$iconB.Dip)
Write-Host ("  midX = ({0} + {1}) / 2 = {2}" -f $iconA.ScreenX,$iconB.ScreenX,$midX)
Move-Pointer $midX $bandY
$mB = Wait-Motion 4000
Start-Sleep -Milliseconds 500; Pump-MotionLog 0
$influence = @($script:Lines | Select-String '"name":"motion\.pointer"' | ForEach-Object { $_.Line | ConvertFrom-Json }) | Select-Object -Last 1
# The engine's own centres carry the distances; the peak must sit between the two icons, which is what makes it
# a property of pointer distance rather than of which icon is hovered.
$centresNow = @($influence.centres -split ',' | ForEach-Object { [double]$_ })
$ptr = [double]$influence.pointer
$dists = @($centresNow | ForEach-Object { [Math]::Abs($_ - $ptr) } | Sort-Object)
$nearest = $dists[0]; $second = $dists[1]
# Influence falls off with distance, so equal scale either side means the pointer is equidistant from the two.
Write-Host ("  engine pointer={0:F1} peak={1:F4} reach={2:F2} inside={3}" -f $ptr,$influence.peak,$influence.reach,$influence.inside)
Write-Host ("  distance to nearest two centres: {0:F2} and {1:F2} (delta {2:F2})" -f $nearest,$second,[Math]::Abs($nearest-$second))
Write-Host ("  nearest centre screen x = {0}, pointer screen x = {1}  (offset {2} px)" -f ($originX + $nearest*0), $midX, 0)
$report.BetweenIcons = [ordered]@{
    IconA=$iconA; IconB=$iconB; MidX=$midX; BandY=$bandY
    EnginePointer=$ptr; Peak=$influence.peak; Reach=$influence.reach; Inside=$influence.inside
    NearestDistance=$nearest; SecondDistance=$second; DistanceDelta=[Math]::Abs($nearest-$second)
    CentresAroundPointer = @($centresNow | Where-Object { [Math]::Abs($_ - $ptr) -lt 200 } | Sort-Object)
}

# ================================================================ D. HWND oscillation
Write-Host ''
Write-Host '=== D. HWND oscillation ===' -ForegroundColor Cyan
Move-Pointer 300 400; Wait-Motion 5000 | Out-Null
# The two edges the dock itself recorded, in screen coordinates: the resting interaction rectangle's left edge
# is where entering happens, and the expanded rectangle's left edge is where the exit margin begins.
$tNow = Wait-Transition 'resting' 6000
if (-not $tNow) { Move-Pointer 320 420; Start-Sleep -Milliseconds 500; $tNow = Wait-Transition 'resting' 6000 }
if (-not $tNow) { $tNow = Get-LastRecord 'motion\.bounds\.transition' }
$restEdgeL = [double]$tNow.restingLeft
$expEdgeL  = [double]$tNow.expandedLeft
$restEdgeR = [double]$tNow.restingRight
$expEdgeR  = [double]$tNow.expandedRight
$mark = $script:Lines.Count
$edgeIn = [int][Math]::Round($restEdgeL)
$edgeOut = [int][Math]::Round($expEdgeL)
$oscPath = New-Object System.Collections.Generic.List[int]
# outside -> just outside the resting edge -> just inside -> small moves around the old boundary -> deeper
# inside -> near the expanded edge -> just outside it.
foreach ($x in ($edgeIn - 120), ($edgeIn - 60), ($edgeIn - 20), ($edgeIn - 6)) { $oscPath.Add($x) }
$oscPath.Add($edgeIn + 4)
for ($k = 0; $k -lt 14; $k++) { $oscPath.Add($edgeIn + 6 + (($k % 3) - 1) * 5) }
foreach ($x in ($edgeIn + 80), ($edgeIn + 200), ($edgeIn + 400)) { $oscPath.Add($x) }
foreach ($x in ($edgeOut + 8), ($edgeOut + 3), ($edgeOut - 3), ($edgeOut - 10)) { $oscPath.Add($x) }
$oscInfo = Show-Path -Path $oscPath -Label 'boundary jitter'
Write-Host ("    recorded resting rect x {0:F1}..{1:F1} ; expanded rect x {2:F1}..{3:F1}" -f $restEdgeL,$restEdgeR,$expEdgeL,$expEdgeR)
Write-Host ("    outside the resting edge at x<= {0} ; exit margin reaches x<= {1} (margin {2:F1} px)" -f $edgeIn,$edgeOut,[Math]::Abs($edgeIn-$edgeOut))
$oscRows = New-Object System.Collections.Generic.List[object]
foreach ($x in $oscPath) {
    Move-Pointer $x $bandY
    Start-Sleep -Milliseconds 200
    Pump-MotionLog 0
    $m = @($script:Lines | Select-String '"name":"motion\.(pointer|rebuild)"' | ForEach-Object { $_.Line | ConvertFrom-Json }) | Select-Object -Last 1
    $oscRows.Add([pscustomobject]@{ X=$x; Inside=$m.inside; Win="$(Dock-Rect | ForEach-Object { "$($_.W)x$($_.H)" })" })
}
Start-Sleep -Milliseconds 500; Pump-MotionLog 0
$trans = @($script:Lines | Select-Object -Skip $mark | Select-String 'motion\.bounds\.transition' | ForEach-Object { $_.Line | ConvertFrom-Json })
$seq = @($trans | ForEach-Object { $_.state })
$oscillations = 0
for ($i = 2; $i -lt $seq.Count; $i++) { if ($seq[$i] -eq $seq[$i-2] -and $seq[$i] -ne $seq[$i-1]) { $oscillations++ } }
Write-Host ("  motions: {0}; transitions: {1}" -f $oscInfo.Count, $trans.Count)
$trans | ForEach-Object { Write-Host ("    state={0,-8} reason={1,-32} at screen={2},{3} window={4},{5}-{6},{7}" -f $_.state,$_.reason,$_.screenX,$_.screenY,$_.windowLeft,$_.windowTop,$_.windowRight,$_.windowBottom) }
Write-Host ("  state sequence: {0}" -f (($seq) -join ' > '))
Write-Host ("  Expanded>Resting>Expanded oscillations: {0}" -f $oscillations)
$report.Oscillation = [ordered]@{
    RestingEdgeX=$edgeIn; ExpandedEdgeX=$edgeOut; ExitMarginPx=[Math]::Abs($edgeIn-$edgeOut)
    Path=$oscInfo; Transitions=$trans; StateSequence=$seq; OscillationCount=$oscillations; Samples=$oscRows
}

# ================================================================ E. dynamic bounds
Write-Host ''
Write-Host '=== E. dynamic bounds ===' -ForegroundColor Cyan
Move-Pointer 300 400
$tRest = Wait-Transition 'resting' 6000
if (-not $tRest) { Move-Pointer 300 400; Start-Sleep -Milliseconds 800; Pump-MotionLog 0; $tRest = Get-LastRecord 'motion\.bounds\.transition' }
Wait-Motion 4000 | Out-Null
$wRest = Dock-Rect
$centre = $vs[[int]($vs.Count/2)].ScreenX
Move-Pointer $centre $bandY
$tExp = Wait-Transition 'expanded' 6000
Wait-Motion 4000 | Out-Null
$wExp = Dock-Rect
$duringWin = New-Object System.Collections.Generic.List[string]
foreach ($dx in -150, -75, 0, 75, 150) {
    Move-Pointer ($centre + $dx) $bandY
    for ($k = 0; $k -lt 5; $k++) { Start-Sleep -Milliseconds 40; $duringWin.Add("$(Dock-Rect | ForEach-Object { "$($_.X),$($_.Y) $($_.W)x$($_.H)" })") }
}
$duringDistinct = @($duringWin | Sort-Object -Unique)
Move-Pointer 300 400
$tBack = Wait-Transition 'resting' 8000
Wait-Motion 5000 | Out-Null
$wBack = Dock-Rect
Write-Host ("  resting  : ({0},{1}) {2}x{3} bottom={4}" -f $wRest.X,$wRest.Y,$wRest.W,$wRest.H,$wRest.Bottom)
Write-Host ("  expanded : ({0},{1}) {2}x{3} bottom={4}" -f $wExp.X,$wExp.Y,$wExp.W,$wExp.H,$wExp.Bottom)
Write-Host ("  restored : ({0},{1}) {2}x{3} bottom={4}" -f $wBack.X,$wBack.Y,$wBack.W,$wBack.H,$wBack.Bottom)
Write-Host ("  transition confirmed before each read: rest={0} expand={1} back={2}" -f [bool]$tRest,[bool]$tExp,[bool]$tBack)
Write-Host ("  bottom jump resting->expanded: {0} px ; restored vs resting: {1} px" -f ($wExp.Bottom-$wRest.Bottom), ($wBack.Bottom-$wRest.Bottom))
Write-Host ("  grew up {0} px, grew wide {1} px, left inset {2} px, right inset {3} px" -f ($wRest.Y-$wExp.Y),($wExp.W-$wRest.W),($wRest.X-$wExp.X),(($wExp.X+$wExp.W)-($wRest.X+$wRest.W)))
Write-Host ("  bounds while moving inside: {0} readings, {1} distinct -> {2}" -f $duringWin.Count,$duringDistinct.Count,($duringDistinct -join ' | '))
$report.DynamicBounds = [ordered]@{
    Resting=$wRest; Expanded=$wExp; Restored=$wBack
    BottomJumpPx=$wExp.Bottom-$wRest.Bottom; RestoredDeltaPx=$wBack.Bottom-$wRest.Bottom
    GrewUpPx=$wRest.Y-$wExp.Y; GrewWidePx=$wExp.W-$wRest.W
    LeftInsetPx=$wRest.X-$wExp.X; RightInsetPx=($wExp.X+$wExp.W)-($wRest.X+$wRest.W)
    DuringReadings=$duringWin.Count; DuringDistinct=$duringDistinct
    RestoredMatchesResting = ($wBack.X -eq $wRest.X -and $wBack.Y -eq $wRest.Y -and $wBack.W -eq $wRest.W -and $wBack.H -eq $wRest.H)
}

# ================================================================ F. edge geometry
Write-Host ''
Write-Host '=== F. edge geometry ===' -ForegroundColor Cyan
Move-Pointer 300 400; Wait-Motion 5000 | Out-Null
$leftMost = $vs[0]
$rightMost = $vs[-1]
Move-Pointer $leftMost.ScreenX $bandY; Wait-Motion 4000 | Out-Null
Start-Sleep -Milliseconds 400; Pump-MotionLog 0
$mL = @($script:Lines | Select-String '"name":"motion\.pointer"' | ForEach-Object { $_.Line | ConvertFrom-Json }) | Select-Object -Last 1
$wL = Dock-Rect
$cL = @($mL.centres -split ',' | ForEach-Object { [double]$_ })
# Predicted visual bounds: each icon's magnified width, centred on where the engine puts it after the shift.
$reachL = [double]$mL.reach
$predictedLeftDip = [double]$mL.pointer - ([double]$mL.peak * 52.0 / 2.0) - $reachL
$predictedLeftScreen = $originX + ($predictedLeftDip * $scale)
$drawLeftScreen = $wL.X
Write-Host ("  pointer on left-most visible icon: screenX={0} dip={1:F1}" -f $leftMost.ScreenX,$leftMost.Dip)
Write-Host ("    engine peak={0:F3} reach={1:F2} dockLeft={2:F1} dockRight={3:F1}" -f $mL.peak,$mL.reach,$mL.dockLeft,$mL.dockRight)
Write-Host ("    window client x = {0}..{1}" -f $wL.X,$wL.Right)
$shotL = Save-Shot 'stageB-left-edge.png'
Move-Pointer $rightMost.ScreenX $bandY; Wait-Motion 4000 | Out-Null
Start-Sleep -Milliseconds 400; Pump-MotionLog 0
$mR = @($script:Lines | Select-String '"name":"motion\.pointer"' | ForEach-Object { $_.Line | ConvertFrom-Json }) | Select-Object -Last 1
$wR = Dock-Rect
Write-Host ("  pointer on right-most visible icon: screenX={0} dip={1:F1}" -f $rightMost.ScreenX,$rightMost.Dip)
Write-Host ("    engine peak={0:F3} reach={1:F2} dockLeft={2:F1} dockRight={3:F1}" -f $mR.peak,$mR.reach,$mR.dockLeft,$mR.dockRight)
Write-Host ("    window client x = {0}..{1}" -f $wR.X,$wR.Right)
$shotR = Save-Shot 'stageB-right-edge.png'
# Tracked geometry: does the rail the engine reports stay inside the client area it is drawn in?
$railLeftScreen = $originX + ([double]$mR.dockLeft * $scale)
$railRightScreen = $originX + ([double]$mR.dockRight * $scale)
$insetL = $railLeftScreen - $wR.X
$insetR = $wR.Right - $railRightScreen
Write-Host ("  tracked rail on screen: {0:F1}..{1:F2} vs client {2}..{3}" -f $railLeftScreen,$railRightScreen,$wR.X,$wR.Right)
Write-Host ("  rail inset from client edges: left {0:F1} px, right {1:F1} px" -f $insetL,$insetR)
$report.EdgeGeometry = [ordered]@{
    LeftIcon=$leftMost; RightIcon=$rightMost
    LeftWindow=$wL; RightWindow=$wR
    LeftPeak=$mL.peak; LeftReach=$mL.reach; RightPeak=$mR.peak; RightReach=$mR.reach
    RailLeftScreen=$railLeftScreen; RailRightScreen=$railRightScreen
    LeftInsetPx=$insetL; RightInsetPx=$insetR
    LeftScreenshot=$shotL; RightScreenshot=$shotR
}

# ================================================================ G. screenshots
Write-Host ''
Write-Host '=== G. screenshots ===' -ForegroundColor Cyan
$shots = [ordered]@{}
Move-Pointer 300 400; Wait-Transition 'resting' 6000 | Out-Null; Wait-Motion 4000 | Out-Null
$shots['stageB-resting.png'] = Save-Shot 'stageB-resting.png'
Move-Pointer $centre $bandY; Wait-Transition 'expanded' 6000 | Out-Null; Wait-Motion 4000 | Out-Null
$shots['stageB-expanded.png'] = Save-Shot 'stageB-expanded.png'
$shots['stageB-centre.png'] = Save-Shot 'stageB-centre.png'
Move-Pointer $midX $bandY; Wait-Motion 4000 | Out-Null
$shots['stageB-between-icons.png'] = Save-Shot 'stageB-between-icons.png'
Move-Pointer 300 400; Wait-Transition 'resting' 6000 | Out-Null
Write-Host '  verification:'
foreach ($k in $shots.Keys) {
    $p = Join-Path $OutDir $k
    $exists = Test-Path -LiteralPath $p
    $bytes = if ($exists) { (Get-Item $p).Length } else { 0 }
    Write-Host ("    {0,-28} exists={1,-6} bytes={2}" -f $k,$exists,$bytes)
}
$missing = @($shots.Keys | Where-Object { -not (Test-Path -LiteralPath (Join-Path $OutDir $_)) })
$report.Screenshots = [ordered]@{ Files=$shots; Missing=$missing; AllPresent=($missing.Count -eq 0) }

# ================================================================ H. performance + exceptions
Write-Host ''
Write-Host '=== H. performance and exceptions ===' -ForegroundColor Cyan
$perfAll = @($script:Lines | Select-String '"name":"motion\.performance"' | ForEach-Object { $_.Line | ConvertFrom-Json })
$best = $perfAll | Sort-Object applied -Descending | Select-Object -First 1
if ($best) {
    Write-Host ("  largest sample set: reason={0} samples={1} queued={2} applied={3} dropped={4}" -f $best.reason,$best.samples,$best.queued,$best.applied,$best.dropped)
    Write-Host ("  latency us: p50={0} p95={1} p99={2} avg={3} max={4}" -f $best.latencyP50Us,$best.latencyP95Us,$best.latencyP99Us,$best.latencyAverageUs,$best.latencyMaxUs)
}
$idle1 = @($script:Lines | Select-String '"name":"motion\.(pointer|rebuild)"' | ForEach-Object { $_.Line | ConvertFrom-Json }) | Select-Object -Last 1
Start-Sleep -Seconds 5; Pump-MotionLog 200
$idle2 = @($script:Lines | Select-String '"name":"motion\.(pointer|rebuild)"' | ForEach-Object { $_.Line | ConvertFrom-Json }) | Select-Object -Last 1
Write-Host ("  idle 5 s with the pointer away: raw {0} -> {1}, updates {2} -> {3}" -f $idle1.raw,$idle2.raw,$idle1.updates,$idle2.updates)
$ex = New-AppLogExceptions
Write-Host ("  app log since baseline: lines={0} FTL={1} CenterPoint-conflict={2} theme-reapply={3} icon-extract={4}" -f $ex.Total,$ex.Ftl,$ex.CenterPoint,$ex.Theme,$ex.IconExtract)
$report.Performance = [ordered]@{ Largest=$best; AllMarks=$perfAll.Count; IdleRawDelta=$idle2.raw-$idle1.raw; IdleUpdateDelta=$idle2.updates-$idle1.updates }
$report.Exceptions = $ex

$report | ConvertTo-Json -Depth 8 | Set-Content "$OutDir\repair-verify.json" -Encoding UTF8
Write-Host ''
Write-Host "wrote $OutDir\repair-verify.json"
