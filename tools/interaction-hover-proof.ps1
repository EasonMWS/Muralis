<#
    SUPERSEDED — do not use this to judge the current candidate.

    This harness drives the pointer with SetCursorPos, which moves the cursor WITHOUT producing a raw input
    report. That was fine while the proof renderer polled the cursor position on a timer, because polling reads
    the position however it got there. The renderer is now event-driven from the process-wide RawPointerBroker
    and never polls, so a harness built on SetCursorPos reports no motion at all — and a report of "the dock did
    not move" from it says nothing about the dock.

    What replaced it, exercising identical ground through the same event-driven path:

      * tools/raw-pointer-proof.ps1  — hover, every icon reachable, peak reaches MaxScale, the pointer leaving
                                       resets the wave, report and drop counters, capture-to-apply latency.
      * tools/drag-reorder-proof.ps1 — click launches and does not reorder; a drag reorders exactly once and
                                       does not launch.

    Kept rather than deleted because it is the record of how the earlier milestone was verified, and because the
    SetCursorPos-versus-mouse_event distinction is worth having written down where someone will find it.
#>
<#
    MILESTONE PROOF (historical) — hover magnification and click launch on a clear-dock candidate.

    Drives the real pointer across the candidate's icons at the positions the dock itself reports, and reads the
    candidate's own stdout to see what it did: the peak magnification the motion engine reached, which icon it
    considered hovered, and whether a click launched anything.

    The peak is the number that matters. The product's engine tops out at 1.8x, so a peak of 1.0 means the wave
    never ran and a peak near 1.8 means the engine was driven properly; anything in between is reported as what
    it is rather than rounded up.

    Usage: ./tools/interaction-hover-proof.ps1 -Exe <path> [-Pins 5] [-Cell 56] [-Pad 12] [-LaunchIndex 1]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Exe,
    [string[]] $AppArgs = @(),
    [int] $Pins = 5,
    [int] $Cell = 56,
    [int] $Pad = 12,
    [int] $LaunchIndex = 1,
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\clear-dock\hover'
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Add-Type -Namespace Hv -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
'@

function Find-Candidate {
    param([int] $Owner)
    $hits = New-Object System.Collections.Generic.List[IntPtr]
    $cb = [Hv.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0
        [void][Hv.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $Owner) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Hv.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -like '*Clear Dock*') { $hits.Add($h) }
        }
        return $true
    }
    [void][Hv.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $hits
}

$so = Join-Path $OutDir 'hover-stdout.txt'
if (Test-Path $so) { Remove-Item $so -Force }
# Caller arguments are placed first so an explicit --paths list picks the apps, while --pins is still supplied as
# the default the candidate honours when it resolves the product's own settings instead.
$runArgs = @($AppArgs) + @('--pins', "$Pins")
$startArgs = @{ FilePath = $Exe; PassThru = $true; RedirectStandardOutput = $so }
if ($runArgs.Count -gt 0) { $startArgs['ArgumentList'] = $runArgs }
$proc = Start-Process @startArgs

$wins = @()
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 300
    if ($proc.HasExited) { throw "candidate exited early (code $($proc.ExitCode))" }
    $wins = @(Find-Candidate -Owner $proc.Id | Where-Object { [Hv.W]::IsWindowVisible($_) })
    if ($wins.Count -gt 0) { break }
}
if ($wins.Count -eq 0) { throw 'no visible candidate window appeared' }
$h = $wins[0]
$r = New-Object Hv.W+RECT
[void][Hv.W]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top

Write-Host "=== MILESTONE PROOF: hover + click ===" -ForegroundColor Cyan
Write-Host "  window: ($($r.Left),$($r.Top)) ${w}x${ht}"

# How many icons the candidate actually drew, read back from its own report. The candidate resolves the pinned
# apps from the product's settings file, so asking for more than are pinned yields fewer — and a harness that
# assumes its request was honoured then fails a correct candidate for not hovering icons that do not exist.
$pins = $Pins
$reported = @(Get-Content $so -ErrorAction SilentlyContinue | Where-Object { $_ -like 'hwnd=*' } | Select-Object -First 1)
$reportedLine = if ($reported.Count -gt 0) { [string]$reported[0] } else { '' }
Write-Host "  candidate reported: $(if ($reportedLine) { $reportedLine } else { '(nothing yet)' })"
if ($reportedLine -match 'pins=(\d+)') {
    $pins = [int]$Matches[1]
}
if ($pins -ne $Pins) { Write-Host "  requested $Pins icons, the candidate drew $pins (that is all that is pinned)" -ForegroundColor Yellow }
if ($pins -lt 1) { throw 'the candidate reported no icons' }

Start-Sleep -Milliseconds 1200
$stampBefore = (Get-Content $so -ErrorAction SilentlyContinue | Measure-Object).Count

# --- hover each icon in turn, then park away from the dock ---
$railY = $r.Top + [int]($ht * 0.72)
for ($i = 0; $i -lt $pins; $i++) {
    $cx = $r.Left + $Pad + ($i * $Cell) + [int]($Cell / 2)
    [void][Hv.W]::SetCursorPos($cx, $railY)
    Start-Sleep -Milliseconds 320
}
# Leave, so the wave is required to settle as well as rise.
[void][Hv.W]::SetCursorPos($r.Left - 200, 300)
Start-Sleep -Milliseconds 700

$lines = @(Get-Content $so -ErrorAction SilentlyContinue)
$newLines = if ($lines.Count -gt $stampBefore) { $lines[$stampBefore..($lines.Count - 1)] } else { @() }

