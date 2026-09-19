<#
    Final Stage B GUI acceptance.

    Readings come from outside the process: UI Automation for what the dock drew, the window's own rectangle for
    its bounds, and the dock's profiler log for what the motion computed. Nothing is inferred from the source.

    Three things this script gets right, each of which produced convincing and wrong numbers before:
      * the log is read by file position, never re-parsed whole — re-reading it stalls the run and shows up
        later as latency the dock never had;
      * every reading waits for the raw report count to stop changing, so a snapshot is of a settled dock;
      * the pointer is aimed at the engine's own resting centres, not at evenly spaced guesses. The rail is not
        uniform — pinned apps and shelf items are different widths, and an even-spacing assumption aims between
        the icons and reports that nothing happened.

    Usage: ./tools/phase4d-final-verify.ps1
#>
[CmdletBinding()]
param(
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\stageB\final'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$native = Join-Path $env:USERPROFILE '.dsh\profiles\web-computer-test\node_modules\dsh-computer-use-windows\native\publish\DshComputerUse.Native.exe'
$log = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
$applog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\muralis-20260918.log'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$dockHwnd = (Get-Content "$OutDir\dock-hwnd.txt" -Raw).Trim()

Add-Type -Namespace F -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll", SetLastError=true)] public static extern int GetWindowLongW(IntPtr h, int i);
public const uint MOVE=0x0001, ABSOLUTE=0x8000, LEFTDOWN=0x0002, LEFTUP=0x0004, WHEEL=0x0800;
public const int GWL_EXSTYLE=-20, WS_EX_TOPMOST=0x00000008, WS_EX_NOACTIVATE=0x08000000, WS_EX_TOOLWINDOW=0x00000080;
'@
$sw = [F.W]::GetSystemMetrics(0); $sh = [F.W]::GetSystemMetrics(1)

$script:pos = 0; $script:tail = ''; $script:lines = New-Object System.Collections.Generic.List[string]
function Read-NewLogLines {
    if (-not (Test-Path -LiteralPath $log)) { return }
    try { $fs = [System.IO.File]::Open($log, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite) } catch { return }
    try {
        if ($fs.Length -lt $script:pos) { $script:pos = 0; $script:tail = '' }
        if ($fs.Length -eq $script:pos) { return }
        $fs.Seek($script:pos, [System.IO.SeekOrigin]::Begin) | Out-Null
        $buf = New-Object byte[] ($fs.Length - $script:pos)
        $n = $fs.Read($buf, 0, $buf.Length); $script:pos += $n
        $text = $script:tail + [System.Text.Encoding]::UTF8.GetString($buf, 0, $n)
        $parts = $text -split "`n"; $script:tail = $parts[-1]
        for ($i = 0; $i -lt $parts.Count - 1; $i++) { if ($parts[$i].Length -gt 0) { $script:lines.Add($parts[$i]) } }
    } finally { $fs.Dispose() }
}
function Pump([int] $ms = 0) {
    $deadline = (Get-Date).AddMilliseconds($ms)
    do { Read-NewLogLines; if ($ms -le 0) { break }; Start-Sleep -Milliseconds 35 } while ((Get-Date) -lt $deadline)
}
function Mks([string] $name) { @($script:lines | Select-String $name | ForEach-Object { $_.Line | ConvertFrom-Json }) }
function Motion {
    $e = @($script:lines | Select-String '"motion\.(pointer|rebuild)"' | ForEach-Object { $_.Line | ConvertFrom-Json })
    if (-not $e) { return $null }
    $l = $e[-1]
    [pscustomobject]@{ Updates=$l.updates; Raw=$l.raw; Inside=$l.inside; Peak=$l.peak; Reach=$l.reach; Pointer=$l.pointer; Icons=$l.icons; Sane=$l.sane }
}
function Settle([int] $timeoutMs = 3000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs); $stable = 0; $last = -1
    while ((Get-Date) -lt $deadline) {
        Pump 50
        $m = Motion; $raw = if ($m) { $m.Raw } else { -2 }
        if ($raw -eq $last) { $stable++ } else { $stable = 0; $last = $raw }
        if ($stable -ge 5) { Start-Sleep -Milliseconds 250; Pump 0; return }
    }
}
function Ptr([int]$x, [int]$y) {
    [F.W]::mouse_event([F.W]::MOVE -bor [F.W]::ABSOLUTE, [int](($x * 65535) / ($sw - 1)), [int](($y * 65535) / ($sh - 1)), 0, [IntPtr]::Zero)
}
function Down { [F.W]::mouse_event([F.W]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero) }
function Up { [F.W]::mouse_event([F.W]::LEFTUP, 0, 0, 0, [IntPtr]::Zero) }
function WRect {
    $o = ((& $native list 2>&1 | Out-String | ConvertFrom-Json) | Where-Object { $_.title -eq 'Muralis Dock' } | Select-Object -First 1)
    [pscustomobject]@{ X=$o.bounds.x; Y=$o.bounds.y; W=$o.bounds.width; H=$o.bounds.height; Bottom=($o.bounds.y + $o.bounds.height) }
}
function Icons {
    $o = (& $native observe --hwnd $dockHwnd --maxElements 700 --outputDir $OutDir 2>&1 | Out-String | ConvertFrom-Json)
    @($o.elements) | Where-Object { $_.automationId -eq 'DockIconMotion' -and $_.bounds.y -ge 0 -and $_.bounds.y -lt 250 } | Sort-Object { $_.bounds.x }
}
function MaxIconW { param($icons) if (-not $icons -or $icons.Count -eq 0) { return 0 }; ($icons | ForEach-Object { $_.bounds.width } | Measure-Object -Maximum).Maximum }
function Shot([string] $name) {
    & $native observe --hwnd $dockHwnd --maxElements 700 --outputDir $OutDir 2>&1 | Out-Null
    $s = Get-ChildItem "$OutDir\window-*.png" | Sort-Object LastWriteTime | Select-Object -Last 1
    Copy-Item $s.FullName "$OutDir\final-$name.png" -Force
}
function FtlSince {
    if (-not (Test-Path $applog)) { return [pscustomobject]@{ Ftl=0; CenterPoint=0 } }
    $size = [int](Get-Content "$OutDir\applog-baseline.txt" -Raw)
    $fs = [System.IO.File]::Open($applog,'Open','Read','ReadWrite'); $fs.Seek($size,'Begin')|Out-Null
    $buf = New-Object byte[] ($fs.Length - $size); $fs.Read($buf,0,$buf.Length)|Out-Null; $fs.Dispose()
    $t = [System.Text.Encoding]::UTF8.GetString($buf)
    [pscustomobject]@{ Ftl = @($t -split "`n" | Select-String '\[FTL\]').Count; CenterPoint = @($t -split "`n" | Select-String 'CenterPoint property in use').Count }
}

