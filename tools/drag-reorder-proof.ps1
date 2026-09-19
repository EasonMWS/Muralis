<#
    DRAG REORDER PROOF 鈥?the renderer can carry the product's pinned-app drag semantics.

    Performs real drags with real injected mouse input and checks, from the candidate's own output:

      * a press that does not travel launches, and does not reorder;
      * a drag that travels carries the icon and reports candidate slots in between;
      * the release commits exactly once;
      * the order after the drop is the order the candidate says it produced;
      * the three scripted drags (left to middle, middle to left, left to right) land where they should.

    The order is in memory only. The candidate never writes the product's settings, and this harness checks that
    the settings file's bytes are unchanged across the whole run rather than taking it on trust.

    Usage:
      ./tools/drag-reorder-proof.ps1 -Exe <path> [-AppArgs '--paths p.txt'] [-Pins 5]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Exe,
    [string[]] $AppArgs = @(),
    [int] $Pins = 5,
    [int] $IconBox = 52,
    [int] $Cell = 56,
    [int] $Pad = 12,
    [int] $VerticalReserve = 44,
    [int] $PlatePaddingY = 10,
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\clear-dock\drag'
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (-not (Test-Path $Exe)) { throw "no executable at $Exe" }

Add-Type -Namespace Dg -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public const uint MOVE = 0x0001, ABSOLUTE = 0x8000, LEFTDOWN = 0x0002, LEFTUP = 0x0004;
'@

$SW = [Dg.W]::GetSystemMetrics(0)
$SH = [Dg.W]::GetSystemMetrics(1)

function Move-To([int] $x, [int] $y) {
    [Dg.W]::mouse_event([Dg.W]::MOVE -bor [Dg.W]::ABSOLUTE,
        [int](([long]$x * 65535) / ($SW - 1)), [int](([long]$y * 65535) / ($SH - 1)), 0, [IntPtr]::Zero)
}
function Button-Down { [Dg.W]::mouse_event([Dg.W]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero) }
function Button-Up { [Dg.W]::mouse_event([Dg.W]::LEFTUP, 0, 0, 0, [IntPtr]::Zero) }

function Find-Candidate([int] $owner) {
    $hits = New-Object System.Collections.Generic.List[IntPtr]
    $cb = [Dg.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0; [void][Dg.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $owner) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Dg.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -like '*Clear Dock*') { $hits.Add($h) }
        }
        return $true
    }
    [void][Dg.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $hits
}

$settings = Join-Path $env:LOCALAPPDATA 'Muralis\settings.json'
$settingsBefore = if (Test-Path $settings) { (Get-FileHash $settings -Algorithm SHA256).Hash } else { 'absent' }
$settingsTimeBefore = if (Test-Path $settings) { (Get-Item $settings).LastWriteTimeUtc.Ticks } else { 0 }

Write-Host "=== DRAG REORDER PROOF ===" -ForegroundColor Cyan
Write-Host "candidate: $Exe"
Write-Host "settings guard: $settingsBefore"

$stdoutPath = Join-Path $OutDir 'drag-stdout.txt'
if (Test-Path $stdoutPath) { Remove-Item $stdoutPath -Force }

$runArgs = @($AppArgs) + @('--pins', "$Pins")
$startArgs = @{ FilePath = $Exe; PassThru = $true; RedirectStandardOutput = $stdoutPath }
if ($runArgs.Count -gt 0) { $startArgs['ArgumentList'] = $runArgs }
$proc = Start-Process @startArgs

$wins = @()
$deadline = (Get-Date).AddSeconds(25)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
    if ($proc.HasExited) { throw "the candidate exited before presenting (code $($proc.ExitCode))" }
    $wins = @(Find-Candidate $proc.Id | Where-Object { [Dg.W]::IsWindowVisible($_) })
    if ($wins.Count -gt 0) { break }
}
if ($wins.Count -eq 0) { throw 'no visible candidate window appeared' }
$h = $wins[0]

$r = New-Object Dg.W+RECT
[void][Dg.W]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top
Start-Sleep -Milliseconds 900

$pinCount = $Pins
$line = @(Get-Content $stdoutPath -ErrorAction SilentlyContinue | Where-Object { $_ -like 'hwnd=*' } | Select-Object -First 1)
if ($line.Count -gt 0 -and ([string]$line[0]) -match 'pins=(\d+)') { $pinCount = [int]$Matches[1] }
Write-Host "  window: rect=($($r.Left),$($r.Top)) ${w}x${ht} icons=$pinCount"

$bandY = $r.Top + $VerticalReserve + [int](($ht - $VerticalReserve - $PlatePaddingY) / 2)
$slotX = 0..($pinCount - 1) | ForEach-Object { $r.Left + $Pad + ($_ * $Cell) + [int]($Cell / 2) }
Write-Host "  band y=$bandY  slot x = $($slotX -join ',')"

