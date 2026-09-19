<#
    Stage B acceptance run for the dock's pointer-driven magnification.

    Drives real pointer movement and reads back, at each step, what the dock actually did: its window geometry,
    the raw report count, the pointer update count, and every icon's rendered rectangle from UI Automation.

    The pointer is moved with mouse_event (absolute). SetCursorPos was tried first and does not generate the
    mouse messages an application listens for, so the dock would see nothing; injected input is
    indistinguishable from a hand on the mouse to the receiving window.

    Usage: ./tools/phase4d-stage-b-acceptance.ps1 -DockHwnd <id>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DockHwnd,
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\stageB\acceptance'
)

$ErrorActionPreference = 'Stop'
$native = Join-Path $env:USERPROFILE '.dsh\profiles\web-computer-test\node_modules\dsh-computer-use-windows\native\publish\DshComputerUse.Native.exe'
if (-not (Test-Path -LiteralPath $native)) { throw "the Computer Use profile is not installed: $native" }

$log = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# The log accumulates across runs, so only the lines this run writes are read. Without this the first reading
# of every run is the previous run's last one, which is worse than no reading: it looks like an answer.
$logSkip = if (Test-Path -LiteralPath $log) { (Get-Content $log).Count } else { 0 }

function Read-Log {
    if (-not (Test-Path -LiteralPath $log)) { return @() }
    @(Get-Content $log | Select-Object -Skip $logSkip)
}

Add-Type -Namespace Acc -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
public const uint MOVE = 0x0001, ABSOLUTE = 0x8000;
public struct RECT { public int Left, Top, Right, Bottom; }
'@

function Move-To([int] $x, [int] $y) {
    $sw = [Acc.W]::GetSystemMetrics(0); $sh = [Acc.W]::GetSystemMetrics(1)
    [Acc.W]::mouse_event([Acc.W]::MOVE -bor [Acc.W]::ABSOLUTE, [int](($x * 65535) / ($sw - 1)), [int](($y * 65535) / ($sh - 1)), 0, [IntPtr]::Zero)
}

function Dock-Rect {
    $r = New-Object Acc.W+RECT
    [Acc.W]::GetWindowRect([IntPtr][int]$DockHwnd, [ref]$r) | Out-Null
    [pscustomobject]@{ X = $r.Left; Y = $r.Top; W = $r.Right - $r.Left; H = $r.Bottom - $r.Top }
}

function Motion-Stats {
    $events = @(Read-Log | Select-String '"motion\.(pointer|rebuild)"' | ForEach-Object { $_.Line | ConvertFrom-Json })
    if (-not $events) { return $null }
    $last = $events[-1]
    [pscustomobject]@{
        Updates = $last.updates
        Raw     = $last.raw
        Icons   = $last.icons
        Inside  = $last.inside
        Peak    = $last.peak
        Reach   = $last.reach
        Inside2 = $last.inside
    }
}

function Icons {
    $obs = (& $native observe --hwnd $DockHwnd --maxElements 500 --outputDir $OutDir 2>&1 | Out-String | ConvertFrom-Json)
    @($obs.elements) | Where-Object { $_.automationId -eq 'DockIconMotion' }
}

function Icon-Report {
    $icons = @(Icons)
    $nonRest = @($icons | Where-Object { $_.bounds.height -ne 52 -or $_.bounds.width -ne 52 -or $_.bounds.y -ne 70 })
    $heights = ($icons | ForEach-Object { $_.bounds.height } | Sort-Object -Unique) -join ','
    $tops = ($icons | ForEach-Object { $_.bounds.y } | Sort-Object -Unique) -join ','
    $maxHeight = 0
    $minTop = [int]::MaxValue
    foreach ($icon in $icons) {
        if ($icon.bounds.height -gt $maxHeight) { $maxHeight = $icon.bounds.height }
        if ($icon.bounds.y -lt $minTop) { $minTop = $icon.bounds.y }
    }

    if ($icons.Count -eq 0) { $minTop = -1 }

    # Icons the window actually draws: the Shelf's scroller holds items laid out far outside the window and
    # clipped away, and their rectangles are not places the pointer can be.
    $onScreen = @($icons | Where-Object { $_.bounds.y -ge 0 -and $_.bounds.y -lt 200 })

    [pscustomobject]@{
        Count     = $icons.Count
        NotRest   = $nonRest.Count
        Heights   = $heights
        Tops      = $tops
        MaxHeight = $maxHeight
        MinTop    = $minTop
        OnScreen  = $onScreen.Count
        AllTops   = @($onScreen | ForEach-Object { $_.bounds.y })
    }
}

$before = Dock-Rect
Write-Host "A/B  resting dock window: ($($before.X),$($before.Y)) $($before.W)x$($before.H)" -ForegroundColor Cyan
$restIcons = Icon-Report
Write-Host "     icons at rest: count=$($restIcons.Count) onScreen=$($restIcons.OnScreen) heights=$($restIcons.Heights) tops=$($restIcons.Tops)"

