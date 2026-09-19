<#
    Stage B acceptance, run against a dock that is verified to be standing still.

    Earlier attempts reported a rail whose width went negative, and the cause was not in the dock: the shelf was
    still laying itself out while it was being measured. The leftmost icon advanced from 241 to 1031 dock units
    and the icon count fell from 45 to 33 over a few seconds, and a rail measured mid-layout is a rail measured
    at no particular moment. So this run refuses to measure until the dock has stopped writing, and refuses to
    report rather than report numbers that describe nothing.

    The screen mapping is taken from the transition trace and then checked against the engine's own pointer:
    aiming at the screen position of a cached centre must make the engine report that same centre. A mapping
    that survives the check is the real one; one that does not is discarded rather than used.

    Usage: ./tools/phase4d-acceptance.ps1
#>
[CmdletBinding()]
param(
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\stageB\stable',
    [int] $SweepDwellMs = 45
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
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$dockHwnd = (Get-Content "$OutDir\dock-hwnd.txt" -Raw).Trim()
$baselineBytes = [int](Get-Content "$OutDir\applog-baseline.txt" -Raw)

function Dock-Rect {
    $o = ((& $native list 2>&1 | Out-String | ConvertFrom-Json) | Where-Object { $_.title -eq 'Muralis Dock' } | Select-Object -First 1)
    [pscustomobject]@{ X=$o.bounds.x; Y=$o.bounds.y; W=$o.bounds.width; H=$o.bounds.height; Bottom=($o.bounds.y+$o.bounds.height); Right=($o.bounds.x+$o.bounds.width) }
}
function Rebuilds { @($script:Lines | Select-String '"name":"motion\.rebuild"' | ForEach-Object { $_.Line | ConvertFrom-Json }) }
function Transitions { @($script:Lines | Select-String 'motion\.bounds\.transition' | ForEach-Object { $_.Line | ConvertFrom-Json }) }
function LastMotion { @($script:Lines | Select-String '"name":"motion\.(pointer|rebuild)"' | ForEach-Object { $_.Line | ConvertFrom-Json }) | Select-Object -Last 1 }
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
function AppLog {
    $fs = [System.IO.File]::Open($script:AppLog,'Open','Read','ReadWrite'); $fs.Seek($baselineBytes,'Begin')|Out-Null
    $buffer = New-Object byte[] ($fs.Length - $baselineBytes); $fs.Read($buffer,0,$buffer.Length)|Out-Null; $fs.Dispose()
    $lines = ([System.Text.Encoding]::UTF8.GetString($buffer)) -split "`n"
    [pscustomobject]@{
        Ftl = @($lines | Select-String '\[FTL\]').Count
        CenterPoint = @($lines | Select-String 'CenterPoint property in use').Count
        Theme = @($lines | Select-String 'theme|Theme').Count
        Icon = @($lines | Select-String 'icon|Icon').Count
        Lines = @($lines | Where-Object { $_.Trim().Length -gt 0 }).Count
    }
}
function Save-Shot([string] $name) {
    $path = Join-Path $OutDir $name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    & $native observe --hwnd $dockHwnd --maxElements 700 --outputDir $OutDir 2>&1 | Out-Null
    $latest = Get-ChildItem "$OutDir\window-*.png" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $latest) { return $false }
    Copy-Item $latest.FullName $path -Force
    return (Test-Path -LiteralPath $path)
}