$report = [ordered]@{}

# ================================================================ 1. baseline
Write-Host '=== 1. baseline ===' -ForegroundColor Cyan
Pump 0
Ptr 300 400; Settle
$r0 = WRect
$ic0 = @(Icons)
$ex = [F.W]::GetWindowLongW([IntPtr][int]$dockHwnd, [F.W]::GWL_EXSTYLE)
$src = @(Mks '"label":"motion\.pointer\.source"') | Select-Object -Last 1
$startup = @(Mks '"label":"rawinput\.startup"') | Select-Object -Last 1
$rebuild = @(Mks '"name":"motion\.rebuild"') | Select-Object -Last 1
Write-Host "  dock window: ($($r0.X),$($r0.Y)) $($r0.W)x$($r0.H) bottom=$($r0.Bottom)"
Write-Host "  icons drawn: $($ic0.Count)  (engine rail: $($rebuild.icons) icons, width $(($rebuild.widths -split ',')[0]) DIP)"
Write-Host "  exstyle 0x$('{0:X8}' -f $ex)  TOPMOST=$([bool]($ex -band [F.W]::WS_EX_TOPMOST))  DPI scale=$(($r0.W / 819.2).ToString('F4'))"
Write-Host "  broker: consumers=$($src.brokerConsumers) opened=$($src.opened) mouseBefore=$($src.mouseBefore) mouseAfter=$($src.mouseAfter)"
Write-Host "  startup audit: total=$($startup.total) mouse=$($startup.mouse) other=$($startup.other) shellState=$($startup.shellState)"