# Where the dock's icons actually are, taken from the icons themselves. The window reserves room above
# them for the magnification to be drawn in, so the icons do not sit anywhere near its bottom edge.
$iconTop = ($restIcons.AllTops | Measure-Object -Minimum).Minimum
$bandY = $before.Y + [int]($iconTop + 26)
Write-Host "     pointer band y=$bandY (icon box starts at $iconTop)"

Write-Host "=== D: pointer far from the dock ===" -ForegroundColor Cyan
Move-To 300 300
Start-Sleep -Milliseconds 700
Write-Host "     $(Motion-Stats | ConvertTo-Json -Compress)   window: $((Dock-Rect | ConvertTo-Json -Compress))"

Write-Host "=== E/F: pointer enters the dock ===" -ForegroundColor Cyan
$entered = $false
foreach ($step in 1..6) {
    $x = $before.X + 200 + ($step * 60)
    Move-To $x $bandY
    Start-Sleep -Milliseconds 350
    $rect = Dock-Rect
    $stats = Motion-Stats
    Write-Host ("     x={0,-6} window=({1},{2}) {3}x{4}  updates={5} raw={6} inside={7}" -f $x, $rect.X, $rect.Y, $rect.W, $rect.H, $stats.Updates, $stats.Raw, $stats.Inside)
    if (-not $entered -and $rect.H -gt $before.H) {
        $entered = $true
        Write-Host "     -> window expanded to $($rect.W)x$($rect.H)" -ForegroundColor Green
    }
}

if (-not $entered) {
    Write-Host "the window never expanded: the pointer did not reach the dock" -ForegroundColor Red
}

$expanded = Dock-Rect
Write-Host "=== G: pointer to left / centre / right of the dock ===" -ForegroundColor Cyan
$spots = @(
    @{ Name = 'left';   X = $expanded.X + 240 },
    @{ Name = 'centre'; X = $expanded.X + [int]($expanded.W / 2) },
    @{ Name = 'right';  X = $expanded.X + $expanded.W - 240 }
)
foreach ($spot in $spots) {
    Move-To $spot.X $bandY
    Start-Sleep -Milliseconds 400
    $r = Icon-Report
    $s = Motion-Stats
    Write-Host ("     {0,-7} x={1,-6} peak={2,-8} iconsNotAtRest={3,-4} heights={4} tops={5}" -f $spot.Name, $spot.X, $s.Peak, $r.NotRest, $r.Heights, $r.Tops)
    & $native observe --hwnd $DockHwnd --maxElements 500 --outputDir $OutDir 2>&1 | Out-Null
    Copy-Item (Get-ChildItem "$OutDir\window-*.png" | Sort-Object LastWriteTime | Select-Object -Last 1).FullName "$OutDir\spot-$($spot.Name).png" -Force
}

Write-Host "=== I/J: pointer between two icons ===" -ForegroundColor Cyan
$between = @(Icons) | Sort-Object { $_.bounds.x }
if ($between.Count -ge 3) {
    $a = $between[1].bounds; $b = $between[2].bounds
    $midX = $expanded.X + [int](($a.x + $a.width / 2 + $b.x + $b.width / 2) / 2)
    Move-To $midX $bandY
    Start-Sleep -Milliseconds 400
    $icons = @(Icons)
    $left = $icons | Where-Object { $_.bounds.x -eq $a.x } | Select-Object -First 1
    $right = $icons | Where-Object { $_.bounds.x -eq $b.x } | Select-Object -First 1
    Write-Host ("     between x={0}: left h={1} y={2} | right h={3} y={4} | delta={5}" -f `
        $midX, $left.bounds.height, $left.bounds.y, $right.bounds.height, $right.bounds.y, `
        [Math]::Abs($left.bounds.height - $right.bounds.height))
}

Write-Host "=== K/L: fast left-right sweep ===" -ForegroundColor Cyan
$before2 = Motion-Stats
for ($i = 0; $i -lt 24; $i++) {
    $x = if ($i % 2 -eq 0) { $expanded.X + 250 } else { $expanded.X + $expanded.W - 250 }
    Move-To $x $bandY
    Start-Sleep -Milliseconds 45
}
Start-Sleep -Milliseconds 500
$after2 = Motion-Stats
Write-Host ("     updates {0} -> {1} (delta {2})   raw {3} -> {4}" -f $before2.Updates, $after2.Updates, ($after2.Updates - $before2.Updates), $before2.Raw, $after2.Raw)

Write-Host "=== M/N/O: pointer leaves the dock ===" -ForegroundColor Cyan
Move-To 300 300
Start-Sleep -Milliseconds 900
$after = Dock-Rect
$final = Icon-Report
$s = Motion-Stats
Write-Host "     window: ($($after.X),$($after.Y)) $($after.W)x$($after.H)  (resting was $($before.W)x$($before.H))"
Write-Host "     inside=$($s.Inside) peak=$($s.Peak) iconsNotAtRest=$($final.NotRest) heights=$($final.Heights) tops=$($final.Tops)"

Write-Host "=== P: process state ===" -ForegroundColor Cyan
Get-Process -Name Muralis -ErrorAction SilentlyContinue |
    Select-Object Id, ProcessName, @{n = 'Responding'; e = { $_.Responding } }, @{n = 'MainWindow'; e = { $_.MainWindowTitle } } |
    Format-Table -AutoSize