function Read-Lines { return @(Get-Content $stdoutPath -ErrorAction SilentlyContinue) }

# Everything from a line count onwards. Written once because the two places that need it had it subtly wrong:
# slicing by "the count before" and then reading from that index re-includes the last old line, and reading
# [count..(count-1)] on an unchanged file throws.
function New-Lines([int] $alreadyHad) {
    $lines = Read-Lines
    if ($lines.Count -le $alreadyHad) { return @() }
    return @($lines[$alreadyHad..($lines.Count - 1)])
}

# Wait until the output has stopped growing, then return the settled line count. Every drag's window starts here.
# A window opened while the previous gesture was still being written is how one drag's commit gets attributed to
# the next — the candidate writes asynchronously, so "the count a moment ago" is not a boundary.
function Wait-Quiet([int] $timeoutMs = 3000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    $last = (Read-Lines).Count
    $stableSince = Get-Date
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $now = (Read-Lines).Count
        if ($now -ne $last) {
            $last = $now
            $stableSince = Get-Date
            continue
        }
        if (((Get-Date) - $stableSince).TotalMilliseconds -ge 400) { break }
    }

    return $last
}

function Drag([int] $fromSlot, [int] $toX) {
    $fromX = $slotX[$fromSlot]

    # Settle the pointer on the icon and let the reports land before pressing. Without the wait the press is
    # sometimes resolved at the position the previous move left, which makes a drag read as a click that barely
    # moved — a property of stepped injection, not of the renderer.
    Move-To $fromX $bandY
    Start-Sleep -Milliseconds 300
    Move-To $fromX $bandY
    Start-Sleep -Milliseconds 200

    Button-Down
    Start-Sleep -Milliseconds 220

    # One path, walked once, slowly. An earlier version walked it twice to make sure the renderer got a path —
    # but reports while the button is held are sparse and arrive with some latency, so the second pass's return
    # to the start interleaved with the first pass's tail and the renderer saw a pointer that went out and came
    # back. That is a property of the injection, and it made a correct renderer look unpredictable.
    $steps = 14
    for ($s = 1; $s -le $steps; $s++) {
        $x = [int]($fromX + (($toX - $fromX) * $s / $steps))
        Move-To $x $bandY
        Start-Sleep -Milliseconds 45
    }

    # Hold still at the destination long enough for the last report to be delivered and applied, so the drop
    # slot is settled before the button comes up.
    Start-Sleep -Milliseconds 300
    Button-Up

    # And let the release land before the next gesture begins: a queued report delivered after the release would
    # otherwise be applied at the start of the next drag.
    Start-Sleep -Milliseconds 800
}

function Capture([string] $label) {
    $lines = Read-Lines
    $before = $script:marks.Count
    $script:marks.Add(@{ Label = $label; Lines = $lines })
    return $lines
}

$marks = New-Object System.Collections.Generic.List[object]

# --- A click that never travels must launch and must not reorder -------------------------------------------
# The click is attempted more than once, because a press with no motion at all does not always produce a raw
# report: the reports are driven by movement, so a press on a stationary pointer can be delivered late or not at
# all. Each attempt moves off the icon and back first, which guarantees the reports that carry it. How many
# attempts it took is reported rather than hidden, and if none of them launches, that is a real failure.
$clickLaunches = @()
$clickReorders = @()
$clickAttempts = 0
foreach ($attempt in 1..3) {
    $clickAttempts = $attempt
    $beforeClick = Wait-Quiet
    Move-To ($slotX[0] - 120) ($bandY - 60)
    Start-Sleep -Milliseconds 220
    Move-To $slotX[0] $bandY
    Start-Sleep -Milliseconds 350
    Button-Down
    Start-Sleep -Milliseconds 160
    Button-Up
    Start-Sleep -Milliseconds 900

    $clickNew = New-Lines $beforeClick
    $clickLaunches = @($clickNew | Where-Object { $_ -like 'INTERACTIVE launch=*' })
    $clickReorders = @($clickNew | Where-Object { $_ -like 'INTERACTIVE reorder *' })
    if ($clickLaunches.Count -gt 0) { break }
}

Write-Host "`n  A click (no travel): attempts=$clickAttempts launches=$($clickLaunches.Count) reorders=$($clickReorders.Count)"
$clickLaunches | ForEach-Object { Write-Host "    $_" }