# The engine's own resting centres, in DIP, and the transform that puts them on the screen. Aiming at these
# rather than at evenly spaced guesses is what makes the pointer land on an icon every time.
$scale = $r0.W / 819.2
$origX = [double]$rebuild.originX
$origY = [double]$rebuild.originY
$winLocalL = $r0.X - $origX
$winLocalT = $r0.Y - $origY
$centresDip = @($rebuild.centres -split ',' | ForEach-Object { [double]$_ })
$widthsDip  = @($rebuild.widths  -split ',' | ForEach-Object { [double]$_ })
# Only the icons the window actually draws: a rail longer than the window is clipped, and clipping happens in
# the window's own coordinates.
$visible = @()
for ($i = 0; $i -lt $centresDip.Count; $i++) {
    $localX = $winLocalL + ($centresDip[$i] * $scale)
    if ($localX -ge 6 -and $localX -le ($r0.W - 6)) {
        $visible += [pscustomobject]@{ Index=$i; Dip=$centresDip[$i]; WidthDip=$widthsDip[$i]; ScreenX=[int]$localX }
    }
}
Write-Host "  visible rail: $($visible.Count) of $($centresDip.Count) icons"
Write-Host "    index / screenX / widthDIP: $(($visible | ForEach-Object { "$($_.Index):$($_.ScreenX)/$([int]$_.WidthDip)" }) -join '  ')"
$railTopLocal = ($ic0 | ForEach-Object { $_.bounds.y } | Measure-Object -Minimum).Minimum
$bandY = $r0.Y + $railTopLocal + 26

$report.Baseline = [ordered]@{
    Window=$r0; IconsDrawn=$ic0.Count; EngineIcons=$rebuild.icons; DpiScale=$scale
    ExStyle=('0x{0:X8}' -f $ex); TopMost=[bool]($ex -band [F.W]::WS_EX_TOPMOST)
    BrokerConsumers=$src.brokerConsumers; MouseBefore=$src.mouseBefore; MouseAfter=$src.mouseAfter
    StartupAudit=$startup; VisibleRail=$visible; BandY=$bandY
}

if ($visible.Count -lt 3) { throw "only $($visible.Count) visible icons" }
$leftIcon   = $visible[0]
$rightIcon  = $visible[-1]
$midIcon    = $visible[[int]($visible.Count / 2)]
$centreIcon = $midIcon
# A pair in the middle, for the between-icons reading.
$pairIdx = [int]($visible.Count / 2)
$pairA = $visible[$pairIdx]
$pairB = $visible[$pairIdx + 1]
$betweenX = [int](($pairA.ScreenX + $pairB.ScreenX) / 2)

# ================================================================ 2. transform ownership
Write-Host ''
Write-Host '=== 2. transform ownership / max scale ===' -ForegroundColor Cyan
Ptr 300 400; Settle
$restW = MaxIconW (Icons)
Ptr $centreIcon.ScreenX $bandY; Settle
$hoverIc = @(Icons); $hoverW = MaxIconW $hoverIc
$peakHover = (Motion).Peak
Down; Start-Sleep -Milliseconds 600; Pump 0
$pressW = MaxIconW (Icons); $peakPress = (Motion).Peak
Up; Start-Sleep -Milliseconds 1000; Settle
$relW = MaxIconW (Icons)
Ptr $betweenX $bandY; Settle
$betweenW = MaxIconW (Icons)
Ptr 300 400; Settle
$awayW = MaxIconW (Icons)
# The largest scale the engine ever reported across the whole session so far.
$allPeaks = @(Mks '"name":"motion\.pointer"' | ForEach-Object { $_.peak })
$peakMax = ($allPeaks | Measure-Object -Maximum).Maximum
$maxWseen = [Math]::Max([Math]::Max($restW, $hoverW), [Math]::Max($pressW, $betweenW))
Write-Host "  max icon width: rest=$restW hover=$hoverW press=$pressW afterRelease=$relW between=$betweenW away=$awayW"
Write-Host "  engine peak: hover=$peakHover press=$peakPress  session max=$peakMax"
Write-Host "  => measured max scale $([Math]::Round($peakMax,4)); double-scale would be $([Math]::Round(1.8*1.08,3))"
Write-Host "  geometry restored after leave: width=$awayW (rest was $restW)"
$report.TransformOwnership = [ordered]@{
    RestWidth=$restW; HoverWidth=$hoverW; PressWidth=$pressW; AfterReleaseWidth=$relW
    BetweenWidth=$betweenW; AwayWidth=$awayW; PeakHover=$peakHover; PeakPress=$peakPress
    SessionPeakMax=$peakMax; MaxWidthSeen=$maxWseen
}