Write-Host '=== wait for the dock to stand still ===' -ForegroundColor Cyan
$seeded = Seed-MotionLog
Write-Host "  seeded $seeded records from the log"
Move-Pointer 300 400
$still = $null
for ($check = 1; $check -le 16; $check++) {
    $before = (Rebuilds).Count
    Start-Sleep -Seconds 3
    Pump-MotionLog 0
    $rb = Rebuilds
    if ($rb.Count -ge 1) {
        $last = $rb[-1]
        $width = [double]$last.dockRight - [double]$last.dockLeft
        Write-Host ("  check {0,2}: rebuilds={1} new={2} icons={3} railL={4} width={5}" -f $check,$rb.Count,($rb.Count-$before),$last.icons,[Math]::Round($last.dockLeft,0),[Math]::Round($width,0))
        if (($rb.Count - $before) -eq 0 -and $width -gt 100) { $still = $last; break }
    } else {
        Write-Host ("  check {0,2}: no rebuild yet" -f $check)
    }
}
if (-not $still) { throw 'the dock never stopped writing; a measurement now would describe no particular moment' }
$railL = [double]$still.dockLeft; $railR = [double]$still.dockRight
$railT = [double]$still.dockTop;  $railB = [double]$still.dockBottom
$centreArr = @($still.centres -split ',' | ForEach-Object { [double]$_ })
Write-Host ("  stable rail: {0} icons, dock units x {1}..{2} (width {3}), y {4}..{5}" -f `
    $centreArr.Count,[Math]::Round($railL,0),[Math]::Round($railR,0),[Math]::Round($railR-$railL,0),[Math]::Round($railT,0),[Math]::Round($railB,0))

Write-Host ''
Write-Host '=== transform, from the dock own transition trace ===' -ForegroundColor Cyan
$restWin = Dock-Rect
for ($i = 0; $i -lt 12; $i++) {
    Move-Pointer (1150 + ($i * 25)) 1363
    Start-Sleep -Milliseconds 200
    Pump-MotionLog 0
    if ((Transitions).Count -gt 0) { break }
}
Start-Sleep -Milliseconds 800; Pump-MotionLog 0
$tExp = @(Transitions) | Where-Object { $_.state -eq 'expanded' } | Select-Object -Last 1
if (-not $tExp) { throw 'the dock never reported entering its expanded state' }
$restL = [double]$tExp.restingLeft; $restR = [double]$tExp.restingRight
$restT = [double]$tExp.restingTop;  $restB = [double]$tExp.restingBottom
$expL  = [double]$tExp.expandedLeft; $expR = [double]$tExp.expandedRight
if (($restR - $restL) -le 100) { throw ('the recorded resting rect is degenerate: {0:F1}..{1:F1}' -f $restL,$restR) }
$scale = ($restR - $restL) / ($railR - $railL)
$originScreenX = $restL - ($railL * $scale)
$bandY = [int][Math]::Round($restT + (($restB - $restT) / 2.0))
Write-Host ("  resting window : ({0},{1}) {2}x{3} bottom={4}" -f $restWin.X,$restWin.Y,$restWin.W,$restWin.H,$restWin.Bottom)
Write-Host ("  resting rect   : x {0:F1}..{1:F1} (width {2:F1})  y {3:F1}..{4:F1}" -f $restL,$restR,($restR-$restL),$restT,$restB)
Write-Host ("  expanded rect  : x {0:F1}..{1:F1} (width {2:F1})" -f $expL,$expR,($expR-$expL))
Write-Host ("  candidate transform: scale={0:F4} originScreenX={1:F1} bandY={2}" -f $scale,$originScreenX,$bandY)

Write-Host ''
Write-Host '=== locate the icons empirically, by peak ===' -ForegroundColor Cyan
# No transform is used here, and none is needed. The dock recorded the screen rectangle its pointer must enter,
# so sweeping inside that rectangle and reading the engine's own peak locates the icons directly: the peak is
# largest when the pointer is nearest a resting centre, and smallest when it sits between two of them. This
# cannot be thrown off by a clamped rail, a DPI conversion or a stale origin, because it measures the answer
# rather than deriving it.
$scan = Build-Path -From ([int]($restL + 8)) -To ([int]($restR - 8)) -Step 4
$scanInfo = Show-Path -Path $scan -Label 'peak scan (step 4)'
$peakRows = New-Object System.Collections.Generic.List[object]
foreach ($x in $scan) {
    Move-Pointer $x $bandY
    Start-Sleep -Milliseconds 120
    Pump-MotionLog 0
    $m = LastMotion
    $peakRows.Add([pscustomobject]@{ X=$x; Peak=[double]$m.peak; Inside=$m.inside; Ptr=[double]$m.pointer })
}
$insideRows = @($peakRows | Where-Object { $_.Inside -eq $true })
Write-Host ("  scanned {0} positions; engine reported inside at {1}" -f $scanInfo.Count,$insideRows.Count)
if ($insideRows.Count -lt 20) { throw "the engine reported inside at only $($insideRows.Count) positions" }
# Local maxima, with a minimum separation so one icon is not counted twice.
$maxima = New-Object System.Collections.Generic.List[object]
$minSep = 30
foreach ($row in $insideRows) {
    $near = @($insideRows | Where-Object { [Math]::Abs($_.X - $row.X) -le $minSep })
    $isMax = $true
    foreach ($o in $near) { if ($o.Peak -gt $row.Peak) { $isMax = $false; break } }
    if (-not $isMax) { continue }
    if ($maxima.Count -gt 0 -and [Math]::Abs($maxima[-1].X - $row.X) -lt $minSep) {
        if ($row.Peak -gt $maxima[-1].Peak) { $maxima[-1] = $row }
        continue
    }
    $maxima.Add($row)
}
Write-Host ("  local peak maxima found: {0}" -f $maxima.Count)
if ($maxima.Count -lt 4) { throw "only $($maxima.Count) peaks found; cannot sweep" }
$vs = $maxima.ToArray()
Write-Host ("  icon centres (screenX | peak | enginePointer): {0}" -f (($vs | ForEach-Object { "$($_.X)|$([Math]::Round($_.Peak,2))|$([Math]::Round($_.Ptr,0))" }) -join '  '))
$leftC = $vs[0].X; $rightC = $vs[-1].X
$centre = $vs[[int]($vs.Count/2)].X
$mid = [int]($vs.Count / 2)
$pairA = $vs[$mid]; $pairB = $vs[$mid + 1]
$midX = [int](($pairA.X + $pairB.X) / 2)
$peakMin = ($vs | ForEach-Object { $_.Peak } | Measure-Object -Minimum).Minimum
$peakMax = ($vs | ForEach-Object { $_.Peak } | Measure-Object -Maximum).Maximum
Write-Host ("  peak at the centres runs {0:F3}..{1:F3}; profile MaxScale is 1.8" -f $peakMin,$peakMax)
Write-Host ("  will use: left={0} centre={1} between={2} right={3}" -f $leftC,$centre,$midX,$rightC)
# The value between the two middle icons, for the symmetry reading.
$valley = @($insideRows | Where-Object { $_.X -gt $pairA.X -and $_.X -lt $pairB.X })
Write-Host ("  between the two middle icons: {0} samples, peak {1:F3}..{2:F3}" -f `
    $valley.Count, (($valley | ForEach-Object { $_.Peak } | Measure-Object -Minimum).Minimum), (($valley | ForEach-Object { $_.Peak } | Measure-Object -Maximum).Maximum))