# --- the three scripted drags -------------------------------------------------------------------------------
# The log is read as a whole and walked as a sequence, rather than sliced into a window per drag. Line-window
# slicing was tried first and kept attributing a commit to the neighbouring drag, because the candidate writes
# asynchronously and "the line count a moment ago" is not a boundary. Pairing each button-down with the button-up
# that follows it is bounded by the events themselves, so it cannot drift.
$script = @(
    @{ Name = 'slot 0 -> past slot 2'; From = 0; ToX = $slotX[2] + [int]($Cell * 0.6) },
    @{ Name = 'slot 2 -> past slot 0'; From = 2; ToX = $slotX[0] - [int]($Cell * 0.6) },
    @{ Name = 'slot 0 -> right end'; From = 0; ToX = $slotX[$pinCount - 1] + [int]($Cell * 0.6) }
)

$order = 0..($pinCount - 1)

# Run the drags first, then find where they begin in the log. The boundary is the last line belonging to the
# click phase, located inside the log itself, rather than a line count taken from outside it. A count read from
# outside is a race against the candidate's own writes, and one taken a moment early swallows the first drag's
# button-down — which is exactly what happened and made a correct renderer look like it never committed.
foreach ($step in $script) { Drag $step.From $step.ToX }
Start-Sleep -Milliseconds 400

$all = Read-Lines
$dragStart = 0
for ($i = 0; $i -lt $all.Count; $i++) {
    if ($all[$i] -like 'INTERACTIVE launch=*') { $dragStart = $i + 1 }
}
$segment = if ($all.Count -gt $dragStart) { @($all[$dragStart..($all.Count - 1)]) } else { @() }

$segment | Set-Content -Path (Join-Path $OutDir 'drag-segment.txt') -Encoding UTF8
Write-Host "  segment begins after line $dragStart; $(@($segment | Where-Object { $_ -like 'INTERACTIVE reorder *' }).Count) reorder lines are in it"

# Walk it: every "edge=down" opens a gesture, the matching "edge=up" closes it, and the reorders and launches
# that fall between them belong to that gesture.
# Walk it. Every "edge=down" opens a gesture, the matching "edge=up" marks it closed, and lines that follow
# still belong to it until the next "edge=down" arrives — because the release is written *before* the commit it
# produced, so a parser that closes the gesture on the up edge throws the commit away. That ordering is what the
# renderer does; the parser has to match it rather than the other way round.
$gestures = New-Object System.Collections.Generic.List[object]
$current = $null
$diag = New-Object System.Collections.Generic.List[string]

function New-Gesture([string] $phase, [int] $slot) {
    return [pscustomobject]@{
        DownPhase = $phase
        DownSlot = $slot
        UpPhase = ''
        UpSlot = -99
        Commits = New-Object System.Collections.Generic.List[string]
        Launches = New-Object System.Collections.Generic.List[string]
        Candidates = 0
    }
}

foreach ($line in $segment) {
    if ($line -match 'edge=down phase=(\w+) slot=(-?\d+)') {
        $current = New-Gesture ([string]$Matches[1]) ([int]$Matches[2])
        $gestures.Add($current)
        $diag.Add("OPEN   <- $line")
        continue
    }

    if ($null -eq $current) { $diag.Add("SKIP   <- $line"); continue }

    if ($line -match 'edge=up phase=(\w+) slot=(-?\d+)') {
        $current.UpPhase = [string]$Matches[1]
        $current.UpSlot = [int]$Matches[2]
        $diag.Add("CLOSE  <- $line")
        continue
    }

    if ($line -like 'INTERACTIVE reorder *') {
        [void]$current.Commits.Add($line)
        $diag.Add("COMMIT <- $line  (gesture now has $($current.Commits.Count))")
    }
    elseif ($line -like 'INTERACTIVE launch=*') {
        [void]$current.Launches.Add($line)
        $diag.Add("LAUNCH <- $line")
    }
    elseif ($line -like 'INTERACTIVE candidate*') {
        $current.Candidates = $current.Candidates + 1
    }
}

$diag | Set-Content -Path (Join-Path $OutDir 'drag-parse.txt') -Encoding UTF8