# ================================================================ 3. slow sweep
Write-Host ''
Write-Host '=== 3. slow sweep ===' -ForegroundColor Cyan
Ptr 300 400; Settle
$slow = New-Object System.Collections.Generic.List[object]
$first = $leftIcon.ScreenX
$last = $rightIcon.ScreenX
$path = @()
$path += ($first - 260) + 0..0 | Out-Null
for ($x = $first - 260; $x -lt $first; $x += 26) { $path += $x }
for ($x = $first; $x -le $last; $x += 13) { $path += $x }
for ($x = $last + 26; $x -le ($last + 260); $x += 26) { $path += $x }
foreach ($x in $path) {
    Ptr $x $bandY
    Start-Sleep -Milliseconds 60
    Pump 0
    $m = Motion
    $slow.Add([pscustomobject]@{ X=$x; Inside=$m.Inside; Peak=$m.Peak; Pointer=$m.Pointer; Updates=$m.Updates; Raw=$m.Raw; Win="$(WRect)" })
}
Settle
$inSteps = @($slow | Where-Object { $_.Inside -eq $true })
$peaks = @($inSteps | ForEach-Object { $_.Peak })
$ptrs  = @($inSteps | ForEach-Object { $_.Pointer })
# Continuity of the wave: pointer and peak must both sweep once, without reversals or resets.
$reversals = 0; $resets = 0
for ($i = 1; $i -lt $inSteps.Count; $i++) {
    if ($inSteps[$i].Pointer -lt $inSteps[$i-1].Pointer - 1) { $reversals++ }
    if ($inSteps[$i].Peak -lt 1.0001 -and $inSteps[$i-1].Peak -gt 1.05) { $resets++ }
}
$peakJumps = 0
for ($i = 1; $i -lt $inSteps.Count; $i++) { if ([Math]::Abs($inSteps[$i].Peak - $inSteps[$i-1].Peak) -gt 0.25) { $peakJumps++ } }
$wins = @($slow | ForEach-Object { $_.Win } | Sort-Object -Unique)
Write-Host "  steps=$($slow.Count) inside=$($inSteps.Count) peak=$((($peaks|Measure-Object -Minimum).Minimum))..$((($peaks|Measure-Object -Maximum).Maximum))"
Write-Host "  pointer range in dock space: $((($ptrs|Measure-Object -Minimum).Minimum))..$((($ptrs|Measure-Object -Maximum).Maximum))"
Write-Host "  pointer reversals=$reversals  engine resets-to-rest=$resets  peak jumps>0.25=$peakJumps"
Write-Host "  distinct window states: $($wins.Count) -> $($wins -join ' | ')"
$report.SlowSweep = [ordered]@{
    Steps=$slow.Count; InsideSteps=$inSteps.Count
    PeakMin=($peaks|Measure-Object -Minimum).Minimum; PeakMax=($peaks|Measure-Object -Maximum).Maximum
    PointerMin=($ptrs|Measure-Object -Minimum).Minimum; PointerMax=($ptrs|Measure-Object -Maximum).Maximum
    Reversals=$reversals; ResetsToRest=$resets; PeakJumps=$peakJumps; DistinctWindowStates=$wins
}

