<#
    Stage B final acceptance — pointer motion phases.

    Drives real pointer motion (mouse_event MOVE|ABSOLUTE; SetCursorPos generates no motion reports and leaves a
    raw-input dock completely inert) and reports, from the product's own profiler stream, what the dock did.

    The path is printed before it is executed, so the run is reproducible from the report rather than trusted.

    Usage:
      ./tools/p4d-final-motion.ps1 -DockHwnd <id> -Phase sweep  [-Cycles 10]
      ./tools/p4d-final-motion.ps1 -DockHwnd <id> -Phase fast   [-Cycles 20]
      ./tools/p4d-final-motion.ps1 -DockHwnd <id> -Phase stress [-Cycles 50]
      ./tools/p4d-final-motion.ps1 -DockHwnd <id> -Phase leave
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DockHwnd,
    [Parameter(Mandatory = $true)][ValidateSet('sweep', 'fast', 'stress', 'leave')][string] $Phase,
    [int] $Cycles = 10,
    [string] $WorkDir = 'D:\AI\temp\dsh-cu-eval\stageB\final-acceptance'
)

$ErrorActionPreference = 'Stop'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

Add-Type -Namespace Mot -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
public const uint MOVE=0x0001, ABSOLUTE=0x8000;
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
'@
$script:SW = [Mot.W]::GetSystemMetrics(0)
$script:SH = [Mot.W]::GetSystemMetrics(1)
function Ptr([int]$x, [int]$y) {
    [Mot.W]::mouse_event([Mot.W]::MOVE -bor [Mot.W]::ABSOLUTE,
        [int](($x * 65535) / ($script:SW - 1)), [int](($y * 65535) / ($script:SH - 1)), 0, [IntPtr]::Zero)
}
function Marks([string]$n) { @(Get-Content $profLog -ErrorAction SilentlyContinue | Select-String $n | ForEach-Object { $_.Line | ConvertFrom-Json }) }

function Rebuilds {
    $r = @(Marks '"name":"motion\.rebuild"')
    if ($r.Count -eq 0) { return $null }
    $r[-1]
}

# The path is taken from the live window rectangle, never from a profiler record whose age is unknown: a record
# written while the dock was expanded or scrolled would put the pointer on empty pixels and the phase would
# measure nothing at all.
$h = [IntPtr][int]$DockHwnd
$wr = New-Object Mot.W+RECT
if (-not [Mot.W]::GetWindowRect($h, [ref]$wr)) { throw "cannot read window rect for hwnd $DockHwnd" }
$cr = New-Object Mot.W+RECT
[Mot.W]::GetClientRect($h, [ref]$cr) | Out-Null
$pt = New-Object Mot.W+POINT
[Mot.W]::ClientToScreen($h, [ref]$pt) | Out-Null

$clientW = $cr.Right - $cr.Left
$clientH = $cr.Bottom - $cr.Top
# The nearest usable dock interface is a strip of each vertical edge; the icon row is measured from the live
# window rather than assumed, so a resting 110-high client and an expanded 159-high one both land on the row.
$left  = [int]($pt.X + ($clientW * 0.13))
$right = [int]($pt.X + ($clientW * 0.87))
$railY = [int]($pt.Y + ($clientH * 0.46))
$outLeft = [int]($pt.X - 140)
$outRight = [int]($pt.X + $clientW + 140)

Write-Host "=== PHASE $Phase (cycles=$Cycles) ===" -ForegroundColor Cyan
Write-Host '  path:' -ForegroundColor DarkGray
"    window     : ($($wr.Left),$($wr.Top)) $($wr.Right-$wr.Left)x$($wr.Bottom-$wr.Top)"
"    client     : origin ($($pt.X),$($pt.Y)) ${clientW}x${clientH}"
"    railY      : $railY"
"    outLeft    : $outLeft  (outside, left of dock)"
"    minX       : $left"
"    maxX       : $right"
"    outRight   : $outRight (outside, right of dock)"
"    span       : $($right - $left) px"

$before = Rebuilds
"[before] updates=$($before.updates) raw=$($before.raw) icons=$($before.icons) peak=$($before.peak)"

$transitions = 0
$resets = 0
$prevInside = $null