# The peak is a running maximum that the candidate reports only when it rises, so it may be announced before this
# harness starts its window — and then never repeated. Reading the maximum over the whole output is therefore the
# correct reading, not a stale one: the value is monotonic, so the largest line ever printed is the true peak.
# The hover transitions are events rather than a running maximum, so those are read inside the window only.
$peakLines = @($lines | Where-Object { $_ -like 'INTERACTIVE peak=*' })
$peaks = @($peakLines | ForEach-Object { if ($_ -match 'peak=([0-9.]+)') { [double]$Matches[1] } })
$hovers = @($newLines | Where-Object { $_ -like 'INTERACTIVE hover=*' } | ForEach-Object {
    if ($_ -match 'hover=(-?\d+)') { [int]$Matches[1] } })

$maxPeak = if ($peaks.Count) { ($peaks | Measure-Object -Maximum).Maximum } else { 1.0 }
Write-Host "`n  hover magnification:" -ForegroundColor Yellow
Write-Host "    peak samples : $($peaks.Count)  (running maximum; may precede this harness's window)"
Write-Host "    max peak     : $maxPeak"
Write-Host "    engine max   : 1.8 (DockMotionProfile.MaxScale)"
Write-Host "    distinct hovers seen: $(($hovers | Sort-Object -Unique) -join ', ')"

# --- click an icon and see whether it launches ---
$clickX = $r.Left + $Pad + ($LaunchIndex * $Cell) + [int]($Cell / 2)
[void][Hv.W]::SetCursorPos($clickX, $railY)
Start-Sleep -Milliseconds 350
$fgBefore = [Hv.W]::GetForegroundWindow()
[void][Hv.W]::mouse_event([Hv.W]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 90
[void][Hv.W]::mouse_event([Hv.W]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 1200
$fgAfter = [Hv.W]::GetForegroundWindow()

$lines2 = @(Get-Content $so -ErrorAction SilentlyContinue)
$new2 = if ($lines2.Count -gt $lines.Count) { $lines2[$lines.Count..($lines2.Count - 1)] } else { @() }
$launches = @($new2 | Where-Object { $_ -like 'INTERACTIVE launch=*' })
$failed = @($new2 | Where-Object { $_ -like 'INTERACTIVE launchFailed=*' })

Write-Host "`n  click launch:" -ForegroundColor Yellow
Write-Host "    clicked icon : $LaunchIndex at screen x=$clickX"
Write-Host "    launch lines : $($launches.Count)"
$launches | ForEach-Object { Write-Host "      $_" }
$failed | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
Write-Host "    foreground before/after: $fgBefore -> $fgAfter  $(if ($fgBefore -eq $fgAfter) { '(unchanged - did not activate)' } else { '(CHANGED)' })"

# The foreground changing is not itself a failure: clicking a launch target is *supposed* to open that app, and
# an app that opens takes the foreground. What must never happen is the DOCK taking it. The two are told apart by
# the window that ended up in front: the dock's own window, or somebody else's.
$fgIsDock = $fgAfter -eq $h
$fgPid = 0
[void][Hv.W]::GetWindowThreadProcessId($fgAfter, [ref]$fgPid)
$fgIsCandidateProcess = $fgPid -eq $proc.Id
$fgClass = ''
if ($fgAfter -ne [IntPtr]::Zero) {
    $sb = New-Object System.Text.StringBuilder 256
    [void][Hv.W]::GetClassName($fgAfter, $sb, 256)
    $fgClass = $sb.ToString()
}
Write-Host "    window in front afterwards: hwnd=$fgAfter class='$fgClass' pid=$fgPid (candidate pid $($proc.Id))"
Write-Host "    the dock itself in front? $fgIsDock    owned by the candidate process? $fgIsCandidateProcess"
if ($fgBefore -ne $fgAfter) {
    Write-Host "    -> the foreground moved to whatever was launched, not to the dock" -ForegroundColor Green
}

# --- the gate ---
# The hover count is checked against the icons that exist, not the number requested, and the expected set is
# "no hit" plus one distinct hit per icon: hovering each in turn must produce each of those and no others.
$failures = New-Object System.Collections.Generic.List[string]
$expectedHovers = $pins + 1
$distinct = @($hovers | Sort-Object -Unique)
if ($maxPeak -lt 1.7) { $failures.Add("hover magnification peaked at $maxPeak, well below the engine's 1.8") }
if ($hovers.Count -lt $expectedHovers) { $failures.Add("only $($hovers.Count) hover changes for $pins icons; expected at least $expectedHovers (no hit, plus each icon)") }
if ($distinct.Count -lt $expectedHovers) { $failures.Add("only $(($distinct -join ', ')) were ever hovered, so not every icon responded") }
if ($launches.Count -eq 0) { $failures.Add('clicking an icon did not launch it') }
if ($failed.Count -gt 0) { $failures.Add('a launch was attempted and failed') }
# Non-activating is about the dock, not about the app the click was asked to open.
if ($fgIsDock) { $failures.Add('the candidate took the foreground') }
if ($fgIsCandidateProcess -and $fgAfter -ne [IntPtr]::Zero) { $failures.Add("the foreground window belongs to the candidate process (hwnd $fgAfter)") }

Write-Host "`n=== VERDICT ===" -ForegroundColor Cyan
if ($failures.Count -eq 0) {
    Write-Host "  PASS - hover magnifies through the product's engine and a click launches the app." -ForegroundColor Green
    $code = 0
} else {
    Write-Host "  FAIL" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    $code = 1
}

if (-not $proc.HasExited) { $proc.Kill() }
exit $code