# ================================================================ 4. fast sweep
Write-Host ''
Write-Host '=== 4. fast sweep ===' -ForegroundColor Cyan
Ptr 300 400; Settle
$pStart = Motion
$winStates = New-Object System.Collections.Generic.List[string]
$switch = (Get-Date)
for ($pass = 1; $pass -le 8; $pass++) {
    for ($x = $first; $x -le $last; $x += 9) { Ptr $x $bandY; Start-Sleep -Milliseconds 7 }
    $winStates.Add("$(WRect)")
    for ($x = $last; $x -ge $first; $x -= 9) { Ptr $x $bandY; Start-Sleep -Milliseconds 7 }
    $winStates.Add("$(WRect)")
}
$sweepMs = ((Get-Date) - $switch).TotalMilliseconds
Settle 8000
$pEnd = Motion
$perf = @(Mks '"name":"motion\.performance"') | Select-Object -Last 1
$pm = @(Mks '"name":"motion\.pointer"')
$deltas = New-Object System.Collections.Generic.List[double]
for ($i = 1; $i -lt $pm.Count; $i++) { $deltas.Add([Math]::Round($pm[$i].t - $pm[$i-1].t, 3)) }
$ds = @($deltas | Sort-Object)
$distinctWin = @($winStates | Sort-Object -Unique)
$allP = @($pm | ForEach-Object { $_.peak })
Write-Host "  raw reports: $($pStart.Raw) -> $($pEnd.Raw)   (+$($pEnd.Raw - $pStart.Raw))"
Write-Host "  applied updates: $($pStart.Updates) -> $($pEnd.Updates)   (+$($pEnd.Updates - $pStart.Updates) over $([int]$sweepMs) ms)"
Write-Host "  injected pointer positions: $(($last - $first) / 9 * 2 * 8) approx"
Write-Host "  peak range during the sweep: $((($allP|Measure-Object -Minimum).Minimum))..$((($allP|Measure-Object -Maximum).Maximum))"
Write-Host "  window states sampled mid-sweep: $($distinctWin.Count) -> $($distinctWin -join ' | ')"
if ($ds.Count) { Write-Host ("  interval between updates ms: p50={0:N2} p95={1:N2} p99={2:N2} min={3:N2} max={4:N2}" -f $ds[[int][Math]::Floor($ds.Count*0.5)], $ds[[int][Math]::Floor($ds.Count*0.95)], $ds[[int][Math]::Min($ds.Count-1,[int][Math]::Floor($ds.Count*0.99))], $ds[0], $ds[-1]) }
if ($perf) { Write-Host "  motion.performance: reason=$($perf.reason) samples=$($perf.samples) queued=$($perf.queued) applied=$($perf.applied) dropped=$($perf.dropped) p50=$($perf.latencyP50Us)us p95=$($perf.latencyP95Us)us p99=$($perf.latencyP99Us)us avg=$($perf.latencyAverageUs)us max=$($perf.latencyMaxUs)us" }
$report.FastSweep = [ordered]@{
    RawBefore=$pStart.Raw; RawAfter=$pEnd.Raw; UpdatesBefore=$pStart.Updates; UpdatesAfter=$pEnd.Updates
    SweepMs=[int]$sweepMs; DistinctWindowStates=$distinctWin; Performance=$perf
    PeakMin=($allP|Measure-Object -Minimum).Minimum; PeakMax=($allP|Measure-Object -Maximum).Maximum
    IntervalMs = if ($ds.Count) { [ordered]@{ P50=$ds[[int][Math]::Floor($ds.Count*0.5)]; P95=$ds[[int][Math]::Floor($ds.Count*0.95)]; P99=$ds[[int][Math]::Min($ds.Count-1,[int][Math]::Floor($ds.Count*0.99))]; Min=$ds[0]; Max=$ds[-1] } } else { $null }
}