switch ($Phase) {
    'sweep' {
        # outside-left -> very slow full sweep -> outside-right, repeated.
        for ($c = 1; $c -le $Cycles; $c++) {
            Ptr $outLeft $railY; Start-Sleep -Milliseconds 160
            for ($x = $left; $x -le $right; $x += 8) { Ptr $x $railY; Start-Sleep -Milliseconds 18 }
            Ptr $outRight $railY; Start-Sleep -Milliseconds 160
        }
    }
    'fast' {
        # fast left -> right and right -> left, alternating.
        for ($c = 1; $c -le $Cycles; $c++) {
            for ($x = $left; $x -le $right; $x += 48) { Ptr $x $railY }
            Ptr $outRight $railY
            Start-Sleep -Milliseconds 60
            for ($x = $right; $x -ge $left; $x -= 48) { Ptr $x $railY }
            Ptr $outLeft $railY
            Start-Sleep -Milliseconds 60
        }
    }
    'stress' {
        # enter -> hover -> move across several icons -> leave -> settle.
        $mid = [int](($left + $right) / 2)
        for ($c = 1; $c -le $Cycles; $c++) {
            Ptr $outLeft $railY; Start-Sleep -Milliseconds 90
            Ptr $mid $railY;     Start-Sleep -Milliseconds 70
            Ptr ($mid - 60) $railY; Start-Sleep -Milliseconds 50
            Ptr ($mid + 60) $railY; Start-Sleep -Milliseconds 50
            Ptr ($mid + 150) $railY; Start-Sleep -Milliseconds 50
            Ptr $outRight $railY; Start-Sleep -Milliseconds 120
        }
    }
    'leave' {
        Ptr $left $railY; Start-Sleep -Milliseconds 400
        Ptr $outRight $railY
        Start-Sleep -Seconds 3
    }
}

Start-Sleep -Milliseconds 900
$after = Rebuilds
$perf = @(Marks '"name":"motion\.performance"')
$raw  = @(Marks '"name":"motion\.raw"')

Write-Host ''
Write-Host "=== RESULT ($Phase) ===" -ForegroundColor Cyan
"[after]  updates=$($after.updates) raw=$($after.raw) icons=$($after.icons) capacity=$($after.capacity)"
"  peak=$($after.peak) reach=$($after.reach) breakAt=$($after.breakAt) narrowAt=$($after.narrowAt) inside=$($after.inside)"
"  measured bounds: L=$($after.dockLeft) R=$($after.dockRight) T=$($after.dockTop) B=$($after.dockBottom)"
"  updates added  : $($after.updates - $before.updates)   raw reports added: $($after.raw - $before.raw)"

# Participant count over every rebuild this phase, which is the regression under test.
$all = @(Marks '"name":"motion\.rebuild"')
"  rebuild records total : $($all.Count)"
"  icons min/max         : $(($all | Measure-Object -Property icons -Minimum).Minimum) / $(($all | Measure-Object -Property icons -Maximum).Maximum)"
"  capacity min/max      : $(($all | Measure-Object -Property capacity -Minimum).Minimum) / $(($all | Measure-Object -Property capacity -Maximum).Maximum)"
"  breakAt values        : $((@($all | ForEach-Object { $_.breakAt }) | Sort-Object -Unique) -join ', ')"
"  narrowAt values       : $((@($all | ForEach-Object { $_.narrowAt }) | Sort-Object -Unique) -join ', ')"

if ($perf.Count) {
    $q = $perf[-1]
    "  perf : samples=$($q.samples) queued=$($q.queued) applied=$($q.applied) dropped=$($q.dropped)"
    "         p50=$($q.latencyP50Us)us p95=$($q.latencyP95Us)us p99=$($q.latencyP99Us)us avg=$($q.latencyAverageUs)us max=$($q.latencyMaxUs)us"
}
if ($raw.Count) {
    $w = $raw[-1]
    "  raw owner: registered=$($w.registered) consumers=$($w.consumers) elapsed=$($w.elapsed)ms"
}

$all | Select-Object t,icons,capacity,rebuilds,updates,raw,inside,peak,reach,breakAt,narrowAt,dockLeft,dockRight,dockTop,dockBottom |
    Export-Csv -NoTypeInformation -Encoding UTF8 -Path "$WorkDir\rebuilds-$Phase.csv"
"  wrote $WorkDir\rebuilds-$Phase.csv"