$dragGestures = @($gestures | Where-Object { $_.UpPhase -eq 'Dragging' })
Write-Host "`n  button gestures: $($gestures.Count) total, $($dragGestures.Count) that dragged"
Write-Host "  segment starts at line $dragStart; the gestures found were:"
$gestures | ForEach-Object {
    Write-Host ("    down phase={0} slot={1}  ->  up phase={2} slot={3}  commits={4} launches={5} candidates={6}" -f `
        $_.DownPhase, $_.DownSlot, $_.UpPhase, $_.UpSlot, $_.Commits.Count, $_.Launches.Count, $_.Candidates)
}

$results = New-Object System.Collections.Generic.List[object]
$model = [System.Collections.Generic.List[int]]::new()
foreach ($v in $order) { $model.Add($v) }

for ($g = 0; $g -lt $dragGestures.Count; $g++) {
    $gesture = $dragGestures[$g]
    $orderBefore = ($model -join ',')
    $from = -1; $to = -1; $ordinal = -1; $reportedOrder = ''
    if ($gesture.Commits.Count -gt 0) {
        $text = [string]$gesture.Commits[0]
        if ($text -match 'from=(\d+) to=(\d+) commits=(\d+) order=(\S+)') {
            $from = [int]$Matches[1]
            $to = [int]$Matches[2]
            $ordinal = [int]$Matches[3]
            $reportedOrder = [string]$Matches[4]
        }
    }

    if ($from -ge 0 -and $to -ge 0 -and $from -ne $to) {
        $target = $model[$from]
        $model.RemoveAt($from)
        $model.Insert($to, $target)
    }
    $expected = ($model -join ',')

    $results.Add([pscustomobject]@{
        Name = $(if ($g -lt $script.Count) { $script[$g].Name } else { "drag $g" })
        DownSlot = $gesture.DownSlot
        From = $from
        To = $to
        Ordinal = $ordinal
        Commits = $gesture.Commits.Count
        Candidates = $gesture.Candidates
        Launches = $gesture.Launches.Count
        OrderBefore = $orderBefore
        OrderExpected = $expected
        OrderReported = $reportedOrder
        Match = ($reportedOrder -eq $expected)
        Moved = ($from -ge 0 -and $to -ge 0 -and $from -ne $to)
    })
}

Write-Host "`n  drags:" -ForegroundColor Yellow
$results | ForEach-Object {
    Write-Host ("    {0,-22} released over slot {1}  commits={2} (ordinal {3})  candidates={4} launches={5}" -f $_.Name, $_.To, $_.Commits, $_.Ordinal, $_.Candidates, $_.Launches)
    Write-Host ("      order {0} -> {1}   reported {2}   {3}" -f $_.OrderBefore, $_.OrderExpected, $_.OrderReported, $(if ($_.Match) { 'MATCH' } else { 'MISMATCH' }))
}

# --- settle, then check the settings file is untouched ------------------------------------------------------
Move-To 60 300
Start-Sleep -Milliseconds 600
if (-not $proc.HasExited) { $proc.Kill() }
Start-Sleep -Milliseconds 400

$settingsAfter = if (Test-Path $settings) { (Get-FileHash $settings -Algorithm SHA256).Hash } else { 'absent' }
$settingsTimeAfter = if (Test-Path $settings) { (Get-Item $settings).LastWriteTimeUtc.Ticks } else { 0 }
Write-Host "`n  settings.json: hash $(if ($settingsAfter -eq $settingsBefore) { 'UNCHANGED' } else { "CHANGED ($settingsBefore -> $settingsAfter)" })"

$failures = New-Object System.Collections.Generic.List[string]
if ($clickLaunches.Count -ne 1) { $failures.Add("a click without travel produced $($clickLaunches.Count) launches, expected exactly 1") }
if ($clickReorders.Count -ne 0) { $failures.Add("a click without travel reordered, which it must not") }

foreach ($res in $results) {
    if ($res.Commits -ne 1) { $failures.Add("$($res.Name): $($res.Commits) commits, expected exactly 1") }
    if ($res.Launches -ne 0) { $failures.Add("$($res.Name): the drag launched $($res.Launches) times, which is an accidental launch") }
    if ($res.Candidates -lt 1) { $failures.Add("$($res.Name): no candidate transition was reported, so a drop slot was never chosen") }
    if (-not $res.Moved) { $failures.Add("$($res.Name): the drag did not change slots, so the scripted drag is not testing a reorder") }
    if (-not $res.Match) { $failures.Add("$($res.Name): order came out '$($res.OrderReported)' but one commit from $($res.From) to $($res.To) gives '$($res.OrderExpected)'") }
}
if ($failures.Count -eq 0 -and $results.Count -gt 0) {
    $final = $results[-1].OrderReported
    if ($final -eq '0,1,2,3,4') { $failures.Add('the three drags between them did not change the order at all') }
}
if ($settingsAfter -ne $settingsBefore) { $failures.Add('settings.json changed during the run') }
if ($settingsTimeAfter -ne $settingsTimeBefore) { $failures.Add('settings.json was touched during the run') }

Write-Host "`n=== VERDICT ===" -ForegroundColor Cyan
if ($failures.Count -eq 0) {
    Write-Host "  PASS - the renderer carries the product's drag semantics:" -ForegroundColor Green
    Write-Host "         a press that does not travel still launches and never reorders; a drag carries the" -ForegroundColor Green
    Write-Host "         icon, reports candidate slots and commits exactly once; the resulting order is the" -ForegroundColor Green
    Write-Host "         move that was performed; and the user's settings file was not touched." -ForegroundColor Green
    $exit = 0
} else {
    Write-Host "  FAIL" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    $exit = 1
}

Write-Host "  drag output: $stdoutPath"
exit $exit