# ================================================================ 5. oscillation
Write-Host ''
Write-Host '=== 5. HWND oscillation at the boundary ===' -ForegroundColor Cyan
Ptr 300 400; Settle
$mark = $script:lines.Count
$rr = WRect
$edge = $rr.X + 2
$osc = New-Object System.Collections.Generic.List[object]
foreach ($x in @($rr.X-90, $rr.X-45, $rr.X-18, $rr.X-6, $edge, ($edge+4), ($edge-4), ($edge+6), ($edge-2), ($edge+8), ($edge-1), ($edge+9), ($edge-6), ($edge+12)) ) {
    Ptr $x $bandY
    Start-Sleep -Milliseconds 250
    Pump 0
    $osc.Add([pscustomobject]@{ X=$x; Win="$(WRect)"; Inside=(Motion).Inside })
}
Start-Sleep -Milliseconds 600; Pump 0
$trans = @($script:lines | Select-Object -Skip $mark | Select-String 'motion\.bounds\.transition' | ForEach-Object { $_.Line | ConvertFrom-Json })
Write-Host "  jitter around the resting left edge x=$edge (window $($rr.X)..$($rr.X+$rr.W)):"
$osc | ForEach-Object { Write-Host "    x=$($_.X) inside=$($_.Inside) win=$($_.Win)" }
Write-Host "  transitions observed: $($trans.Count)"
$trans | ForEach-Object { Write-Host "    t=$($_.t) $($_.state) reason=$($_.reason) at screen=$($_.screenX),$($_.screenY) window=$($_.windowLeft),$($_.windowTop)-$($_.windowRight),$($_.windowBottom)" }
$lastT = @($trans) | Select-Object -Last 1
if ($lastT) {
    Write-Host "  resting region : L=$([Math]::Round($lastT.restingLeft,1)) T=$([Math]::Round($lastT.restingTop,1)) R=$([Math]::Round($lastT.restingRight,1)) B=$([Math]::Round($lastT.restingBottom,1))"
    Write-Host "  expanded region: L=$([Math]::Round($lastT.expandedLeft,1)) T=$([Math]::Round($lastT.expandedTop,1)) R=$([Math]::Round($lastT.expandedRight,1)) B=$([Math]::Round($lastT.expandedBottom,1))"
}
$seq = @($trans | ForEach-Object { $_.state })
$oscillations = 0
for ($i = 2; $i -lt $seq.Count; $i++) { if ($seq[$i] -eq $seq[$i-2] -and $seq[$i] -ne $seq[$i-1]) { $oscillations++ } }
Write-Host "  state sequence: $($seq -join ' > ')"
Write-Host "  Expanded>Resting>Expanded oscillations: $oscillations"
$report.Oscillation = [ordered]@{
    EdgeX=$edge; RestingWindow=$rr; Samples=$osc; Transitions=$trans; StateSequence=$seq
    OscillationCount=$oscillations; LastRegions=$lastT
}

# ================================================================ 6. between icons
Write-Host ''
Write-Host '=== 6. between icons ===' -ForegroundColor Cyan
Ptr 300 400; Settle
Ptr $betweenX $bandY; Settle
$bWin = WRect; $bM = Motion; $bIc = @(Icons)
$bIg = @($bIc | Where-Object { $_.bounds.width -gt 53 } | Sort-Object { $_.bounds.x })
$wid = @($bIg | ForEach-Object { $_.bounds.width })
Write-Host "  window $($bWin.X),$($bWin.Y) $($bWin.W)x$($bWin.H)   peak=$($bM.Peak) reach=$($bM.Reach) inside=$($bM.Inside) sane=$($bM.Sane)"
Write-Host "  raisable icons: $($bIg.Count) widths=$($wid -join '/')"
# Symmetry: walk left and right from the widest icon and compare the pairs.
$peakIdx = 0
for ($i = 1; $i -lt $bIg.Count; $i++) { if ($bIg[$i].bounds.width -gt $bIg[$peakIdx].bounds.width) { $peakIdx = $i } }
$pairs = @()
for ($k = 1; $k -le 3; $k++) {
    $l = $peakIdx - $k; $r = $peakIdx + $k
    if ($l -ge 0 -and $r -lt $bIg.Count) {
        $pairs += [pscustomobject]@{ Step=$k; Left=$bIg[$l].bounds.width; Right=$bIg[$r].bounds.width; Delta=[Math]::Abs($bIg[$l].bounds.width - $bIg[$r].bounds.width) }
    }
}
Write-Host "  peak icon at screenX=$($bIg[$peakIdx].bounds.x) width=$($bIg[$peakIdx].bounds.width); between-hint x=$betweenX"
$pairs | ForEach-Object { Write-Host "    step $($_.Step): left=$($_.Left) right=$($_.Right) delta=$($_.Delta)" }
$report.BetweenIcons = [ordered]@{
    Window=$bWin; Peak=$bM.Peak; Reach=$bM.Reach; Inside=$bM.Inside; Sane=$bM.Sane
    RaisableCount=$bIg.Count; Widths=$wid; PeakIndex=$peakIdx; Pairs=$pairs
}

