<#
    Stage B native GUI acceptance: pointer-driven magnification, measured on the running dock.

    Every reading comes from outside the process — UI Automation for what the dock actually drew, the window's
    own rectangle for its bounds, and the dock's own profiler log for what the motion engine computed. Nothing
    is inferred from the source.

    The pointer is moved with mouse_event (absolute). SetCursorPos was measured earlier producing no mouse
    messages at all, so an application listening for them sees nothing.

    Two things this script is careful about, because both produce convincing but wrong numbers:
      * the log is read incrementally, never re-parsed. Re-reading it whole on every step stalls the run for
        hundreds of milliseconds and shows up later as latency that the dock never had;
      * every reading waits for the raw report count to stop moving, so a snapshot is of a settled dock and not
        of one that is still catching up with the hand.

    Usage: ./tools/phase4d-stage-b-verify.ps1 -DockHwnd <id> [-SkipEnter]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DockHwnd,
    [switch] $SkipEnter,
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\stageB\final'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$native = Join-Path $env:USERPROFILE '.dsh\profiles\web-computer-test\node_modules\dsh-computer-use-windows\native\publish\DshComputerUse.Native.exe'
if (-not (Test-Path -LiteralPath $native)) { throw "the Computer Use profile is not installed: $native" }

$log = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -Namespace Ver -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll", SetLastError=true)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll", SetLastError=true)] public static extern int GetWindowLongW(IntPtr h, int i);
public const uint MOVE = 0x0001, ABSOLUTE = 0x8000;
public const int GWL_EXSTYLE = -20, WS_EX_TOPMOST = 0x00000008, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
public struct RECT { public int Left, Top, Right, Bottom; }
'@

$screenW = [Ver.W]::GetSystemMetrics(0)
$screenH = [Ver.W]::GetSystemMetrics(1)
$cursorBefore = [System.Windows.Forms.Cursor]::Position

# ---------------------------------------------------------------- incremental log reader
# Read by file position, never by line count. The writer appends while this runs, so a line count taken earlier
# is not a valid starting point later, and the reading it produces is of a snapshot that never existed — which
# is how a working dock can look frozen. Only complete lines are consumed; a half-written line waits for its
# other half.
$script:logPos = 0
$script:logTail = ''
$script:logLines = New-Object System.Collections.Generic.List[string]

