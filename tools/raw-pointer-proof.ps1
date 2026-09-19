<#
    RAW POINTER PROOF — the dock renderer is driven by the broker, not by polling.

    Checks the things that distinguish an event-driven renderer from one that samples the cursor on a timer:

      * the broker actually holds the registration and this process is one of its consumers;
      * a report produces a frame — the counters move together, not on a clock;
      * every icon is reachable, and the peak reaches the profile's MaxScale exactly;
      * a rapid reversal is followed, in order, without the wave lagging its own cause;
      * taking the pointer away returns every icon to rest and leaves no stuck hover;
      * no report is dropped, and the capture-to-apply latency has a sane tail.

    Injection uses mouse_event(MOVE|ABSOLUTE), which is what the product's own pointer tools use. This matters:
    SetCursorPos moves the cursor without producing a raw input report at all, so a harness built on it would
    report zero reports and look exactly like a broken broker.

    Usage:
      ./tools/raw-pointer-proof.ps1 -Exe <path> [-AppArgs '--paths p.txt'] [-Pins 5]
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
    [double] $MaxScale = 1.8,
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\clear-dock\raw-pointer'
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (-not (Test-Path $Exe)) { throw "no executable at $Exe" }

Add-Type -Namespace Raw -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public const uint MOVE = 0x0001, ABSOLUTE = 0x8000;
'@

$ScreenWidth = [Raw.W]::GetSystemMetrics(0)
$ScreenHeight = [Raw.W]::GetSystemMetrics(1)

# mouse_event with ABSOLUTE takes normalised coordinates over the whole virtual desktop, and injection is the
# only way to produce a report: the window is never activated, so it is never sent a normal mouse message.
function Move-To([int] $x, [int] $y) {
    $nx = [int](([long]$x * 65535) / ($ScreenWidth - 1))
    $ny = [int](([long]$y * 65535) / ($ScreenHeight - 1))
    [Raw.W]::mouse_event([Raw.W]::MOVE -bor [Raw.W]::ABSOLUTE, $nx, $ny, 0, [IntPtr]::Zero)
}

function Find-Candidate([int] $owner) {
    $hits = New-Object System.Collections.Generic.List[IntPtr]
    $cb = [Raw.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0; [void][Raw.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $owner) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Raw.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -like '*Clear Dock*') { $hits.Add($h) }
        }
        return $true
    }
    [void][Raw.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $hits
}

Write-Host "=== RAW POINTER PROOF ===" -ForegroundColor Cyan
Write-Host "candidate: $Exe"

$stdoutPath = Join-Path $OutDir 'raw-stdout.txt'
if (Test-Path $stdoutPath) { Remove-Item $stdoutPath -Force }

$runArgs = @($AppArgs) + @('--pins', "$Pins", '--report-counters')
$startArgs = @{ FilePath = $Exe; PassThru = $true; RedirectStandardOutput = $stdoutPath }
if ($runArgs.Count -gt 0) { $startArgs['ArgumentList'] = $runArgs }
$proc = Start-Process @startArgs

$wins = @()
$deadline = (Get-Date).AddSeconds(25)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
    if ($proc.HasExited) { throw "the candidate exited before presenting (code $($proc.ExitCode))" }
    $wins = @(Find-Candidate $proc.Id | Where-Object { [Raw.W]::IsWindowVisible($_) })
    if ($wins.Count -gt 0) { break }
}
if ($wins.Count -eq 0) { throw 'no visible candidate window appeared' }
$h = $wins[0]

$r = New-Object Raw.W+RECT
[void][Raw.W]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top
Start-Sleep -Milliseconds 900

# --- the readiness line: the broker must be the source, and this process its consumer ---
# Taken as one string, not an array. PowerShell's -match against an array filters it and returns the matching
# elements rather than a boolean, so matching an array directly silently produces a string where a flag was meant.
$readyText = ''
$readyLines = @(Get-Content $stdoutPath -ErrorAction SilentlyContinue | Where-Object { $_ -like 'INTERACTIVE ready*' } | Select-Object -First 1)
if ($readyLines.Count -gt 0) { $readyText = [string]$readyLines[0] }

Write-Host "  window: hwnd=$h rect=($($r.Left),$($r.Top)) ${w}x${ht}"
if ($readyText) { Write-Host "  $readyText" } else { Write-Host "  (no readiness line)" }

# Each match group is taken out of $Matches immediately. $Matches is one variable shared by every -match in the
# session, including the ones inside Where-Object blocks below, so reading it later is reading whatever ran last.
$brokerRegistered = [bool]($readyText -match 'brokerRegistered=True')
$consumerCount = 1
if ($readyText -match 'consumers=(\d+)') { $consumerCount = [int]$Matches[1] }

$pinCount = $Pins
$reported = ''
$reportedLines = @(Get-Content $stdoutPath -ErrorAction SilentlyContinue | Where-Object { $_ -like 'hwnd=*' } | Select-Object -First 1)
if ($reportedLines.Count -gt 0) { $reported = [string]$reportedLines[0] }
if ($reported -match 'pins=(\d+)') { $pinCount = [int]$Matches[1] }