# ================================================================ 7. edge clipping
Write-Host ''
Write-Host '=== 7. edge clipping geometry ===' -ForegroundColor Cyan
Ptr $leftIcon.ScreenX $bandY; Settle
$lw = WRect; $li = @(Icons)
$liMag = @($li | Where-Object { $_.bounds.width -gt 53 } | Sort-Object { $_.bounds.x })
$leftMargin = if ($liMag.Count) { $liMag[0].bounds.x } else { -1 }
$leftTop = if ($liMag.Count) { $liMag[0].bounds.y } else { -1 }
$leftMaxW = MaxIconW $li
Shot 'edge-left'
Ptr $rightIcon.ScreenX $bandY; Settle
$rw2 = WRect; $ri = @(Icons)
$riMag = @($ri | Where-Object { $_.bounds.width -gt 53 } | Sort-Object { $_.bounds.x })
$rightMargin = if ($riMag.Count) { $rw2.W - ($riMag[-1].bounds.x + $riMag[-1].bounds.width) } else { -1 }
$rightTop = if ($riMag.Count) { $riMag[-1].bounds.y } else { -1 }
$rightMaxW = MaxIconW $ri
Shot 'edge-right'
Write-Host "  LEFT  pointer at x=$($leftIcon.ScreenX): window=$($lw.X),$($lw.Y) $($lw.W)x$($lw.H)"
Write-Host "        outermost raised icon: local x=$leftMargin top=$leftTop maxWidth=$leftMaxW"
Write-Host "        -> left inset=$leftMargin px, top inset=$leftTop px"
Write-Host "  RIGHT pointer at x=$($rightIcon.ScreenX): window=$($rw2.X),$($rw2.Y) $($rw2.W)x$($rw2.H)"
Write-Host "        outermost raised icon: local x=$($riMag[-1].bounds.x) top=$rightTop maxWidth=$rightMaxW"
Write-Host "        -> right inset=$rightMargin px, top inset=$rightTop px"
$report.EdgeClipping = [ordered]@{
    LeftWindow=$lw; RightWindow=$rw2
    LeftInsetPx=$leftMargin; LeftTopInsetPx=$leftTop; LeftMaxWidth=$leftMaxW
    RightInsetPx=$rightMargin; RightTopInsetPx=$rightTop; RightMaxWidth=$rightMaxW
    LeftRaised = $liMag | ForEach-Object { [ordered]@{ X=$_.bounds.x; W=$_.bounds.width; H=$_.bounds.height; Top=$_.bounds.y } }
    RightRaised = $riMag | ForEach-Object { [ordered]@{ X=$_.bounds.x; W=$_.bounds.width; H=$_.bounds.height; Top=$_.bounds.y } }
}