function Read-NewLogLines {
    if (-not (Test-Path -LiteralPath $log)) { return }
    try {
        $fs = [System.IO.File]::Open($log, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    }
    catch { return }

    try {
        if ($fs.Length -lt $script:logPos) { $script:logPos = 0; $script:logTail = '' }
        if ($fs.Length -eq $script:logPos) { return }
        $fs.Seek($script:logPos, [System.IO.SeekOrigin]::Begin) | Out-Null
        $buffer = New-Object byte[] ($fs.Length - $script:logPos)
        $read = $fs.Read($buffer, 0, $buffer.Length)
        $script:logPos += $read
        $text = $script:logTail + [System.Text.Encoding]::UTF8.GetString($buffer, 0, $read)
        $parts = $text -split "`n"
        $script:logTail = $parts[-1]
        for ($i = 0; $i -lt $parts.Count - 1; $i++) {
            if ($parts[$i].Length -gt 0) { $script:logLines.Add($parts[$i]) }
        }
    }
    finally { $fs.Dispose() }
}

function Pump-Log([int] $maxWaitMs = 0) {
    $deadline = (Get-Date).AddMilliseconds($maxWaitMs)
    do {
        Read-NewLogLines
        if ($maxWaitMs -le 0) { break }
        Start-Sleep -Milliseconds 40
    } while ((Get-Date) -lt $deadline)
}
function Marks([string] $name) { @($script:logLines | Select-String $name | ForEach-Object { $_.Line | ConvertFrom-Json }) }
function Motion {
    $events = @($script:logLines | Select-String '"motion\.(pointer|rebuild)"' | ForEach-Object { $_.Line | ConvertFrom-Json })
    if (-not $events) { return $null }
    $last = $events[-1]
    [pscustomobject]@{
        Updates = $last.updates; Raw = $last.raw; Msgs = $last.msgs
        Inside = $last.inside; Peak = $last.peak; Reach = $last.reach
        Pointer = $last.pointer; Icons = $last.icons; Sane = $last.sane
        OriginX = $last.originX; OriginY = $last.originY
    }
}

function Move-To([int] $x, [int] $y) {
    [Ver.W]::mouse_event([Ver.W]::MOVE -bor [Ver.W]::ABSOLUTE, [int](($x * 65535) / ($screenW - 1)), [int](($y * 65535) / ($screenH - 1)), 0, [IntPtr]::Zero)
}

function Dock-Rect {
    $r = New-Object Ver.W+RECT
    [Ver.W]::GetWindowRect([IntPtr][int]$DockHwnd, [ref]$r) | Out-Null
    [pscustomobject]@{ X = $r.Left; Y = $r.Top; W = $r.Right - $r.Left; H = $r.Bottom - $r.Top; Bottom = $r.Bottom }
}

function Icon-Rects {
    $obs = (& $native observe --hwnd $DockHwnd --maxElements 600 --outputDir $OutDir 2>&1 | Out-String | ConvertFrom-Json)
    # Only icons the window draws: the Shelf's scroller holds items laid out far outside it and clipped away.
    @($obs.elements) |
        Where-Object { $_.automationId -eq 'DockIconMotion' -and $_.bounds.y -ge 0 -and $_.bounds.y -lt 200 } |
        Sort-Object { $_.bounds.x }
}

# Waits for the dock to stop receiving reports, so a reading is of a settled dock rather than a moving one.
function Wait-Settled([int] $timeoutMs = 2500) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    $stable = 0
    $lastRaw = -1
    while ((Get-Date) -lt $deadline) {
        Pump-Log 60
        $m = Motion
        $raw = if ($m) { $m.Raw } else { -2 }
        if ($raw -eq $lastRaw) { $stable++ } else { $stable = 0; $lastRaw = $raw }
        if ($stable -ge 5) { Start-Sleep -Milliseconds 300; Pump-Log 0; return }
    }
}

$samples = New-Object System.Collections.Generic.List[object]

function Snap([string] $label, [string] $note, [switch] $NoSettle) {
    if (-not $NoSettle) { Wait-Settled }
    Pump-Log 0
    $rect = Dock-Rect
    $m = Motion
    $icons = @(Icon-Rects)
    $heights = @($icons | ForEach-Object { $_.bounds.height })
    $widths = @($icons | ForEach-Object { $_.bounds.width })
    $tops = @($icons | ForEach-Object { $_.bounds.y })
    $xs = @($icons | ForEach-Object { $_.bounds.x })

    $sample = [pscustomobject]@{
        Step       = $label
        Note       = $note
        Time       = (Get-Date).ToString('HH:mm:ss.fff')
        WinX       = $rect.X; WinY = $rect.Y; WinW = $rect.W; WinH = $rect.H; WinBottom = $rect.Bottom
        Updates    = $m.Updates; Raw = $m.Raw; Inside = $m.Inside; Peak = $m.Peak; Reach = $m.Reach
        Pointer    = $m.Pointer; Sane = $m.Sane; EngineIcons = $m.Icons
        Zoomed     = @($icons | Where-Object { $_.bounds.height -ne 52 -or $_.bounds.width -ne 52 }).Count
        MaxH       = if ($heights.Count) { ($heights | Measure-Object -Maximum).Maximum } else { 0 }
        MinTop     = if ($tops.Count) { ($tops | Measure-Object -Minimum).Minimum } else { -1 }
        IconCount  = $icons.Count
        IconXs     = ($xs -join ',')
        IconWidths = (($widths | Sort-Object -Unique) -join '/')
    }

    $samples.Add($sample)
    Write-Host ("  [{0,-20}] win=({1},{2}) {3}x{4} bot={5} | peak={6:N4} reach={7:N2} in={8} zoom={9} maxW={10} top={11} upd={12} raw={13}" -f `
        $label, $rect.X, $rect.Y, $rect.W, $rect.H, $rect.Bottom, $m.Peak, $m.Reach, $m.Inside, $sample.Zoomed, `
        (($widths | Measure-Object -Maximum).Maximum), $sample.MinTop, $m.Updates, $m.Raw)
    return $sample
}