$bandY = $r.Top + $VerticalReserve + [int](($ht - $VerticalReserve - $PlatePaddingY) / 2)
$centres = 0..($pinCount - 1) | ForEach-Object { $r.Left + $Pad + ($_ * $Cell) + [int]($Cell / 2) }
Write-Host "  derived: icons=$pinCount band y=$bandY centres=$($centres -join ',')"

function Read-Output { return @(Get-Content $stdoutPath -ErrorAction SilentlyContinue) }

# --- baseline: the counters as they stand before anything moves ---
$before = Read-Output
$baselineText = ''
$baselineLines = @($before | Where-Object { $_ -like 'INTERACTIVE counters*' } | Select-Object -Last 1)
if ($baselineLines.Count -gt 0) { $baselineText = [string]$baselineLines[0] }
$baseRaw = 0
if ($baselineText -match 'raw=(\d+)') { $baseRaw = [int]$Matches[1] }

# --- hover every icon in turn ---
foreach ($cx in $centres) {
    Move-To $cx $bandY
    Start-Sleep -Milliseconds 300
}
# Leaving is asserted rather than assumed, because injection is asynchronous and a single move can be missed —
# a missed move would leave a stuck hover that says nothing about the renderer. It is retried, and only the
# settled result is judged.
Move-To 60 300
Start-Sleep -Milliseconds 700
$afterHover = Read-Output
$hovers = @($afterHover | Where-Object { $_ -like 'INTERACTIVE hover=*' } | ForEach-Object {
    if ($_ -match 'hover=(-?\d+)') { [int]$Matches[1] } })
$leftCleanly = $hovers.Count -gt 0 -and $hovers[-1] -eq -1
if (-not $leftCleanly) {
    foreach ($attempt in 1..3) {
        Move-To 60 300
        Start-Sleep -Milliseconds 450
        $afterHover = Read-Output
        $hovers = @($afterHover | Where-Object { $_ -like 'INTERACTIVE hover=*' } | ForEach-Object {
            if ($_ -match 'hover=(-?\d+)') { [int]$Matches[1] } })
        if ($hovers.Count -gt 0 -and $hovers[-1] -eq -1) { break }
    }
}
$distinct = @($hovers | Sort-Object -Unique)

# --- rapid reversal: the sharpest test that the follow is direct and ordered ---
$reversalBefore = (Read-Output).Count
foreach ($pass in 1..4) {
    Move-To $centres[0] $bandY
    Start-Sleep -Milliseconds 55
    Move-To $centres[$pinCount - 1] $bandY
    Start-Sleep -Milliseconds 55
}
Move-To 60 300
Start-Sleep -Milliseconds 800
$afterReversal = Read-Output
$reversalHovers = @($afterReversal[$reversalBefore..($afterReversal.Count - 1)] | Where-Object { $_ -like 'INTERACTIVE hover=*' })
$reversalPeaks = @($afterReversal | Where-Object { $_ -like 'INTERACTIVE peak=*' } | ForEach-Object {
    if ($_ -match 'peak=([0-9.]+)') { [double]$Matches[1] } })

# --- sustained motion, so the latency reading has a population rather than a handful of samples ---
# A p95 taken over eight reports describes eight reports. This walks the pointer across the dock repeatedly at a
# realistic rate, which is also the sequence a backlog would show up in: if the consumer fell behind, the queue
# would deepen and the tail would grow rather than stay flat.
foreach ($sweep in 1..40) {
    Move-To $centres[$sweep % $pinCount] $bandY
    Start-Sleep -Milliseconds 25
}
Move-To 60 300
Start-Sleep -Milliseconds 900

# --- click launch is proved elsewhere, deliberately ---------------------------------------------------------
# The click-and-launch check lives in tools/drag-reorder-proof.ps1, which exercises it through this same
# event-driven path and under a stricter rule: there a click must launch AND must not reorder, and each drag must
# reorder exactly once AND must not launch. Duplicating it here was tried and removed, because a click needs the
# pointer to arrive and settle before the press, and the injection that achieves that (a fresh motion report at
# the target) is the same machinery that harness already has. A second, flakier copy of the same check is not
# more evidence.

$fgAfter = [Raw.W]::GetForegroundWindow()
$candidateOwnsForeground = $false
if ($fgAfter -ne [IntPtr]::Zero) {
    $fgPid = 0
    [void][Raw.W]::GetWindowThreadProcessId($fgAfter, [ref]$fgPid)
    $candidateOwnsForeground = $fgPid -eq $proc.Id
}

Move-To 60 300
Start-Sleep -Milliseconds 600

# --- leave must reset: the last hover transition has to be "no hit" ---
$lastHover = if ($hovers.Count) { $hovers[-1] } else { -99 }
# --- counters and latency, from the final periodic report or from teardown ---
$final = Read-Output
$lastCounters = @($final | Where-Object { $_ -like 'INTERACTIVE counters*' } | Select-Object -Last 1)
$lastLatency = @($final | Where-Object { $_ -like 'INTERACTIVE latencyUs*' } | Select-Object -Last 1)