# ================================================================ 8. dynamic bounds
Write-Host ''
Write-Host '=== 8. dynamic bounds ===' -ForegroundColor Cyan
Ptr 300 400; Settle 5000
$dRest = WRect
Ptr $centreIcon.ScreenX $bandY; Settle
$dExp = WRect
$during = New-Object System.Collections.Generic.List[string]
foreach ($dx in -120, -60, 0, 60, 120) {
    Ptr ($centreIcon.ScreenX + $dx) $bandY
    for ($k = 0; $k -lt 5; $k++) { Start-Sleep -Milliseconds 45; $during.Add("$(WRect)") }
}
$dDistinct = @($during | Sort-Object -Unique)
Ptr 300 400; Settle 5000
$dBack = WRect
Write-Host "  resting : ($($dRest.X),$($dRest.Y)) $($dRest.W)x$($dRest.H) bottom=$($dRest.Bottom)"
Write-Host "  expanded: ($($dExp.X),$($dExp.Y)) $($dExp.W)x$($dExp.H) bottom=$($dExp.Bottom)"
Write-Host "  restored: ($($dBack.X),$($dBack.Y)) $($dBack.W)x$($dBack.H) bottom=$($dBack.Bottom)"
Write-Host "  bottom jump resting->expanded = $($dExp.Bottom - $dRest.Bottom) px ; restricted delta = $($dBack.Bottom - $dRest.Bottom) px"
Write-Host "  grew up = $($dRest.Y - $dExp.Y) px, grew wide = $($dExp.W - $dRest.W) px"
Write-Host "  side insets: left = $($dRest.X - $dExp.X) px, right = $(($dExp.X+$dExp.W)-($dRest.X+$dRest.W)) px"
Write-Host "  bounds while moving inside: $($during.Count) readings, $($dDistinct.Count) distinct -> $($dDistinct -join ' | ')"
$report.DynamicBounds = [ordered]@{
    Resting=$dRest; Expanded=$dExp; Restored=$dBack
    BottomJumpPx=$dExp.Bottom-$dRest.Bottom; RestoredDeltaPx=$dBack.Bottom-$dRest.Bottom
    GrewUpPx=$dRest.Y-$dExp.Y; GrewWidePx=$dExp.W-$dRest.W
    LeftInsetPx=$dRest.X-$dExp.X; RightInsetPx=($dExp.X+$dExp.W)-($dRest.X+$dRest.W)
    DuringReadings=$during.Count; DuringDistinct=$dDistinct
}

# ================================================================ 13. leave / reset
Write-Host ''
Write-Host '=== 13. leave / reset ===' -ForegroundColor Cyan
$cycles = @()
foreach ($i in 1..5) {
    Ptr $centreIcon.ScreenX $bandY; Settle
    $on = [pscustomobject]@{ Win="$(WRect)"; Peak=(Motion).Peak; MaxW=(MaxIconW (Icons)) }
    Ptr 300 400; Settle 5000
    $off = [pscustomobject]@{ Win="$(WRect)"; Peak=(Motion).Peak; MaxW=(MaxIconW (Icons)) }
    $cycles += [pscustomobject]@{ Cycle=$i; On=$on; Off=$off }
    Write-Host "  cycle $i : on peak=$($on.Peak) maxW=$($on.MaxW) $($on.Win)  ->  off peak=$($off.Peak) maxW=$($off.MaxW) $($off.Win)"
}
$idle1 = Motion
Start-Sleep -Seconds 5
Pump 300
$idle2 = Motion
Write-Host "  idle 5 s, pointer away: raw $($idle1.Raw) -> $($idle2.Raw) ; updates $($idle1.Updates) -> $($idle2.Updates)"
$report.LeaveReset = [ordered]@{
    Cycles=$cycles; IdleRawDelta=$idle2.Raw-$idle1.Raw; IdleUpdateDelta=$idle2.Updates-$idle1.Updates
}

$ftl = FtlSince
Write-Host ''
Write-Host "=== XAML exceptions this run: FTL=$($ftl.Ftl)  CenterPoint-conflict=$($ftl.CenterPoint) ===" -ForegroundColor Yellow
$report.XamlExceptions = $ftl

$report | ConvertTo-Json -Depth 8 | Set-Content "$OutDir\final-verify.json" -Encoding UTF8
Write-Host "wrote $OutDir\final-verify.json"