Write-Host ''
Write-Host '=== A. slow sweep ===' -ForegroundColor Cyan
Move-Pointer 300 400; Settle 5000 | Out-Null
$slow = Build-Path -From ([int]($leftC - 170)) -To ([int]($rightC + 170)) -Step 6
$slowInfo = Show-Path -Path $slow -Label 'slow left -> right (step 6)'
Write-Host ("    crosses the dock: {0}" -f (($slowInfo.Min -le $leftC - 160) -and ($slowInfo.Max -ge $rightC + 160)))
$rows = New-Object System.Collections.Generic.List[object]
foreach ($x in $slow) {
    Move-Pointer $x $bandY
    Start-Sleep -Milliseconds 45
    Pump-MotionLog 0
    $m = LastMotion
    $rows.Add([pscustomobject]@{ X=$x; Inside=$m.inside; Peak=$m.peak; Ptr=$m.pointer; Updates=$m.updates; Raw=$m.raw; Win="$(Dock-Rect | ForEach-Object { "$($_.W)x$($_.H)" })" })
}
$end = Settle 5000
$inRows = @($rows | Where-Object { $_.Inside -eq $true })
$peaks = @($inRows | ForEach-Object { $_.Peak })
$back = 0; $jump = 0; $reset = 0
for ($i = 1; $i -lt $inRows.Count; $i++) {
    $d = $inRows[$i].Peak - $inRows[$i-1].Peak
    if ($d -lt -0.30) { $back++ }
    if ([Math]::Abs($d) -gt 0.30) { $jump++ }
    if ($inRows[$i].Peak -lt 1.02 -and $inRows[$i-1].Peak -gt 1.10) { $reset++ }
}
$wins = @($rows | ForEach-Object { $_.Win } | Sort-Object -Unique)
Write-Host ("  steps={0} inside={1}  updates {2} -> {3} (+{4})  raw {5} -> {6} (+{7})" -f `
    $slowInfo.Count,$inRows.Count,$rows[0].Updates,$end.updates,($end.updates-$rows[0].Updates),$rows[0].Raw,$end.raw,($end.raw-$rows[0].Raw))
Write-Host ("  peak {0:F3}..{1:F3}  drops>0.30={2} jumps>0.30={3} resets-to-rest={4}" -f ($peaks|Measure-Object -Minimum).Minimum,($peaks|Measure-Object -Maximum).Maximum,$back,$jump,$reset)
Write-Host ("  window sizes during the sweep: {0} -> {1}" -f $wins.Count,($wins -join ' | '))

Write-Host ''
Write-Host '=== B. fast sweep ===' -ForegroundColor Cyan
Move-Pointer 300 400; $mBase = Settle 5000
$fwd = Build-Path -From ([int]($restL + 10)) -To ([int]($restR - 10)) -Step 5
$bwd = Build-Path -From ([int]($restR - 10)) -To ([int]($restL + 10)) -Step 5
$fwdInfo = Show-Path -Path $fwd -Label 'fast out (step 5)'
$bwdInfo = Show-Path -Path $bwd -Label 'fast back (step 5)'
$passes = 4
$positions = ($fwdInfo.Count + $bwdInfo.Count) * $passes
Write-Host ("    passes={0}  positions={1}  dwell={2} ms" -f $passes,$positions,$SweepDwellMs)
$tFast = (Get-Date)
$winDuring = New-Object System.Collections.Generic.List[string]
for ($pass = 1; $pass -le $passes; $pass++) {
    foreach ($x in $fwd) { Move-Pointer $x $bandY; Start-Sleep -Milliseconds $SweepDwellMs }
    $winDuring.Add("$(Dock-Rect | ForEach-Object { "$($_.W)x$($_.H)" })")
    foreach ($x in $bwd) { Move-Pointer $x $bandY; Start-Sleep -Milliseconds $SweepDwellMs }
    $winDuring.Add("$(Dock-Rect | ForEach-Object { "$($_.W)x$($_.H)" })")
}
$fastMs = ((Get-Date) - $tFast).TotalMilliseconds
$mFast = Settle 10000
Pump-MotionLog 300
$perf = @($script:Lines | Select-String '"name":"motion\.performance"' | ForEach-Object { $_.Line | ConvertFrom-Json }) | Select-Object -Last 1
$applied = $mFast.updates - $mBase.updates
$rawGain = $mFast.raw - $mBase.raw
$distinctWin = @($winDuring | Sort-Object -Unique)
Write-Host ("  raw {0} -> {1} (+{2})   applied {3} -> {4} (+{5}) in {6:N0} ms" -f $mBase.raw,$mFast.raw,$rawGain,$mBase.updates,$mFast.updates,$applied,$fastMs)
Write-Host ("  rates: injected {0:N0}/s, raw {1:N1}/s, applied {2:N1}/s" -f ($positions/($fastMs/1000)),($rawGain/($fastMs/1000)),($applied/($fastMs/1000)))
Write-Host ("  window sizes mid-sweep: {0} -> {1}" -f $distinctWin.Count,($distinctWin -join ' | '))
if ($perf) {
    Write-Host ("  instrumentation: latencySamples={0} queued={1} applied={2} dropped={3}" -f $perf.samples,$perf.queued,$perf.applied,$perf.dropped)
    Write-Host ("  latency us     : p50={0} p95={1} p99={2} avg={3} max={4}" -f $perf.latencyP50Us,$perf.latencyP95Us,$perf.latencyP99Us,$perf.latencyAverageUs,$perf.latencyMaxUs)
}

Write-Host ''
Write-Host '=== C. between icons ===' -ForegroundColor Cyan
Move-Pointer 300 400; Settle 5000 | Out-Null
Write-Host ("  A index={0} screenX={1} dip={2:F1} | B index={3} screenX={4} dip={5:F1}" -f $pairA.Index,$pairA.X,$pairA.Ptr,$pairB.Index,$pairB.X,$pairB.Ptr)
Write-Host ("  midX = {0}" -f $midX)
Move-Pointer $midX $bandY
Settle 5000 | Out-Null
$mB = LastMotion
$dists = @($centreArr | ForEach-Object { [Math]::Abs($_ - [double]$mB.pointer) } | Sort-Object)
Write-Host ("  engine pointer={0:F1}  peak={1:F4}  reach={2:F2}  inside={3}" -f $mB.pointer,$mB.peak,$mB.reach,$mB.inside)
Write-Host ("  distance to the nearest two resting centres: {0:F2} and {1:F2}  (delta {2:F2})" -f $dists[0],$dists[1],[Math]::Abs($dists[1]-$dists[0]))
Write-Host ("  icons at dock {0:F1} and {1:F1}; pointer at {2:F1}; their midpoint is {3:F1}" -f $pairA.Ptr,$pairB.Ptr,[double]$mB.pointer,(($pairA.Ptr+$pairB.Ptr)/2))
$bw = Dock-Rect
Write-Host ("  window while between icons: {0}x{1} at ({2},{3})" -f $bw.W,$bw.H,$bw.X,$bw.Y)

Write-Host ''
Write-Host '=== D. HWND oscillation ===' -ForegroundColor Cyan
Move-Pointer 300 400; Settle 6000 | Out-Null
$mark = $script:Lines.Count
$edgeIn = [int][Math]::Round($restL)
$edgeOut = [int][Math]::Round($expL)
$path = New-Object System.Collections.Generic.List[int]
foreach ($x in ($edgeIn-140),($edgeIn-70),($edgeIn-25),($edgeIn-8)) { $path.Add($x) }
$path.Add($edgeIn + 5)
for ($k = 0; $k -lt 16; $k++) { $path.Add($edgeIn + 7 + (($k % 4) - 1) * 6) }
foreach ($x in ($edgeIn+90),($edgeIn+220),($edgeIn+420)) { $path.Add($x) }
foreach ($x in ($edgeOut+10),($edgeOut+4),($edgeOut-4),($edgeOut-12)) { $path.Add($x) }
$oscInfo = Show-Path -Path $path -Label 'boundary jitter'
Write-Host ("    resting edge x={0}  expanded edge x={1}  (exit margin {2} px)" -f $edgeIn,$edgeOut,[Math]::Abs($edgeIn-$edgeOut))
foreach ($x in $path) { Move-Pointer $x $bandY; Start-Sleep -Milliseconds 190 }
Start-Sleep -Milliseconds 600; Pump-MotionLog 0
$trans = @($script:Lines | Select-Object -Skip $mark | Select-String 'motion\.bounds\.transition' | ForEach-Object { $_.Line | ConvertFrom-Json })
$seq = @($trans | ForEach-Object { $_.state })
$osc = 0
for ($i = 2; $i -lt $seq.Count; $i++) { if ($seq[$i] -eq $seq[$i-2] -and $seq[$i] -ne $seq[$i-1]) { $osc++ } }
Write-Host ("  motions={0}  transitions={1}" -f $oscInfo.Count,$trans.Count)
$trans | ForEach-Object { Write-Host ("    {0,-8} {1,-32} screen={2},{3}" -f $_.state,$_.reason,$_.screenX,$_.screenY) }
Write-Host ("  sequence: {0}" -f (($seq) -join ' > '))
Write-Host ("  Expanded>Resting>Expanded oscillations: {0}" -f $osc)

Write-Host ''
Write-Host '=== E. dynamic bounds ===' -ForegroundColor Cyan
Move-Pointer 300 400
$sawRest = $false
for ($i = 0; $i -lt 12; $i++) { Move-Pointer 300 (400+$i); Start-Sleep -Milliseconds 400; Pump-MotionLog 0; if ((@(Transitions) | Select-Object -Last 1).state -eq 'resting') { $sawRest = $true; break } }
Settle 5000 | Out-Null
$wRest = Dock-Rect
Move-Pointer $centre $bandY
$sawExpand = $false
for ($i = 0; $i -lt 20; $i++) { Start-Sleep -Milliseconds 250; Pump-MotionLog 0; if ((@(Transitions) | Select-Object -Last 1).state -eq 'expanded') { $sawExpand = $true; break } }
Settle 4000 | Out-Null
$wExp = Dock-Rect
$during = New-Object System.Collections.Generic.List[string]
foreach ($dx in -140,-70,0,70,140) { Move-Pointer ($centre+$dx) $bandY; for ($k=0;$k -lt 5;$k++){ Start-Sleep -Milliseconds 40; $during.Add("$(Dock-Rect | ForEach-Object { "$($_.X),$($_.Y) $($_.W)x$($_.H)" })") } }
$duringDistinct = @($during | Sort-Object -Unique)
Move-Pointer 300 400
$sawBack = $false
for ($i = 0; $i -lt 20; $i++) { Move-Pointer 300 (400+$i); Start-Sleep -Milliseconds 300; Pump-MotionLog 0; if ((@(Transitions) | Select-Object -Last 1).state -eq 'resting') { $sawBack = $true; break } }
Settle 5000 | Out-Null
$wBack = Dock-Rect
Write-Host ("  resting  : ({0},{1}) {2}x{3} bottom={4}" -f $wRest.X,$wRest.Y,$wRest.W,$wRest.H,$wRest.Bottom)
Write-Host ("  expanded : ({0},{1}) {2}x{3} bottom={4}" -f $wExp.X,$wExp.Y,$wExp.W,$wExp.H,$wExp.Bottom)
Write-Host ("  restored : ({0},{1}) {2}x{3} bottom={4}" -f $wBack.X,$wBack.Y,$wBack.W,$wBack.H,$wBack.Bottom)
Write-Host ("  transition confirmed before each read: rest={0} expand={1} back={2}" -f $sawRest,$sawExpand,$sawBack)
Write-Host ("  bottom jump: {0} px ; restored vs resting: {1} px" -f ($wExp.Bottom-$wRest.Bottom),($wBack.Bottom-$wRest.Bottom))
Write-Host ("  grew up {0} px, grew wide {1} px, insets L {2} R {3}" -f ($wRest.Y-$wExp.Y),($wExp.W-$wRest.W),($wRest.X-$wExp.X),(($wExp.X+$wExp.W)-($wRest.X+$wRest.W)))
Write-Host ("  inside-move readings: {0} -> {1} distinct" -f $during.Count,$duringDistinct.Count)

Write-Host ''
Write-Host '=== F. edge geometry and screenshots ===' -ForegroundColor Cyan
$shots = [ordered]@{}
Move-Pointer 300 (400+3); Start-Sleep -Milliseconds 900; Settle 4000 | Out-Null
$shots['stageB-resting.png'] = Save-Shot 'stageB-resting.png'
Move-Pointer $centre $bandY; Settle 4000 | Out-Null
$shots['stageB-expanded.png'] = Save-Shot 'stageB-expanded.png'
$shots['stageB-centre.png'] = Save-Shot 'stageB-centre.png'
Move-Pointer $midX $bandY; Settle 4000 | Out-Null
$shots['stageB-between-icons.png'] = Save-Shot 'stageB-between-icons.png'
Move-Pointer $vs[0].X $bandY; Settle 4000 | Out-Null
$mL = LastMotion; $wL = Dock-Rect
$shots['stageB-left-edge.png'] = Save-Shot 'stageB-left-edge.png'
Move-Pointer $vs[-1].X $bandY; Settle 4000 | Out-Null
$mR = LastMotion; $wR = Dock-Rect
$shots['stageB-right-edge.png'] = Save-Shot 'stageB-right-edge.png'
Move-Pointer 300 (400+4); Start-Sleep -Milliseconds 900; Settle 4000 | Out-Null
Write-Host ("  left-most confirmed icon screenX={0} (dip {1:F1}); peak={2:F3} reach={3:F2}" -f $vs[0].X,$vs[0].Ptr,$mL.peak,$mL.reach)
Write-Host ("  right-most confirmed icon screenX={0} (dip {1:F1}); peak={2:F3} reach={3:F2}" -f $vs[-1].X,$vs[-1].Ptr,$mR.peak,$mR.reach)
Write-Host ("  window client at left pose {0}..{1}; right pose {2}..{3}" -f $wL.X,$wL.Right,$wR.X,$wR.Right)
Write-Host '  screenshots:'
foreach ($k in $shots.Keys) {
    $p = Join-Path $OutDir $k
    $ok = Test-Path -LiteralPath $p
    $bytes = if ($ok) { (Get-Item $p).Length } else { 0 }
    Write-Host ("    {0,-28} exists={1,-6} bytes={2}" -f $k,$ok,$bytes)
}
$missing = @($shots.Keys | Where-Object { -not (Test-Path -LiteralPath (Join-Path $OutDir $_)) })
Write-Host ("  missing: {0}" -f $(if ($missing.Count) { $missing -join ', ' } else { 'none' }))

Write-Host ''
Write-Host '=== G. exceptions and idle ===' -ForegroundColor Cyan
$ex = AppLog
Write-Host ("  app log since baseline: lines={0} FTL={1} CenterPoint={2} theme={3} icon={4}" -f $ex.Lines,$ex.Ftl,$ex.CenterPoint,$ex.Theme,$ex.Icon)
$idleA = LastMotion
Start-Sleep -Seconds 5; Pump-MotionLog 200
$idleB = LastMotion
Write-Host ("  idle 5 s with the pointer away: raw {0} -> {1}, updates {2} -> {3}" -f $idleA.raw,$idleB.raw,$idleA.updates,$idleB.updates)
Write-Host ''
Write-Host '=== done ===' -ForegroundColor Cyan