$script:logPos = 0
$script:logTail = ''
Read-NewLogLines
$existing = $script:logLines.Count
$script:logLines.Clear()

# ---------------------------------------------------------------- 1. environment
Write-Host '=== 1. environment ===' -ForegroundColor Cyan
$app = Get-Process -Name Muralis -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $app) { throw 'Muralis is not running' }
$built = (Get-Item $app.Path).LastWriteTime
Write-Host "  process: pid=$($app.Id) started=$($app.StartTime.ToString('HH:mm:ss')) responding=$($app.Responding) threads=$($app.Threads.Count) ws=$([int]($app.WorkingSet64/1MB))MB"
Write-Host "  exe built: $built"
$mode = (Get-Content "$env:LOCALAPPDATA\Muralis\settings.json" -Raw | ConvertFrom-Json).DesktopExperience.Mode
Write-Host "  desktop mode: $mode"
$dockStyle = [Ver.W]::GetWindowLongW([IntPtr][int]$DockHwnd, [Ver.W]::GWL_EXSTYLE)
$dockTopMost = [bool]($dockStyle -band [Ver.W]::WS_EX_TOPMOST)
Write-Host ("  dock exstyle 0x{0:X8}: TOPMOST={1} NOACTIVATE={2} TOOLWINDOW={3}" -f `
    $dockStyle, $dockTopMost, [bool]($dockStyle -band [Ver.W]::WS_EX_NOACTIVATE), [bool]($dockStyle -band [Ver.W]::WS_EX_TOOLWINDOW))

Move-To 300 300
Wait-Settled
$rest = Dock-Rect
$restIcons = @(Icon-Rects)
if ($restIcons.Count -eq 0) { throw 'the dock reports no icons' }

# The rail is the row most of the icons are on. The dock also holds a utility row above it and, in the Shelf's
# scroller, items laid out outside the window — and a band placed by their extremes lands off the icons.
$topCounts = $restIcons | Group-Object { $_.bounds.y } | Sort-Object Count -Descending
$railTop = [int]$topCounts[0].Name
$railIcons = @($restIcons | Where-Object { [Math]::Abs($_.bounds.y - $railTop) -le 4 })
$bandY = $rest.Y + $railTop + 26
Write-Host "  icon rows (top -> count): $(($topCounts | Select-Object -First 4 | ForEach-Object { "$($_.Name):$($_.Count)" }) -join ' ')"
Write-Host "  rail row top=$railTop with $($railIcons.Count) icons; pointer band y=$bandY"

$report = [ordered]@{
    Process = [ordered]@{ Pid = $app.Id; Exe = $app.Path; Built = $built.ToString('o'); Responding = $app.Responding }
    Mode = $mode
    DockExStyle = ('0x{0:X8}' -f $dockStyle)
    DockTopMost = $dockTopMost
    Resting = $rest
    BandY = $bandY
}

# ---------------------------------------------------------------- 2. pointer scale/lift
Write-Host ''
Write-Host '=== 2. pointer-driven scale / lift (A-H) ===' -ForegroundColor Cyan

$usable = @($railIcons | Where-Object { $_.bounds.x -gt $rest.X + 150 -and $_.bounds.x + $_.bounds.width -lt $rest.X + $rest.W - 150 })
$leftC = [int]($usable[0].bounds.x + $usable[0].bounds.width / 2)
$rightC = [int]($usable[-1].bounds.x + $usable[-1].bounds.width / 2)
$mid = [int]($usable.Count / 2)
$centreC = [int]($usable[$mid].bounds.x + $usable[$mid].bounds.width / 2)
$betweenX = [int](($usable[$mid].bounds.x + $usable[$mid].bounds.width / 2 + $usable[$mid + 1].bounds.x + $usable[$mid + 1].bounds.width / 2) / 2)
Write-Host "  icon centres on screen: left=$leftC centre=$centreC right=$rightC between=$betweenX"

Write-Host '  A. slow entry from the left (18 small steps)'
foreach ($step in 1..18) {
    Move-To ($leftC - 360 + ($step * 20)) $bandY
    Start-Sleep -Milliseconds 90
}
Snap 'A-slow-entry' 'slow entry from the left'

Write-Host '  B. parked on the left icon centre'
Move-To $leftC $bandY
Snap 'B-left-icon' 'pointer on the left icon centre'

Write-Host '  C. parked on the centre icon centre'
Move-To $centreC $bandY
Snap 'C-centre-icon' 'pointer on the centre icon centre'

Write-Host '  D. parked on the right icon centre'
Move-To $rightC $bandY
Snap 'D-right-icon' 'pointer on the right icon centre'

Write-Host '  E. parked exactly between two adjacent icons'
Move-To $betweenX $bandY
$betweenSample = Snap 'E-between' 'pointer between two adjacent icons'
$betweenRects = @(Icon-Rects)

# Forty traced passes, not forty teleports: two mouse_event calls back to back are coalesced by the system
# into one position, and a sweep that arrives as a handful of jumps is not a sweep.
Write-Host '  F. fast left to right sweep (12 traced passes)'
for ($i = 0; $i -lt 12; $i++) {
    for ($x = $leftC; $x -le $rightC; $x += 12) { Move-To $x $bandY; Start-Sleep -Milliseconds 6 }
}
Wait-Settled 5000
Snap 'F-left-right' 'fast left-to-right sweep'

Write-Host '  G. fast right to left sweep (12 traced passes)'
for ($i = 0; $i -lt 12; $i++) {
    for ($x = $rightC; $x -ge $leftC; $x -= 12) { Move-To $x $bandY; Start-Sleep -Milliseconds 6 }
}
Wait-Settled 5000
Snap 'G-right-left' 'fast right-to-left sweep'

Write-Host '  H. leave the dock'
Move-To 300 300
Wait-Settled 5000
$afterLeave = Snap 'H-left' 'pointer away again'

# ---------------------------------------------------------------- 3. dynamic bounds
Write-Host ''
Write-Host '=== 3. dynamic HWND bounds ===' -ForegroundColor Cyan
Move-To 300 300
Wait-Settled 5000
$bRest1 = Dock-Rect
Write-Host "  resting : ($($bRest1.X),$($bRest1.Y)) $($bRest1.W)x$($bRest1.H) bottom=$($bRest1.Bottom)"

# Enter and expand, then leave and collapse, watching the window on every step of the way in.
$trace = New-Object System.Collections.Generic.List[string]
for ($x = $bRest1.X + 120; $x -le $centreC; $x += 40) {
    Move-To $x $bandY
    Start-Sleep -Milliseconds 70
    $r = Dock-Rect
    $trace.Add("in x=$x -> $($r.X),$($r.Y) $($r.W)x$($r.H)")
}
Wait-Settled 4000
$bExpanded = Dock-Rect
Write-Host "  expanded: ($($bExpanded.X),$($bExpanded.Y)) $($bExpanded.W)x$($bExpanded.H) bottom=$($bExpanded.Bottom)"
Snap 'bounds-expanded' 'window expanded, pointer on the dock' -NoSettle

# Bounds must not move while the pointer moves inside an already-expanded dock.
$during = New-Object System.Collections.Generic.List[string]
foreach ($dx in -200, -100, 0, 100, 200) {
    Move-To ($centreC + $dx) $bandY
    for ($k = 0; $k -lt 6; $k++) {
        Start-Sleep -Milliseconds 45
        $r = Dock-Rect
        $during.Add("$($r.X),$($r.Y) $($r.W)x$($r.H)")
    }
}
$distinct = @($during | Sort-Object -Unique)
Write-Host "  while moving inside the expanded dock: $($during.Count) readings, $($distinct.Count) distinct bounds"
$distinct | ForEach-Object { Write-Host "    $_" }

# Clipping: the outermost magnified icon must stay inside the window.
Move-To $leftC $bandY
Wait-Settled 3000
$lw = Dock-Rect
$li = @(Icon-Rects)
$leftMargin = $li[0].bounds.x - $lw.X
Move-To $rightC $bandY
Wait-Settled 3000
$rw = Dock-Rect
$ri = @(Icon-Rects)
$rightMargin = ($rw.X + $rw.W) - ($ri[-1].bounds.x + $ri[-1].bounds.width)
Write-Host "  magnified icons stay inside: left margin=$leftMargin px, right margin=$rightMargin px"

Move-To 300 300
Wait-Settled 4000
$bRest2 = Dock-Rect
Write-Host "  resting : ($($bRest2.X),$($bRest2.Y)) $($bRest2.W)x$($bRest2.H) bottom=$($bRest2.Bottom)"
Snap 'bounds-restored' 'pointer away, window collapsed' -NoSettle

$report.Bounds = [ordered]@{
    Resting = $bRest1; Expanded = $bExpanded; Restored = $bRest2
    BottomJump = $bExpanded.Bottom - $bRest1.Bottom
    GrewUp = $bRest1.Y - $bExpanded.Y
    GrewWide = $bExpanded.W - $bRest1.W
    DuringExpand = $distinct
    EntryTrace = $trace
    LeftMarginPx = $leftMargin; RightMarginPx = $rightMargin
}

# ---------------------------------------------------------------- 8. performance
Write-Host ''
Write-Host '=== 8. real runtime performance ===' -ForegroundColor Cyan
# No log reads and no Automation while the sweeps run: a measurement that stalls the app measures the harness.
Pump-Log 0
$pStart = Motion
$slowStart = (Get-Date)
for ($pass = 1; $pass -le 4; $pass++) {
    for ($x = $leftC; $x -le $rightC; $x += 10) { Move-To $x $bandY; Start-Sleep -Milliseconds 12 }
    for ($x = $rightC; $x -ge $leftC; $x -= 10) { Move-To $x $bandY; Start-Sleep -Milliseconds 12 }
}
$slowWall = ((Get-Date) - $slowStart).TotalMilliseconds
for ($pass = 1; $pass -le 10; $pass++) {
    Move-To $leftC $bandY; Start-Sleep -Milliseconds 12
    Move-To $rightC $bandY; Start-Sleep -Milliseconds 12
}
$fastWall = ((Get-Date) - $slowStart).TotalMilliseconds - $slowWall
Wait-Settled 5000
Pump-Log 200
$pEnd = Motion

function Stats($values) {
    $v = @($values | Where-Object { $_ -ne $null } | Sort-Object)
    if ($v.Count -eq 0) { return $null }
    [pscustomobject]@{
        Count = $v.Count
        Min = $v[0]
        P50 = $v[[int][Math]::Floor($v.Count * 0.50)]
        P95 = $v[[int][Math]::Floor([Math]::Min($v.Count - 1, $v.Count * 0.95))]
        P99 = $v[[int][Math]::Floor([Math]::Min($v.Count - 1, $v.Count * 0.99))]
        Max = $v[-1]
    }
}

$pm = @(Marks '"name":"motion\.pointer"')
$deltas = New-Object System.Collections.Generic.List[double]
$updDeltas = New-Object System.Collections.Generic.List[double]
$rawDeltas = New-Object System.Collections.Generic.List[double]
for ($i = 1; $i -lt $pm.Count; $i++) {
    $deltas.Add([Math]::Round($pm[$i].t - $pm[$i - 1].t, 3))
    $updDeltas.Add([double]($pm[$i].updates - $pm[$i - 1].updates))
    $rawDeltas.Add([double]($pm[$i].raw - $pm[$i - 1].raw))
}
$gapStats = Stats $deltas
$updStats = Stats $updDeltas
$rawStats = Stats $rawDeltas
$wallStats = Stats @($pm | ForEach-Object { $_.wall })
$rd = @(Marks '"name":"raw\.dump"')
$lastDump = if ($rd.Count) { $rd[-1] } else { $null }

$updatesPerSecond = if ($slowWall + $fastWall -gt 0) { [Math]::Round(($pEnd.Updates - $pStart.Updates) / (($slowWall + $fastWall) / 1000.0), 1) } else { 0 }
Write-Host ("  raw reports     : {0} -> {1}  (+{2})" -f $pStart.Raw, $pEnd.Raw, ($pEnd.Raw - $pStart.Raw))
Write-Host ("  motion updates  : {0} -> {1}  (+{2})  = {3}/s over {4:N0} ms of sweeps" -f $pStart.Updates, $pEnd.Updates, ($pEnd.Updates - $pStart.Updates), $updatesPerSecond, ($slowWall + $fastWall))
Write-Host ("  pointer marks   : {0}" -f $pm.Count)
Write-Host ("  interval between updates (ms): p50={0:N2} p95={1:N2} p99={2:N2} max={3:N2} min={4:N2}" -f $gapStats.P50, $gapStats.P95, $gapStats.P99, $gapStats.Max, $gapStats.Min)
Write-Host ("  updates per produced mark    : p50={0} p95={1} p99={2} max={3}" -f $updStats.P50, $updStats.P95, $updStats.P99, $updStats.Max)
Write-Host ("  raw reports per mark         : p50={0} p95={1} p99={2} max={3}" -f $rawStats.P50, $rawStats.P95, $rawStats.P99, $rawStats.Max)
Write-Host ("  pointer trace wall (ms)      : p50={0:N3} p95={1:N3} p99={2:N3} max={3:N3}" -f $wallStats.P50, $wallStats.P95, $wallStats.P99, $wallStats.Max)
if ($lastDump) { Write-Host ("  raw input window: registered={0} pumping={1} failure={2} reports={3} messages={4} dispatched={5}" -f $lastDump.registered, $lastDump.pumping, $lastDump.failure, $lastDump.reports, $lastDump.messages, $lastDump.dispatched) }
$report.Performance = [ordered]@{
    RawBefore = $pStart.Raw; RawAfter = $pEnd.Raw
    UpdatesBefore = $pStart.Updates; UpdatesAfter = $pEnd.Updates
    UpdatesPerSecond = $updatesPerSecond
    SweepMs = [Math]::Round($slowWall + $fastWall, 0)
    PointerMarks = $pm.Count
    IntervalMs = $gapStats; UpdatesPerMark = $updStats; RawPerMark = $rawStats; TraceWallMs = $wallStats
    RawWindow = $lastDump
}

# ---------------------------------------------------------------- 9. screenshots
Write-Host ''
Write-Host '=== 9. screenshots ===' -ForegroundColor Cyan
function Shot([string] $name) {
    & $native observe --hwnd $DockHwnd --maxElements 600 --outputDir $OutDir 2>&1 | Out-Null
    $shot = Get-ChildItem "$OutDir\window-*.png" | Sort-Object LastWriteTime | Select-Object -Last 1
    Copy-Item $shot.FullName "$OutDir\stageB-$name.png" -Force
    Write-Host "  stageB-$name.png"
}
Move-To 300 300; Wait-Settled 3000; Shot 'resting'
Move-To $centreC $bandY; Wait-Settled 3000; Shot 'pointer-at-centre'
Move-To $betweenX $bandY; Wait-Settled 3000; Shot 'between-icons'
Move-To $leftC $bandY; Wait-Settled 3000; Shot 'expanded-left-edge'
Move-To $rightC $bandY; Wait-Settled 3000; Shot 'expanded-right-edge'
Move-To 300 300; Wait-Settled 3000; Shot 'restored'

$report.Steps = $samples
$report.BetweenIcons = $betweenRects | Select-Object -First 6 | ForEach-Object { [ordered]@{ X = $_.bounds.x; W = $_.bounds.width; H = $_.bounds.height; Top = $_.bounds.y } }
$report | ConvertTo-Json -Depth 7 | Set-Content "$OutDir\stage-b-verify.json" -Encoding UTF8
Write-Host ''
Write-Host "wrote $OutDir\stage-b-verify.json"

[System.Windows.Forms.Cursor]::Position = $cursorBefore