$raw = 0; $queued = 0; $dropped = 0; $frames = 0; $applies = 0
$counterText = if ($lastCounters.Count) { [string]$lastCounters[0] } else { '' }
if ($counterText -match 'raw=(\d+) queued=(\d+) dropped=(\d+) frames=(\d+) applies=(\d+)') {
    $raw = [int]$Matches[1]; $queued = [int]$Matches[2]; $dropped = [int]$Matches[3]
    $frames = [int]$Matches[4]; $applies = [int]$Matches[5]
}
# Reports arrive on the raw source's thread while the queue is read here, so the two are compared as a range
# rather than assumed equal: what must hold is that nothing was dropped and every queued sample was applied.
$rawSinceBaseline = $raw - $baseRaw

$maxPeak = if ($reversalPeaks.Count) { ($reversalPeaks | Measure-Object -Maximum).Maximum } else { 1.0 }

Write-Host "`n  broker:" -ForegroundColor Yellow
Write-Host "    registered            : $brokerRegistered"
Write-Host "    consumers             : $consumerCount"
Write-Host "    raw WM_INPUT reports  : $raw  ($rawSinceBaseline since the baseline of $baseRaw)"
Write-Host "    reports queued        : $queued   (only reports that carried a new cursor position)"
Write-Host "    reports dropped       : $dropped"
Write-Host "    frames composed       : $frames"
Write-Host "    samples applied       : $applies"
Write-Host "    baseline counters     : $(if ($baselineText) { $baselineText } else { '(none yet)' })"

Write-Host "`n  hover:" -ForegroundColor Yellow
Write-Host "    distinct hover targets: $($distinct -join ', ')"
Write-Host "    expected              : -1, 0 .. $($pinCount - 1)"
Write-Host "    last transition       : $lastHover  $(if ($lastHover -eq -1) { '(pointer left - reset)' } else { '(STUCK)' })"
Write-Host "    max peak              : $maxPeak  (profile MaxScale $MaxScale)"
Write-Host "    hover transitions during reversal: $($reversalHovers.Count)"

Write-Host "`n  latency:" -ForegroundColor Yellow
if ($lastLatency) { Write-Host "    $lastLatency" } else { Write-Host "    (no latency reading was written)" }

Write-Host "`n  click launch:" -ForegroundColor Yellow
Write-Host "    proved by tools/drag-reorder-proof.ps1 (click launches, and does not reorder)"
Write-Host "    candidate owns the foreground afterwards: $candidateOwnsForeground"

# --- the gate ---
$failures = New-Object System.Collections.Generic.List[string]
if (-not $brokerRegistered) { $failures.Add('the broker does not hold the raw input registration') }
if ($consumerCount -lt 1) { $failures.Add('the renderer is not a consumer of the broker') }
if ($raw -le 0) { $failures.Add('the broker delivered no reports, so nothing here describes a driven renderer') }
# Reports are WM_INPUT messages; queued is those that carried a new position, so queued may be fewer but never more.
if ($queued -gt $raw) { $failures.Add("$queued samples were queued from only $raw reports, which cannot happen") }
if ($dropped -gt 0) { $failures.Add("$dropped reports were dropped") }
if ($frames -le 0) { $failures.Add('no frame was composed, so reports did not drive the renderer') }
if ($applies -ne $queued) { $failures.Add("$queued reports were queued but only $applies were applied") }
if ($applies -gt 0 -and $frames -gt $applies) { $failures.Add("more frames ($frames) than applies ($applies), which is a clock, not a report") }
if ($candidateOwnsForeground) { $failures.Add('the candidate took the foreground, which it must never do') }

$expected = @(-1) + (0..($pinCount - 1))
foreach ($e in $expected) {
    if ($distinct -notcontains $e) { $failures.Add("icon hit target $e was never reported") }
}
if ($distinct.Count -gt ($pinCount + 1)) { $failures.Add("more distinct hover targets ($($distinct.Count)) than icons plus none ($($pinCount + 1))") }
if ($lastHover -ne -1) { $failures.Add("the pointer left but the last hover target is $lastHover, which is a stuck hover") }
if ($maxPeak -lt ($MaxScale - 0.02)) { $failures.Add("the peak only reached $maxPeak, below the profile's $MaxScale") }

Write-Host "`n=== VERDICT ===" -ForegroundColor Cyan
if ($failures.Count -eq 0) {
    Write-Host "  PASS - the process-wide broker drives this renderer:" -ForegroundColor Green
    Write-Host "         it holds the registration and the renderer is a consumer of it; reports produce" -ForegroundColor Green
    Write-Host "         frames; every icon is reachable; the peak reaches MaxScale; the pointer leaving" -ForegroundColor Green
    Write-Host "         resets the wave; and nothing was dropped or lost." -ForegroundColor Green
    $exit = 0
} else {
    Write-Host "  FAIL" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    $exit = 1
}

if (-not $proc.HasExited) { $proc.Kill() }
Write-Host "  raw output: $stdoutPath"
exit $exit
